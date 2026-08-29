# iw3-webui

A job queue and browser UI for [iw3](https://github.com/nagadomi/nunif), nunif's
2D → stereo-3D video converter.

iw3 upstream ships a CLI and a wxPython desktop GUI. Neither has a queue, so a
batch of films means either babysitting one conversion at a time or writing a
shell loop and losing all visibility into it. Conversions are long — a 90-minute
4K film is on the order of 18 hours on the hardware this was written for — which
makes "what is it doing and when will it be done" the question that actually
matters.

This gives iw3 a persistent queue with live progress, per-job logs and honest
ETAs, in a container you can put on whatever machine holds the GPU.

## What's in here

| Directory | What it is |
|---|---|
| `container/` | The queue, the web UI and the Dockerfile. Stands alone; needs nothing else. |
| `cove-extension/` | Optional. An **Add to iw3 Queue** button for [Cove](https://github.com/coveapp/cove)'s video detail page, which posts to this queue over HTTP. |

The extension needs the container. The container does not need the extension.

## Features

- **Persistent queue** — SQLite under your config volume. Survives restarts; a
  job that was running when the container died is re-queued rather than lost.
- **Real progress, not a spinner** — iw3 drives tqdm, which already computes
  percentage, frame counts, rate and remaining time. The backend parses those
  rather than inventing its own numbers. The scene-detection pre-pass draws its
  own bar and is deliberately shown as a *separate*, greyed-out phase so it can
  never be mistaken for conversion progress.
- **ETAs for jobs that haven't started**, from throughput measured on *your*
  machine — see [Estimates](#estimates).
- **Live log streaming** over SSE, throttled so a multi-hour job doesn't take
  the browser tab down with it.
- **2-minute preview** — see [Preview](#preview).
- **Settings read from iw3 itself** — the form's fields, defaults and choices
  are introspected from iw3's own `create_parser()` at startup, so they cannot
  drift out of sync with the nunif version in the image.

## Requirements

- Docker
- A GPU passed into the container, or patience (CPU works; it is very slow)
- Somewhere to read source video from, and somewhere to write results to

## Quick start

Build for an Intel Arc GPU (the default, and the only combination tested here —
see [Other GPUs](#other-gpus)):

```sh
cd container
docker build -t iw3-webui .
```

Run it:

```sh
docker run -d --name iw3 \
  --restart unless-stopped \
  -p 8790:8790 \
  --device /dev/dri:/dev/dri:rwm \
  -e PUID=1000 -e PGID=1000 \
  -v /path/to/config:/config \
  -v /path/to/videos:/input:ro \
  -v /path/to/output:/output \
  iw3-webui
```

Then open `http://<host>:8790`.

Check that the GPU actually arrived — this is the one thing a build cannot
verify for you:

```sh
curl -s localhost:8790/api/health
```

An empty `accelerators` list means every conversion will run on the CPU.

### Volumes

| Mount | Purpose |
|---|---|
| `/config` | `NUNIF_HOME`: model checkpoints, the job database, per-job logs |
| `/input` | Your source videos. Mount read-only; the container never writes here. |
| `/output` | Converted files, and `_previews/<job-id>/` for previews |

### Model checkpoints

Several depth checkpoints are CC-BY-NC-4.0 licensed and are **not**
auto-downloaded. On first start the container writes a `README.md` into
`/config` listing the exact filenames and their Hugging Face sources. Models not
on that list (`ZoeD_*`, DepthPro, Depth-Anything v1) download themselves on
first use.

If you pick a model whose checkpoint is missing, iw3 fails with a
`FileNotFoundError` naming the exact path — that is iw3's own behaviour, not a
check added here.

## Preview

The **Preview (2 min clip)** button cuts a two-minute clip from the *middle* of
the source and converts that, with exactly the settings the real job would use.

Two decisions worth explaining:

- **A clip, not stills.** This button used to pass iw3's `--keyframe` and
  produce a handful of images. Stills cannot answer the question a 3D preview
  exists to answer — whether depth stays stable while the picture moves.
  Flicker, and depth bleeding across a hard cut, only show up in motion.
- **The middle, not the start.** Openings are titles, logos and fades often
  enough to be unrepresentative of the film behind them.

The clip is produced by copying the video stream — no re-encode, so what you
judge is the real source. Audio is transcoded to AAC because mp4 will not
accept every audio codec that arrives in an mkv or wmv. If the video codec
itself cannot go into mp4 (`wmv3`, for instance), the clip is re-encoded and the
log says so. The clip is deleted once the preview finishes, and kept if it
fails, so you can look at what iw3 choked on.

Set `PREVIEW_CLIP_SECONDS` to change the length.

## Estimates

Queued jobs get an ETA, and the queue header shows the total. Runtime scales
with *frames processed* — duration × `min(source fps, max_fps)` — not with clip
length, so a 50 fps source costs roughly twice a 25 fps source of the same
running time.

The frames-per-second figures come from **your own finished jobs**, grouped by
depth model and resolution and taken as a median. Until a combination has run on
your machine at least once, a seed value measured on an Intel Arc Pro B60 stands
in. See what is being used:

```sh
curl -s localhost:8790/api/throughput
```

Estimates are shown with a leading `~` in grey. A countdown reported by iw3
itself is shown plain. The two are never mixed.

## Configuration

Everything below is an environment variable on the container.

| Variable | Default | Meaning |
|---|---|---|
| `WEBUI_PORT` | `8790` | Port inside the container |
| `PUID` / `PGID` | `99` / `100` | User/group the process drops to; owns the output files |
| `UMASK` | `000` | umask for created files |
| `IW3_GPU` | `0` | Passed to iw3's `--gpu`. `-1` is CPU; `1` is a second card. |
| `PREVIEW_CLIP_SECONDS` | `120` | Length of the preview clip |
| `FFMPEG_BIN` | `ffmpeg` | ffmpeg used for clip extraction |
| `NUNIF_HOME` | `/config` | Checkpoints, queue database, logs |

Only one job runs at a time regardless. iw3 was not built to share a device
between concurrent conversions.

## Other GPUs

The backend is chosen with build args. `TORCH_REQUIREMENTS` names one of nunif's
own `requirements-torch-*.txt` files, or `none` when the base image already
ships a working torch.

| Target | `TORCH_REQUIREMENTS` | Status |
|---|---|---|
| Intel Arc / XPU | *(defaults — nothing to pass)* | **Tested.** Developed and measured on an Arc Pro B60. |
| NVIDIA / CUDA | `requirements-torch-cu126.txt` | **Untested here** — no NVIDIA hardware to verify on |
| AMD / ROCm | `requirements-torch-rocm.txt` | **Untested here** |
| CPU only | `requirements-torch.txt`, plus `-e IW3_GPU=-1` at run time | **Untested here** |

The default base image is Intel's, and it is the only one that already carries
a matching torch. Any other base also needs `BASE_IMAGE` and `VENV_PATH` — for
example `nvidia/cuda:12.6.3-cudnn-runtime-ubuntu24.04` with `VENV_PATH=/usr`
(the tag exists; whether the build succeeds against it, I do not know).

Two obstacles you will hit on a plain distro base that the Intel image hides,
and that the Dockerfile does not yet solve for you:

- the apt layer installs `python3-dev` but no `python3-pip`
- Ubuntu 24.04 refuses a system-wide `pip install` (PEP 668), so the install
  wants a real virtualenv at `VENV_PATH` rather than `/usr`

I would rather say "untested" than imply a build I have never run works. If you
get one of these going, a PR fixing the Dockerfile and this table is the most
useful thing you could send.

Non-Intel backends also need their own device flags at `docker run` — `--gpus
all` for CUDA, `--device /dev/kfd --device /dev/dri` for ROCm — instead of the
Intel `--device /dev/dri`.

## Security

**There is no authentication.** Anyone who can reach the port can queue jobs,
read logs and browse the directory tree under `/input`. This was built for a
LAN. Put it behind a reverse proxy with auth, or don't expose it.

## Credits

All of the actual conversion is [nagadomi/nunif](https://github.com/nagadomi/nunif).
This repository is a queue and a web front end around `python -m iw3` — it does
not change how iw3 converts anything.

## License

MIT — see [LICENSE](LICENSE). nunif is MIT as well; several depth model
checkpoints are CC-BY-NC-4.0 and are neither redistributed nor auto-downloaded
here.
