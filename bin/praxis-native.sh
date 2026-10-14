#!/usr/bin/env sh
set -eu
HERE="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
# The self-contained binary embeds its own scaffold payload (DF-ROS-2026-A041).
exec "$HERE/praxis-bin" "$@"
