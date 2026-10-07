#!/usr/bin/env sh
set -eu

INSTALL_BASE="${ECHELON_HOME:-$HOME/.echelon}"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT HUP INT TERM

curl -fsSL https://raw.githubusercontent.com/kemiller2002/praxis/main/scripts/install-native.sh -o "$tmp/install-praxis.sh"
sh "$tmp/install-praxis.sh" --install-base "$INSTALL_BASE"

"$INSTALL_BASE/bin/echelon" install ordo

printf '%s\n' "Echelon engineering toolchain installed."
"$INSTALL_BASE/bin/echelon" doctor
