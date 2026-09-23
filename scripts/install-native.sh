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
base_url="${ECHELON_RELEASE_BASE_URL:-https://github.com/$REPO/releases/download/v$VERSION}"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT HUP INT TERM

curl -fsSL "$base_url/$asset" -o "$tmp/$asset"
curl -fsSL "$base_url/native-checksums.txt" -o "$tmp/native-checksums.txt"

expected="$(awk -v file="$asset" '$2 == file { print $1 }' "$tmp/native-checksums.txt")"
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

mkdir -p "$tool_root" "$bin_dir"
if [ -d "$target" ]; then
  [ -x "$target/praxis" ] && [ -x "$target/praxis-bin" ] && [ -x "$target/echelon" ] && [ -f "$target/VERSION" ] || {
    echo "Existing Praxis $VERSION installation is incomplete. Remove $target and retry." >&2
    exit 1
  }
else
  cp -R "$source_root" "$target"
  chmod +x "$target/praxis" "$target/praxis-bin" "$target/echelon"
fi

if [ -e "$current" ] && [ ! -L "$current" ]; then
  echo "Cannot activate $VERSION because $current exists and is not a symlink." >&2
  exit 1
fi
rm -f "$current"
ln -s "$target" "$current"

for command_name in praxis ros; do
  cat > "$bin_dir/$command_name" <<EOF
#!/usr/bin/env sh
set -eu
tool_root="\${ECHELON_HOME:-\$HOME/.echelon}/tools/praxis/current"
exec "\$tool_root/praxis" "\$@"
EOF
  chmod +x "$bin_dir/$command_name"
done

cat > "$bin_dir/echelon" <<EOF
#!/usr/bin/env sh
set -eu
tool_root="\${ECHELON_HOME:-\$HOME/.echelon}/tools/praxis/current"
exec "\$tool_root/echelon" "\$@"
EOF
chmod +x "$bin_dir/echelon"

printf '%s\n' "Installed Praxis $VERSION to $target"
printf '%s\n' "Commands: $bin_dir/praxis, $bin_dir/ros, and $bin_dir/echelon"
case ":$PATH:" in
  *":$bin_dir:"*) ;;
  *) printf '%s\n' "Add $bin_dir to PATH to invoke Echelon tools from any directory." ;;
esac
