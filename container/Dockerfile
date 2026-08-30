# A queued, browser-driven front end for iw3 (nunif's 2D -> stereo-3D video
# converter).
#
# The GPU backend is chosen entirely through build args - see BUILD.md for the
# per-vendor invocations:
#
#   BASE_IMAGE     a base providing Python and, ideally, a working torch
#   TORCH_INSTALL  pip arguments that install torch, or empty when the base
#                  image already carries the right one
#   VENV_PATH      directory whose bin/ holds the Python to use
#
# There is deliberately no vendor-specific code anywhere below this line, and
# none is needed: nunif resolves the backend itself (cuda -> mps -> xpu, see
# nunif/device.py), so a build differs from another only in which base image
# carries which torch. BUILD.md has the per-vendor invocations.
#
# The defaults below are the Intel Arc / XPU combination this was developed
# and measured on. Base image choice: Intel's own PyTorch-XPU container
# (Ubuntu 24.04,
# torch==2.11.0+xpu), NOT nunif's INSTALL-xpu pin (torch==2.7.1+xpu from
# requirements-torch-xpu.txt). Reasoning:
#   - nunif's own INSTALL-xpu.md was only verified by its author on an Intel
#     Data Center GPU Max 1100 via Tiber Cloud ("I don't own any Intel GPU
#     hardware myself") - it predates confirmed Battlemage support.
#   - Intel's hardware-support docs (dgpu-docs.intel.com) list the Arc Pro B60
#     (PCI E211) as reaching "full support" only on kernel 6.15+ / Ubuntu
#     24.04+. Intel's own xpu-2.11.0-ubuntu24.04 container bundles a
#     compute-runtime validated against exactly that combination.
#   - This is a deliberate deviation from nunif's documented install path,
#     verified empirically (torch.xpu.is_available()) rather than assumed from
#     docs. If it turns out to be wrong, fall back to nunif's pinned
#     torch==2.7.1+xpu instead.
ARG BASE_IMAGE=intel/pytorch:xpu-2.11.0-ubuntu24.04
FROM ${BASE_IMAGE}

ARG TORCH_INSTALL=""
ARG VENV_PATH=/opt/venv

ENV DEBIAN_FRONTEND=noninteractive \
    NUNIF_HOME=/config
# Which Python gets used is decided here and nowhere else. Base images put it
# in different places - Intel's in /opt/venv, PyTorch's official images in
# /opt/conda - and the apt layer below pulls in a system python3 as a
# dependency of python3-dev, so leaving this to chance means sometimes
# installing into the wrong interpreter. Point VENV_PATH at the one that owns
# torch and it stays first.
ENV PATH="${VENV_PATH}/bin:${PATH}"

# git-core, libmagickwand-dev, libraqm-dev, python3-dev: nunif's own
# INSTALL-ubuntu.md system dependencies.
# ffmpeg: general-purpose system ffmpeg for manual use only. iw3 itself never
# shells out to it - it encodes through PyAV (the `av` pip package) directly,
# so this package does NOT give iw3 hardware (QSV/VAAPI) encode. iw3's own
# --video-codec choices are libx264/libx265/libopenh264/utvideo/ffv1 plus
# NVENC (irrelevant here); the depth-inference pass runs on the B60 via XPU,
# the final encode pass runs on CPU via libx265.
# gosu: used by entrypoint.sh to drop from root to PUID/PGID at container start.
RUN apt-get update && apt-get install -y --no-install-recommends \
      git-core \
      libmagickwand-dev \
      libraqm-dev \
      python3-dev \
      ffmpeg \
      gosu \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /opt
# Pinned, not `--depth 1` of whatever HEAD happens to be. An unpinned clone
# makes every rebuild a silent upgrade of the converter itself - including
# rebuilds you only meant to pick up a UI change, and including rebuilds done
# in the middle of a queue that is days deep. Bump this deliberately.
ARG NUNIF_REF=d23721f1b5f0a4c92c3ee1be013180bf298730c5
RUN git clone https://github.com/nagadomi/nunif.git \
    && git -C nunif checkout --detach "$NUNIF_REF" \
    && git -C nunif log -1 --format='nunif pinned at %H (%cd) %s' --date=short

WORKDIR /opt/nunif
# Empty for the XPU and CUDA bases, and deliberately so: both already carry a
# torch that suits them, and installing nunif's requirements-torch-*.txt on top
# would replace it - on the XPU base with an older pin, on the CUDA base with a
# byte-for-byte identical one after a multi-gigabyte download. Bases without
# torch pass the pip arguments that fetch it. See BUILD.md.
# Unquoted on purpose: this is an argument list, not one argument.
RUN if [ -n "$TORCH_INSTALL" ]; then \
        pip install --no-cache-dir $TORCH_INSTALL; \
    fi
RUN pip install --no-cache-dir -r requirements.txt

# Record which torch went in. Whether it can *reach* a GPU cannot be answered
# here - a build host has no device passed in - so that check lives at
# runtime instead: GET /api/health.
RUN python3 -c "import torch; print('built with torch', torch.__version__)"

# Phase 2: browser UI. A small FastAPI app that shells out to `python -m
# iw3` per job (does NOT import iw3.gui - that's a wxPython desktop app with
# no job queue, no persistence, no log streaming; none of what was asked
# for). fastapi/hypercorn/pydantic/starlette are already installed above via
# nunif's own requirements.txt - no new Python dependencies.
COPY webui /opt/iw3-webui

COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

# /config    = NUNIF_HOME (appdata volume: model checkpoints + cache + job queue)
# /input     = read-only source share
# /output    = writable converted-file destination
VOLUME ["/config", "/input", "/output"]

ENV WEBUI_PORT=8790
EXPOSE 8790

ENTRYPOINT ["/entrypoint.sh"]
# docker exec still works for ad hoc Phase 1-style conversions (see USAGE.md)
# independently of the queue - just note that doing so while the queue is
# also running a job means two GPU jobs at once, which iw3 was never
# designed for.
CMD ["sh", "-c", "cd /opt/iw3-webui && exec hypercorn --bind 0.0.0.0:${WEBUI_PORT:-8790} app:app"]
