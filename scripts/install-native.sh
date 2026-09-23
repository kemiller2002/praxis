#!/usr/bin/env sh
set -eu

REPO="kemiller2002/praxis"
VERSION=""
INSTALL_BASE="${ECHELON_HOME:-$HOME/.echelon}"

while [ "$#" -gt 0 ]; do
  case "$1" in
    --version)
      VERSION="${2:-}"
      shift 2
      ;;
    --install-base)
      INSTALL_BASE="${2:-}"
      shift 2
      ;;
    -h|--help)
      echo "Usage: install-native.sh [--version X.Y.Z] [--install-base PATH]"
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      exit 2
      ;;
  esac
done

command -v curl >/dev/null 2>&1 || { echo "curl is required." >&2; exit 1; }
command -v tar >/dev/null 2>&1 || { echo "tar is required." >&2; exit 1; }

if [ -z "$VERSION" ]; then
  VERSION="$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest" |
    sed -n 's/.*"tag_name":[[:space:]]*"v\([^"]*\)".*/\1/p' |
    head -n 1)"
fi

[ -n "$VERSION" ] || { echo "Could not determine the latest Praxis version." >&2; exit 1; }

os="$(uname -s)"
arch="$(uname -m)"
case "$os" in
  Darwin) os_part="osx" ;;
  Linux) os_part="linux" ;;
  *) echo "Unsupported operating system: $os" >&2; exit 3 ;;
esac

case "$arch" in
  x86_64|amd64) arch_part="x64" ;;
  arm64|aarch64) arch_part="arm64" ;;
  *) echo "Unsupported architecture: $arch" >&2; exit 3 ;;
esac

rid="$os_part-$arch_part"
if [ "$rid" = "linux-x64" ]; then
  if [ -f /etc/alpine-release ] || (command -v ldd >/dev/null 2>&1 && ldd --version 2>&1 | grep -qi musl); then
    rid="linux-musl-x64"
  fi
fi

asset="praxis-$rid.tar.gz"
base_url="https://github.com/$REPO/releases/download/v$VERSION"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT HUP INT TERM

curl -fsSL "$base_url/$asset" -o "$tmp/$asset"
curl -fsSL "$base_url/checksums.txt" -o "$tmp/checksums.txt"

expected="$(awk -v file="$asset" '$2 == file { print $1 }' "$tmp/checksums.txt")"
[ -n "$expected" ] || { echo "No checksum found for $asset." >&2; exit 1; }

if command -v sha256sum >/dev/null 2>&1; then
  actual="$(sha256sum "$tmp/$asset" | awk '{print $1}')"
else
  actual="$(shasum -a 256 "$tmp/$asset" | awk '{print $1}')"
fi

[ "$actual" = "$expected" ] || { echo "Checksum verification failed for $asset." >&2; exit 1; }

tar -xzf "$tmp/$asset" -C "$tmp"
source_root="$tmp/praxis-$rid"
tool_root="$INSTALL_BASE/tools/praxis"
target="$tool_root/$VERSION"
current="$tool_root/current"
bin_dir="$INSTALL_BASE/bin"

rm -rf "$target"
mkdir -p "$tool_root" "$bin_dir"
cp -R "$source_root" "$target"
chmod +x "$target/praxis" "$target/praxis-bin" "$target/echelon"

ln -sfn "$target" "$current"
ln -sfn "$current/praxis" "$bin_dir/praxis"
ln -sfn "$current/praxis" "$bin_dir/ros"
ln -sfn "$current/echelon" "$bin_dir/echelon"

printf '%s\n' "Installed Praxis $VERSION to $target"
printf '%s\n' "Commands: $bin_dir/praxis, $bin_dir/ros, and $bin_dir/echelon"
case ":$PATH:" in
  *":$bin_dir:"*) ;;
  *) printf '%s\n' "Add $bin_dir to PATH to invoke Echelon tools from any directory." ;;
esac
