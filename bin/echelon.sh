#!/usr/bin/env sh
set -eu

HOME_DIR="${ECHELON_HOME:-$HOME/.echelon}"
BIN_DIR="$HOME_DIR/bin"

usage() {
  cat <<'EOF'
Usage:
  echelon setup
  echelon upgrade
  echelon install ordo [VERSION]
  echelon install praxis [VERSION]
  echelon doctor
  echelon version
EOF
}

install_ordo() {
  version="${1:-}"
  ref="main"
  [ -n "$version" ] && ref="v$version"
  if [ -n "$version" ]; then
    curl -fsSL "https://raw.githubusercontent.com/kemiller2002/ordo/$ref/scripts/install-native.sh" |
      sh -s -- --version "$version" --install-base "$HOME_DIR"
  else
    curl -fsSL "https://raw.githubusercontent.com/kemiller2002/ordo/$ref/scripts/install-native.sh" |
      sh -s -- --install-base "$HOME_DIR"
  fi
}

install_praxis() {
  version="${1:-}"
  ref="main"
  [ -n "$version" ] && ref="v$version"
  if [ -n "$version" ]; then
    curl -fsSL "https://raw.githubusercontent.com/kemiller2002/praxis/$ref/scripts/install-native.sh" |
      sh -s -- --version "$version" --install-base "$HOME_DIR"
  else
    curl -fsSL "https://raw.githubusercontent.com/kemiller2002/praxis/$ref/scripts/install-native.sh" |
      sh -s -- --install-base "$HOME_DIR"
  fi
}

manifest_version() {
  tool="$1"
  file=".echelon/toolchain.json"
  [ -f "$file" ] || return 0
  tr -d '\r\n' < "$file" | sed -n 's/.*"'"$tool"'"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -n 1
}

setup_all() {
  ordo_version="$(manifest_version ordo || true)"
  praxis_version="$(manifest_version praxis || true)"
  install_ordo "$ordo_version"
  install_praxis "$praxis_version"
}

doctor() {
  failed=0
  for tool in ordo praxis; do
    if [ -x "$BIN_DIR/$tool" ]; then
      printf '%s: ' "$tool"
      "$BIN_DIR/$tool" --version || failed=1
    else
      echo "$tool: not installed"
      failed=1
    fi
  done

  if [ -f ".echelon/toolchain.json" ]; then
    echo "toolchain manifest: .echelon/toolchain.json"

    required_ordo="$(manifest_version ordo || true)"
    required_praxis="$(manifest_version praxis || true)"
    active_ordo=""
    active_praxis=""

    [ -f "$HOME_DIR/tools/ordo/current/VERSION" ] && active_ordo="$(cat "$HOME_DIR/tools/ordo/current/VERSION")"
    [ -f "$HOME_DIR/tools/praxis/current/VERSION" ] && active_praxis="$(cat "$HOME_DIR/tools/praxis/current/VERSION")"

    if [ -n "$required_ordo" ] && [ "$active_ordo" != "$required_ordo" ]; then
      echo "ordo requirement mismatch: required $required_ordo, active ${active_ordo:-none}"
      failed=1
    fi

    if [ -n "$required_praxis" ] && [ "$active_praxis" != "$required_praxis" ]; then
      echo "praxis requirement mismatch: required $required_praxis, active ${active_praxis:-none}"
      failed=1
    fi
  else
    echo "toolchain manifest: not present; latest releases will be used by setup"
  fi
  return "$failed"
}

command="${1:-}"
case "$command" in
  setup|upgrade)
    setup_all
    ;;
  install)
    tool="${2:-}"
    version="${3:-}"
    case "$tool" in
      ordo) install_ordo "$version" ;;
      praxis) install_praxis "$version" ;;
      *) usage; exit 2 ;;
    esac
    ;;
  doctor)
    doctor
    ;;
  version|--version|-V)
    if [ -x "$BIN_DIR/praxis" ]; then
      "$BIN_DIR/praxis" --version
    else
      echo "echelon bootstrap"
    fi
    ;;
  help|--help|-h|"")
    usage
    ;;
  *)
    usage
    exit 2
    ;;
esac
