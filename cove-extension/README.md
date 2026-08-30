# Add to iw3 Queue — a Cove extension

Puts one button on Cove's video detail page. Clicking it hands that video to
the [iw3 queue](../container) running elsewhere, and reports back the job id.

No conversion happens inside Cove. The extension resolves a Cove video id to a
file path, translates that path into iw3's mount namespace, and POSTs it to
iw3's `/api/jobs`. The job then lives entirely in iw3's queue and is tracked in
iw3's own UI — it does not appear in Cove's job drawer.

Cove and iw3 need not share a Docker network, or a machine. The extension talks
to whatever URL `IW3_WEBUI_URL` names.

## Build

Fetch the reference assemblies first — see [refs/README.md](refs/README.md).

```sh
docker run --rm -v "$PWD":/work -w /work/src/Iw3Queue \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  bash -c "dotnet build -c Release -o /work/out"
```

## Install

Copy three files into a directory of their own under Cove's extension folder:

```sh
mkdir -p /path/to/cove/config/extensions/com.yast2.iw3-queue
cp out/Iw3Queue.dll out/Iw3Queue.deps.json \
   src/Iw3Queue/extension.json \
   /path/to/cove/config/extensions/com.yast2.iw3-queue/
docker restart Cove
```

There is no `frontend/` and no JS bundle: an action with no `handlerName` is
dispatched by Cove straight to the server endpoint, so a button that only makes
one server call needs no client code at all.

Cove logs `iw3 Queue <version> initialised, target <url>, media root <root>,
depth model <model>` on success. Check the media root and model in that line —
it is the cheapest way to catch a misconfiguration before you spend GPU hours
on it.

## Configuration

Environment variables on the **Cove** container.

| Variable | Default | Meaning |
|---|---|---|
| `IW3_WEBUI_URL` | `http://iw3:8790` | Where the iw3 queue answers |
| `IW3_QUEUE_MEDIA_ROOT` | `/media/` | Prefix stripped from Cove's path — see below |
| `IW3_QUEUE_STEREO_FORMAT` | `full_sbs` | Any format iw3's UI offers |
| `IW3_QUEUE_PARAMS` | see below | JSON object, same keys as iw3's own form |

### The media root

Cove and iw3 see the same files under different mount points. `IW3_QUEUE_MEDIA_ROOT`
is the Cove side of that mapping: it is stripped from Cove's stored path, and
what remains is the path relative to iw3's `/input`.

With Cove mounting `/mnt/user:/media` and iw3 mounting `/mnt/user/videos:/input`:

```
Cove path   /media/videos/films/example.mkv
prefix      /media/videos/
sent to iw3 films/example.mkv
```

Stripping the prefix also validates. A video outside that subtree cannot be
reached by iw3 at all, so the click is rejected with `422` and a message naming
both paths — rather than sending a path that would fail somewhere deeper.

### The parameters

The defaults are deliberate, not decoration:

```json
{
  "depth_model": "VDA_B", "divergence": 2.0, "convergence": 0.5,
  "foreground_scale": 0, "edge_dilation": [2, 1],
  "video_codec": "libx265", "pix_fmt": "yuv420p", "max_fps": 1000,
  "scene_detect": true, "ema_normalize": true,
  "ema_decay": 0.75, "ema_buffer": 30
}
```

**Do not replace this with an empty object to "use iw3's defaults".** iw3's web
UI prefills its form from `create_parser()`, but the queue only puts fields that
are *present* into the `python -m iw3` argv. An empty set therefore does not
mean "the form's defaults" — it means iw3's literal CLI defaults, whose depth
model is `ZoeD_Any_N`: the oldest single-frame model in the tree, with scene
detection and EMA normalisation off. That mistake ran 18 jobs and 14 GPU-hours
on the wrong model here before anyone noticed, because the output was perfectly
valid, just worse.

If `IW3_QUEUE_PARAMS` is unparseable or empty, the extension logs a warning and
uses the defaults above rather than sending nothing.

`max_fps: 1000` is a deliberate "no cap": iw3 computes
`output fps = min(source fps, max_fps)` with no unlimited sentinel, and its own
GUI accepts up to 1000. Leaving the default 30 in place silently halves every
50/60 fps source. Keep it at 15 or above — below that, iw3 quietly disables
`ema_normalize`.

## What the button does not do

- **No file picker.** It takes Cove's own `MaxPath` — the file Cove already
  treats as canonical for that video. No second opinion about which file is
  "the" file.
- **No settings dialog.** It is a quick-add. Configure the defaults once, above.
- **No progress in Cove.** The toast reports the iw3 job id; progress lives in
  iw3's UI.

## Permissions

The endpoint requires Cove's `jobs.run` permission — the same one Cove's own
`/api/metadata/generate` requires for starting work.

Cove logs a warning at startup that the endpoint is registered without a Cove
authorization policy. That is expected: the check is made explicitly inside the
handler, the same way Cove's own bundled extensions do it. The warning is about
the declarative form being absent, not about the endpoint being open.
