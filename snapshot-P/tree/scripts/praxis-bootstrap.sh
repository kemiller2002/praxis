#!/usr/bin/env sh
# Deterministic, verified Praxis bootstrap for a machine without Praxis
# (PRAXIS-REMOTE-05, DF-ROS-2026-A041 section 9). Used by the GitHub Actions
# remote-execution adapter and usable by any other executor.
#
#   1. read the version the repository pins in .echelon/toolchain.json;
#   2. download exactly that release's native bundle and checksum list;
#   3. verify the bundle's SHA-256;
#   4. verify its build-provenance attestation (unless explicitly skipped);
#   5. check the unpacked binary reports the pinned version;
#   6. print where it is (and write GitHub step outputs when available).
#
# It never falls forward: a missing pin, version, asset, checksum, or
# attestation, or a version mismatch, is an explicit failure. It never
# builds Praxis from source.
#
# Exit codes: 0 ready; 2 invalid arguments; 3 manifest missing or does not
# pin praxis; 4 release or asset unavailable; 5 integrity or attestation
# failure; 6 version mismatch; 7 unsupported platform.
set -eu

MANIFEST=".echelon/toolchain.json"
INSTALL_DIR="${RUNNER_TOOL_CACHE:-${TMPDIR:-/tmp}}/praxis"
ATTESTATION="required"
REPOSITORY="kemiller2002/praxis"

usage() {
  cat >&2 <<'EOF'
Usage: praxis-bootstrap.sh [--manifest PATH] [--install-dir DIR] [--attestation required|skip]
EOF
}

fail() {
  code="$1"
  shift
  echo "praxis-bootstrap: $*" >&2
  exit "$code"
}

while [ "$#" -gt 0 ]; do
  case "$1" in
    --manifest) MANIFEST="${2:-}"; shift 2 ;;
    --install-dir) INSTALL_DIR="${2:-}"; shift 2 ;;
    --attestation) ATTESTATION="${2:-}"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) usage; fail 2 "unknown argument '$1'" ;;
  esac
done

case "$ATTESTATION" in
  required|skip) ;;
  *) fail 2 "--attestation must be 'required' or 'skip'" ;;
esac

[ -f "$MANIFEST" ] || fail 3 "$MANIFEST not found; the repository must pin the Praxis version it expects"

# Parsed as JSON, not pattern-matched: a malformed manifest or a missing pin
# is a failure, never an invitation to install whatever is latest.
VERSION="$(python3 - "$MANIFEST" <<'PY'
import json, re, sys
try:
    with open(sys.argv[1], encoding="utf-8") as handle:
        manifest = json.load(handle)
except Exception as error:
    sys.exit(f"invalid JSON: {error}")
if not isinstance(manifest, dict) or manifest.get("schemaVersion") != 1:
    sys.exit("unsupported toolchain manifest (expected schemaVersion 1)")
version = manifest.get("praxis")
if not isinstance(version, str) or not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", version):
    sys.exit("does not pin an exact stable praxis version (MAJOR.MINOR.PATCH)")
print(version)
PY
)" || fail 3 "$MANIFEST does not pin a usable Praxis version"

case "$(uname -s)-$(uname -m)" in
  Linux-x86_64) RID="linux-x64" ;;
  Linux-aarch64|Linux-arm64) RID="linux-arm64" ;;
  Darwin-arm64) RID="osx-arm64" ;;
  Darwin-x86_64) RID="osx-x64" ;;
  *) fail 7 "unsupported platform $(uname -s)-$(uname -m)" ;;
esac

ASSET="praxis-$RID.tar.gz"
BASE_URL="${PRAXIS_RELEASE_BASE_URL:-https://github.com/$REPOSITORY/releases/download/v$VERSION}"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/praxis-bootstrap.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT

curl -fsSL "$BASE_URL/native-checksums.txt" -o "$WORK/native-checksums.txt" ||
  fail 4 "Praxis $VERSION has no downloadable checksum list at $BASE_URL; the pinned version may not exist"

EXPECTED="$(awk -v file="$ASSET" '$2 == file { print $1 }' "$WORK/native-checksums.txt")"
[ -n "$EXPECTED" ] || fail 4 "Praxis $VERSION publishes no checksum for $ASSET"

TARGET="$INSTALL_DIR/$VERSION-$EXPECTED"
BINARY="$TARGET/praxis-$RID/praxis"

sha256_of() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | awk '{print $1}'
  else
    shasum -a 256 "$1" | awk '{print $1}'
  fi
}

# A cache hit is keyed by the verified digest and still re-verified: the
# bundle kept next to the unpacked binary must match the published digest.
# A cache entry populated with attestation skipped never satisfies a run
# that requires attestation.
cache_usable() {
  [ -f "$TARGET/$ASSET" ] && [ "$(sha256_of "$TARGET/$ASSET")" = "$EXPECTED" ] && [ -x "$BINARY" ] || return 1
  [ "$ATTESTATION" = "skip" ] || [ "$(cat "$TARGET/.attestation" 2>/dev/null || true)" = "verified" ]
}

if cache_usable; then
  echo "praxis-bootstrap: using cached Praxis $VERSION ($EXPECTED)" >&2
else
  curl -fsSL "$BASE_URL/$ASSET" -o "$WORK/$ASSET" || fail 4 "Praxis $VERSION has no downloadable $ASSET"
  ACTUAL="$(sha256_of "$WORK/$ASSET")"
  [ "$ACTUAL" = "$EXPECTED" ] || fail 5 "checksum mismatch for $ASSET: expected $EXPECTED, got $ACTUAL"

  if [ "$ATTESTATION" = "required" ]; then
    command -v gh >/dev/null 2>&1 || fail 5 "attestation verification requires the GitHub CLI (gh)"
    gh attestation verify "$WORK/$ASSET" --repo "$REPOSITORY" >&2 ||
      fail 5 "no valid build-provenance attestation for $ASSET from $REPOSITORY"
  else
    echo "praxis-bootstrap: attestation verification explicitly skipped for $ASSET" >&2
  fi

  rm -rf "$TARGET"
  mkdir -p "$TARGET"
  tar -xzf "$WORK/$ASSET" -C "$TARGET"
  cp "$WORK/$ASSET" "$TARGET/$ASSET"
  if [ "$ATTESTATION" = "required" ]; then echo verified > "$TARGET/.attestation"; else echo skipped > "$TARGET/.attestation"; fi
fi

[ -x "$BINARY" ] || fail 5 "$ASSET does not contain an executable praxis"

REPORTED="$("$BINARY" --version 2>/dev/null | awk '{print $NF}')" || REPORTED=""
[ "$REPORTED" = "$VERSION" ] || fail 6 "the downloaded binary reports version '$REPORTED', not the pinned $VERSION"

if [ -n "${GITHUB_OUTPUT:-}" ]; then
  {
    echo "praxis=$BINARY"
    echo "version=$VERSION"
    echo "digest=sha256:$EXPECTED"
    echo "attestation=$ATTESTATION"
  } >> "$GITHUB_OUTPUT"
fi

echo "$BINARY"
