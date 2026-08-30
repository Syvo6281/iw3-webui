# Building for your GPU

You probably don't need this file. Prebuilt images are published for every
backend below — see the README. Build yourself if you want a different torch,
a different nunif revision, or a vendor no image exists for.

## The whole idea in one paragraph

There is no vendor-specific code in this project. nunif picks the backend
itself — `cuda`, then `mps`, then `xpu`, see `nunif/device.py` — for any device
id `>= 0`. So a build for one vendor differs from another **only** in which
base image carries which torch. That is what the three build args do, and there
is nothing else to change.

## The matrix

| Backend | `BASE_IMAGE` | `TORCH_INSTALL` | `VENV_PATH` |
|---|---|---|---|
| Intel (XPU) | `intel/pytorch:xpu-2.11.0-ubuntu24.04` | *(empty)* | `/opt/venv` |
| NVIDIA (CUDA) | `pytorch/pytorch:2.7.1-cuda12.6-cudnn9-runtime` | *(empty)* | `/opt/conda` |
| AMD (ROCm) | `rocm/pytorch:rocm6.3_ubuntu24.04_py3.12_pytorch_release_2.4.0` | *(empty)* | `/usr` |
| CPU only | `python:3.12-slim` | `--index-url https://download.pytorch.org/whl/cpu torch==2.7.1 torchvision==0.22.1` | `/usr` |

Intel and CUDA are the tidy cases: both base images already ship a torch that
matches, so nothing is installed over the top. For CUDA the match is exact —
that image carries `torch 2.7.1+cu126`, which is precisely what nunif's own
`requirements-torch-cu126.txt` pins.

`VENV_PATH` is not decoration. The apt layer installs `python3-dev`, which
drags in a system `python3`; if that ends up ahead of the interpreter that owns
torch, the build installs nunif into the wrong Python and the container starts
without torch. Point `VENV_PATH` at the Python that owns torch. Verified for
the CUDA base, whose own `PATH` reads
`/usr/local/nvidia/bin:/usr/local/cuda/bin:/opt/conda/bin:…`.

## Commands

NVIDIA:

```sh
docker build -t iw3-webui:cuda \
  --build-arg BASE_IMAGE=pytorch/pytorch:2.7.1-cuda12.6-cudnn9-runtime \
  --build-arg VENV_PATH=/opt/conda \
  container/
```

Intel — all defaults:

```sh
docker build -t iw3-webui:xpu container/
```

CPU:

```sh
docker build -t iw3-webui:cpu \
  --build-arg BASE_IMAGE=python:3.12-slim \
  --build-arg VENV_PATH=/usr \
  --build-arg TORCH_INSTALL="--index-url https://download.pytorch.org/whl/cpu torch==2.7.1 torchvision==0.22.1" \
  container/
```

Note for the CPU build: nunif's `requirements-torch.txt` is **not** a CPU file.
On Linux it pins `torch==2.7.1+cu128` — it is a CUDA file with a CPU index
listed alongside. Hence the explicit `--index-url` above.

## Running

The device flag is worked out at startup, so there is nothing to configure —
but the container can only see a GPU you actually pass in:

| Backend | run flag |
|---|---|
| NVIDIA | `--gpus all` |
| Intel | `--device /dev/dri:/dev/dri:rwm` |
| AMD | `--device /dev/kfd --device /dev/dri` |
| CPU | *(nothing)* |

Forgetting this is the single most common mistake, and it does not fail — it
runs, on the CPU, at a small fraction of the speed. So the container says so
loudly: a line in the startup log, a banner across the top of the web UI, and
`warning` in `GET /api/health`. Check that first when something is slow.

To pin the device instead of detecting it, set `IW3_GPU`: `0` for the first
accelerator, `1` for the second, `-1` to force the CPU.

## Pinning nunif

`NUNIF_REF` selects the nunif commit. It is pinned rather than tracking HEAD so
that rebuilding to pick up a change here does not silently also upgrade the
converter — which would quietly change what your output looks like.

```sh
docker build --build-arg NUNIF_REF=<sha> container/
```

## What is actually tested

| | builds | converts |
|---|---|---|
| Intel XPU | ✅ | ✅ measured on an Arc Pro B60 |
| NVIDIA CUDA | ✅ in CI | ❓ never run — no NVIDIA hardware here |
| CPU | ✅ in CI | ❓ never run |
| AMD ROCm | ❓ | ❓ |

CI builds every variant on each push, so "the Dockerfile is broken" is caught
without hardware. Whether inference is correct on CUDA or ROCm is a different
question, and one this project cannot answer on its own — reports and PRs
welcome.
