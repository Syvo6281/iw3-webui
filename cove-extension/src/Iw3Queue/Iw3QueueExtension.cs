using Cove.Core.Auth;
using Cove.Data;
using Cove.Plugins;
using Cove.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoveAuthorizationService = Cove.Core.Auth.IAuthorizationService;
using CovePermissions = Cove.Core.Auth.Permissions;

namespace Iw3Queue;

/// <summary>
/// One button on the video detail page: "Add to iw3 Queue". Resolves the video's file via
/// Cove's own MaxPath (the file Cove itself already treats as canonical - no separate
/// "which file" logic here) and hands it to the iw3 web UI's own job queue.
///
/// iw3 (nagadomi/nunif, 2D-to-stereo-3D) runs as its own container with its own FastAPI
/// backend and SQLite job queue. No job runs inside Cove: this extension only translates a
/// Cove video id into a path iw3 understands and POSTs it to iw3's own /api/jobs. The queued
/// job lives entirely in iw3's queue and is tracked in iw3's own UI, not Cove's job drawer.
///
/// Cove and iw3 need not share a Docker network - this talks to iw3 over whatever URL
/// IW3_WEBUI_URL names, which is the same address a browser would use. That is what lets the
/// GPU doing the conversion sit in an entirely different machine from Cove.
///
/// Everything site-specific is an environment variable on the *Cove* container; see the
/// extension's README. Nothing here needs a rebuild to be re-pointed.
/// </summary>
public sealed class Iw3QueueExtension : CoveExtensionBase, IActionExtension, IApiExtension
{
    private const string RunEndpoint = "/api/ext/iw3-queue/enqueue";

    // Cove and iw3 see the same files under different mount points. This prefix is the Cove
    // side of that: it is stripped from Cove's MaxPath, and what remains is the path relative
    // to iw3's /input. Stripping it also validates - a video outside this subtree cannot be
    // reached by iw3 at all and is rejected rather than sent as a wrong path.
    //
    // Example: Cove has /mnt/user:/media and iw3 has /mnt/user/videos:/input, so the prefix
    // is "/media/videos/". Mount both at the same path and it is just "/media/".
    private static readonly string CoveMediaRoot =
        EnsureTrailingSlash(Environment.GetEnvironmentVariable("IW3_QUEUE_MEDIA_ROOT") ?? "/media/");

    private static readonly string Iw3BaseUrl =
        (Environment.GetEnvironmentVariable("IW3_WEBUI_URL") ?? "http://iw3:8790").TrimEnd('/');

    private static readonly string StereoFormat =
        Environment.GetEnvironmentVariable("IW3_QUEUE_STEREO_FORMAT") ?? "full_sbs";

    // One long-lived client, reused across requests - the standard fix for socket exhaustion
    // from a new HttpClient per call. Traffic here is a handful of clicks a day.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    // Sent with every click, and NOT optional. iw3's web UI prefills its form with values
    // introspected from `create_parser()`, but any field absent from params is simply omitted
    // from the `python -m iw3` argv, so iw3 falls back to its literal CLI defaults - and its
    // default depth model is ZoeD_Any_N, the oldest single-frame model in the tree, with scene
    // detection and EMA normalisation off. Sending an empty dict is therefore NOT the same as
    // "leaving the UI form untouched"; it silently downgrades the model. That mistake cost 14
    // GPU-hours here before it was noticed, which is why there is no "just use the defaults"
    // path in this extension at all.
    //
    // Override wholesale with IW3_QUEUE_PARAMS (a JSON object, same keys as iw3's own form).
    private const string DefaultParamsJson = """
    {
      "depth_model": "VDA_B",
      "divergence": 2.0,
      "convergence": 0.5,
      "foreground_scale": 0,
      "edge_dilation": [2, 1],
      "video_codec": "libx265",
      "pix_fmt": "yuv420p",
      "max_fps": 1000,
      "scene_detect": true,
      "ema_normalize": true,
      "ema_decay": 0.75,
      "ema_buffer": 30
    }
    """;
    // On max_fps: iw3 computes `output fps = min(source fps, max_fps)` (iw3/utils.py:1065) - a
    // pure cap with no "unlimited" sentinel. iw3's own GUI accepts 0.25-1000.0, so 1000 keeps
    // the source framerate intact for any real video: a 60 fps source stays 60 fps instead of
    // being halved to 30. It does double the frames to process, and runtime scales with frame
    // count rather than clip length. Keep it >= 15 or iw3 silently disables ema_normalize
    // (iw3/utils.py:979).

    private static readonly string? ParamsWarning;
    private static readonly Dictionary<string, JsonElement> Iw3Params = LoadParams(out ParamsWarning);

    private static string EnsureTrailingSlash(string path) =>
        path.EndsWith('/') ? path : path + "/";

    private static Dictionary<string, JsonElement> LoadParams(out string? warning)
    {
        warning = null;
        var raw = Environment.GetEnvironmentVariable("IW3_QUEUE_PARAMS");
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(raw);
                if (parsed is { Count: > 0 })
                    return parsed;
                warning = "IW3_QUEUE_PARAMS is empty - an empty parameter set makes iw3 fall back "
                        + "to its CLI defaults (ZoeD_Any_N). Using the built-in defaults instead.";
            }
            catch (JsonException ex)
            {
                warning = $"IW3_QUEUE_PARAMS is not valid JSON ({ex.Message}). "
                        + "Using the built-in defaults instead.";
            }
        }
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(DefaultParamsJson)!;
    }

    private ILogger? _log;

    public override Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        _log = services.GetService<ILoggerFactory>()?.CreateLogger("Iw3Queue");
        if (ParamsWarning is not null)
            _log?.LogWarning("iw3 Queue: {Warning}", ParamsWarning);
        _log?.LogInformation(
            "iw3 Queue {Version} initialised, target {Url}, media root {Root}, depth model {Model}.",
            Version, Iw3BaseUrl, CoveMediaRoot,
            Iw3Params.TryGetValue("depth_model", out var m) ? m.ToString() : "(unset)");
        return base.InitializeAsync(services, ct);
    }

    // ---------------------------------------------------------------- UI action

    public IReadOnlyList<ExtensionAction> GetActions() =>
    [
        // Single video detail page only - a bulk button would need to pick a stereo format and
        // model per click, which the "quick add" use case does not ask for. Gated on jobs.run,
        // the same permission Cove's own /api/metadata/generate requires for starting work.
        new ExtensionAction(
            Id: "entity-enqueue",
            Label: "Add to iw3 Queue",
            ExtensionId: Id,
            ActionType: "toolbar",
            EntityTypes: ["video"],
            Icon: "glasses",
            ApiEndpoint: RunEndpoint,
            Order: 100)
        { RequiredPermission = CovePermissions.JobsRun },
    ];

    // ---------------------------------------------------------------- API endpoint

    /// <summary>Payload shape sent by ExtensionEntityActions.</summary>
    private sealed class ActionPayload
    {
        public List<int>? EntityIds { get; set; }
        public List<int>? SelectedIds { get; set; }
    }

    private sealed record Iw3JobRequest(
        string Mode,
        [property: JsonPropertyName("input_path")] string InputPath,
        bool Recursive,
        [property: JsonPropertyName("stereo_format")] string StereoFormat,
        Dictionary<string, JsonElement> Params);

    private sealed record Iw3JobResponse(string? Id);

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Cove applies no authorization to extension endpoints and listens on 0.0.0.0 - same
        // pattern as path-autotag and pv-scheduler: check explicitly, inside the handler.
        endpoints.MapPost(RunEndpoint, async (HttpContext http) =>
        {
            var services = http.RequestServices;

            var principal = services.GetService<ICurrentPrincipalAccessor>()?.Current;
            if (principal is null || principal.Kind == PrincipalKind.Anonymous)
                return Results.Json(new { message = "Not signed in." }, statusCode: StatusCodes.Status401Unauthorized);

            var authorization = services.GetService<CoveAuthorizationService>();
            if (authorization is not null && !authorization.Has(principal, CovePermissions.JobsRun))
                return Results.Json(
                    new { message = $"Missing permission: {CovePermissions.JobsRun}" },
                    statusCode: StatusCodes.Status403Forbidden);

            ActionPayload? payload;
            try
            {
                payload = await http.Request.ReadFromJsonAsync<ActionPayload>(http.RequestAborted);
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "iw3 Queue: unreadable payload.");
                return Results.Json(new { message = "Invalid request." }, statusCode: StatusCodes.Status400BadRequest);
            }

            var videoId = (payload?.EntityIds ?? payload?.SelectedIds ?? []).FirstOrDefault();
            if (videoId <= 0)
                return Results.Json(new { message = "No video selected." }, statusCode: StatusCodes.Status400BadRequest);

            var db = services.GetRequiredService<CoveContext>();
            var video = await db.Videos.AsNoTracking()
                .Where(v => v.Id == videoId)
                .Select(v => new { v.Id, v.Title, v.MaxPath })
                .FirstOrDefaultAsync(http.RequestAborted);

            if (video is null)
                return Results.Json(new { message = $"Video {videoId} not found." }, statusCode: StatusCodes.Status404NotFound);
            if (string.IsNullOrEmpty(video.MaxPath))
                return Results.Json(new { message = "Video has no file." }, statusCode: StatusCodes.Status400BadRequest);
            if (!video.MaxPath.StartsWith(CoveMediaRoot, StringComparison.Ordinal))
                return Results.Json(new
                {
                    message = $"Video file is outside iw3's input root (needs {CoveMediaRoot}..., got {video.MaxPath})",
                }, statusCode: StatusCodes.Status422UnprocessableEntity);

            var relativePath = video.MaxPath[CoveMediaRoot.Length..];

            var request = new Iw3JobRequest(
                Mode: "convert",
                InputPath: relativePath,
                Recursive: false,
                StereoFormat: StereoFormat,
                // Copy per request: the record is handed to the serialiser and must not expose
                // the shared static for mutation.
                Params: new Dictionary<string, JsonElement>(Iw3Params));

            HttpResponseMessage response;
            try
            {
                response = await Http.PostAsJsonAsync($"{Iw3BaseUrl}/api/jobs", request, http.RequestAborted);
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "iw3 Queue: could not reach {Url}.", Iw3BaseUrl);
                return Results.Json(new { message = $"iw3 web UI unreachable at {Iw3BaseUrl}: {ex.Message}" },
                    statusCode: StatusCodes.Status502BadGateway);
            }

            var body = await response.Content.ReadAsStringAsync(http.RequestAborted);
            if (!response.IsSuccessStatusCode)
            {
                _log?.LogWarning("iw3 Queue: iw3 rejected {Path}: {Status} {Body}", relativePath, response.StatusCode, body);
                return Results.Json(new { message = $"iw3 rejected the job: {body}" },
                    statusCode: StatusCodes.Status502BadGateway);
            }

            string? jobId = null;
            try { jobId = JsonSerializer.Deserialize<Iw3JobResponse>(body)?.Id; }
            catch (JsonException) { /* fall through with jobId null - the queue call still succeeded */ }

            _log?.LogInformation("iw3 Queue: video {VideoId} ({Path}) queued as iw3 job {JobId}.",
                videoId, relativePath, jobId);

            return Results.Json(new
            {
                jobId,
                description = $"Queued for iw3: {video.Title ?? relativePath}",
            });
        });
    }
}
