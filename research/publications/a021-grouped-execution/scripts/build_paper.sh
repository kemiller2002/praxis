#!/usr/bin/env sh
# Build both manuscripts into build/paper/ (git-ignored): paper.pdf (full, 10+2) and paper-short.pdf (6 incl. refs).
# Usage: scripts/build_paper.sh [--check]   (--check runs check_manuscript.py after building, including page limits)
set -eu
PKG="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$PKG/build/paper"
mkdir -p "$OUT"
cd "$PKG/manuscript"
for doc in paper paper-short; do
  TEXINPUTS="$PKG/manuscript//:" BIBINPUTS="$PKG:" \
    latexmk -pdf -interaction=nonstopmode -halt-on-error -outdir="$OUT" "$doc.tex"
  echo "PDF: $OUT/$doc.pdf"
  if command -v pdfinfo >/dev/null 2>&1; then pdfinfo "$OUT/$doc.pdf" | grep -E '^(Pages|Author|Title):'; fi
done
if [ "${1:-}" = "--check" ]; then
  python3 "$PKG/scripts/check_manuscript.py"
fi
