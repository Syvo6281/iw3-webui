# Converting a file

The queue is the normal way to run a conversion. This file covers the other
way: `docker exec` straight into the running container, for one-off runs and
for trying flags the UI does not expose.

Use the same PUID/PGID you started the container with (the examples below use
99/100).

```
docker exec -u 99:100 iw3 python3 -m iw3 \
  -i /input/<path-to-file> \
  -o /output \
  --depth-model VDA_L \
  --divergence 2.0 \
  --convergence 0.5 \
  --edge-dilation 2 \
  --scene-detect \
  --ema-normalize \
  --video-codec libx265 \
  --gpu 0 \
  -y
```

Output lands at `/output/<original-filename>_LRF_Full_SBS.mp4` — the exact
suffix Quest players use for stereo-format auto-detection.

## Every flag, explained

| Flag | Value | Why |
|---|---|---|
| `-u 99:100` | (docker exec flag, not iw3's) | **Required.** `docker exec` bypasses `entrypoint.sh`'s PUID/PGID drop and runs as root by default. Without it, output files (and any new torch.hub cache entries) land root-owned and can silently block later runs as the unprivileged user. |
| `-i` | path under `/input` | Source file. `/input` is the torrent share, mounted read-only. |
| `-o` | `/output` (a **directory**, never a filename) | iw3 auto-names the output `{original}_LRF_Full_SBS.mp4` only when `-o` is a directory. Passing a filename skips the naming convention Quest players rely on for format auto-detection. |
| `--depth-model VDA_L` | Video-Depth-Anything Large (the queue defaults to `VDA_B`, roughly twice as fast for a small quality difference) | Temporally consistent depth estimation across frames — unlike single-frame models (e.g. `Any_V2_L`), it doesn't independently re-guess depth every frame, so it doesn't flicker on video. Use `VDA_Metric_L` instead if the scene reads as flat or over-curved — it estimates absolute-scale depth rather than relative. |
| `--divergence 2.0` | iw3 default | 3D strength / simulated eye separation. Higher = more pop, more eye strain. 2.0 is iw3's own moderate default; lower it (e.g. 1.0–1.5) for less aggressive depth. |
| `--convergence 0.5` | iw3 default | Where the "screen plane" sits in depth. 0.5 pulls part of the scene in front of the screen; 0 keeps everything behind it. |
| `--edge-dilation 2` | iw3 default | Expands foreground edges before warping, to hide the gap left where the background is revealed behind a moved foreground object. |
| `--scene-detect` | on | Re-runs depth estimation from scratch at hard cuts instead of carrying state across them — without this, VDA's temporal consistency can bleed depth across a cut and produce a false sense of continuity. Matters for movies specifically. |
| `--ema-normalize` | on | Exponential-moving-average smoothing of the per-frame depth scale. VDA's absolute depth scale jitters slightly frame to frame; this kills the resulting flicker. Recommended whenever using a VDA model (per nunif's own docs). |
| `--video-codec libx265` | software HEVC | The only hardware-relevant option here — iw3 has no VAAPI/QSV encode path (it encodes via PyAV directly, not a system ffmpeg subprocess); this is CPU-side regardless. Depth inference is what runs on the GPU. |
| `--gpu 0` | device index | `-1` selects the CPU; `1` a second card. The queue passes whatever `IW3_GPU` is set to. |
| `-y` | overwrite without prompting | Needed for unattended/batch runs. |

## Batch conversion

`-i` also accepts a directory with `--recursive` to process a whole folder
unattended, one file at a time (the GPU can't be shared across concurrent
jobs — iw3 processes sequentially regardless):

```
docker exec -u 99:100 iw3 python3 -m iw3 \
  -i /input/<folder> -o /output --recursive --skip-error \
  --depth-model VDA_L --divergence 2.0 --convergence 0.5 --edge-dilation 2 \
  --scene-detect --ema-normalize --video-codec libx265 --gpu 0 -y
```

`--skip-error` keeps a bad file from aborting the whole batch.
