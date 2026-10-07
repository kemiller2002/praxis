#!/usr/bin/env sh
# Build the manuscript PDF into build/paper/ (git-ignored).
# Usage: scripts/build_paper.sh [--check]   (--check also runs check_manuscript.py first)
set -eu
PKG="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$PKG/build/paper"
mkdir -p "$OUT"
if [ "${1:-}" = "--check" ]; then
  python3 "$PKG/scripts/check_manuscript.py"
fi
cd "$PKG/manuscript"
TEXINPUTS="$PKG/manuscript//:" BIBINPUTS="$PKG:" \
  latexmk -pdf -interaction=nonstopmode -halt-on-error -outdir="$OUT" paper.tex
echo "PDF: $OUT/paper.pdf"
if command -v pdfinfo >/dev/null 2>&1; then pdfinfo "$OUT/paper.pdf" | grep -E '^(Pages|Author|Title):'; fi
