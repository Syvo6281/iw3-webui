#!/bin/bash
set -euo pipefail

PUID="${PUID:-99}"
PGID="${PGID:-100}"
umask "${UMASK:-000}"

if getent group "$PGID" >/dev/null 2>&1; then
    GROUP_NAME="$(getent group "$PGID" | cut -d: -f1)"
else
    groupadd -o -g "$PGID" iw3
    GROUP_NAME="iw3"
fi

if id -u iw3 >/dev/null 2>&1; then
    usermod -o -u "$PUID" -g "$PGID" iw3
else
    useradd -o -u "$PUID" -g "$PGID" -M -s /bin/bash iw3
fi

CHECKPOINT_DIR="$NUNIF_HOME/iw3/pretrained_models/hub/checkpoints"
mkdir -p "$CHECKPOINT_DIR"

README="$NUNIF_HOME/README.md"
if [ ! -f "$README" ]; then
    cat > "$README" <<'EOF'
# iw3 model checkpoints

Several depth checkpoints iw3 can use are CC-BY-NC-4.0 licensed and are NOT
auto-downloaded. Download them yourself and place them at these exact paths
under this appdata volume (NUNIF_HOME):

    iw3/pretrained_models/hub/checkpoints/depth_anything_v2_vitb.pth
    iw3/pretrained_models/hub/checkpoints/depth_anything_v2_vitl.pth
    iw3/pretrained_models/hub/checkpoints/video_depth_anything_vitl.pth
    iw3/pretrained_models/hub/checkpoints/metric_video_depth_anything_vitl.pth

Source repos (Hugging Face):

    depth_anything_v2_vitb.pth        -> huggingface.co/depth-anything/Depth-Anything-V2-Base
    depth_anything_v2_vitl.pth        -> huggingface.co/depth-anything/Depth-Anything-V2-Large
    video_depth_anything_vitl.pth     -> huggingface.co/depth-anything/Video-Depth-Anything-Large
    metric_video_depth_anything_vitl.pth -> huggingface.co/depth-anything/Metric-Video-Depth-Anything-Large

All four are licensed CC-BY-NC-4.0 (non-commercial). Download the .pth file
directly from each repo's "Files" tab and place it at the path above -
filenames must match exactly.

Models that are NOT in this list (e.g. ZoeD_*, older Depth-Anything v1,
DepthPro) auto-download on first use into this same checkpoints/ directory -
nothing to do for those.

If you select a model via --depth-model whose checkpoint is missing here,
iw3 fails with a clear FileNotFoundError naming the exact missing path -
this is iw3's own behavior, not a custom check.
EOF
fi

chown "$PUID:$PGID" "$NUNIF_HOME" "$NUNIF_HOME/iw3" \
    "$NUNIF_HOME/iw3/pretrained_models" \
    "$NUNIF_HOME/iw3/pretrained_models/hub" \
    "$CHECKPOINT_DIR" "$README" 2>/dev/null || true

exec gosu "$PUID:$PGID" "$@"
