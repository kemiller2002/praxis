#!/usr/bin/env sh
set -eu

HOME_DIR="${ECHELON_HOME:-$HOME/.echelon}"
BIN_DIR="$HOME_DIR/bin"
TOOLS_DIR="$HOME_DIR/tools"

usage() {
  cat <<'EOF'
Usage:
  echelon setup
  echelon upgrade
  echelon install ordo [VERSION]
  echelon install praxis [VERSION]
  echelon doctor [--fix] [--verbose]
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

repository_root() {
  git rev-parse --show-toplevel 2>/dev/null || pwd
}

manifest_path() {
  root="$(repository_root)"
  if [ -f "$root/.echelon/toolchain.json" ]; then
    printf '%s\n' "$root/.echelon/toolchain.json"
  elif [ -f ".echelon/toolchain.json" ]; then
    printf '%s\n' ".echelon/toolchain.json"
  fi
}

manifest_version() {
  tool="$1"
  file="$(manifest_path)"
  [ -n "$file" ] && [ -f "$file" ] || return 0
  tr -d '\r\n' < "$file" | sed -n 's/.*"'"$tool"'"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -n 1
}

setup_all() {
  ordo_version="$(manifest_version ordo || true)"
  praxis_version="$(manifest_version praxis || true)"
  install_ordo "$ordo_version"
  install_praxis "$praxis_version"
}

active_version() {
  tool="$1"
  version_file="$TOOLS_DIR/$tool/current/VERSION"
  [ -f "$version_file" ] && tr -d '\r\n' < "$version_file" || true
}

installed_versions() {
  tool="$1"
  root="$TOOLS_DIR/$tool"
  [ -d "$root" ] || return 0
  first=1
  for entry in "$root"/*; do
    [ -d "$entry" ] || continue
    name="$(basename "$entry")"
    [ "$name" = "current" ] && continue
    if [ "$first" -eq 0 ]; then
      printf ', '
    fi
    printf '%s' "$name"
    first=0
  done
}

single_installed_version() {
  tool="$1"
  root="$TOOLS_DIR/$tool"
  [ -d "$root" ] || return 0

  count=0
  candidate=""
  for entry in "$root"/*; do
    [ -d "$entry" ] || continue
    name="$(basename "$entry")"
    [ "$name" = "current" ] && continue
    count=$((count + 1))
    candidate="$name"
  done

  [ "$count" -eq 1 ] && printf '%s\n' "$candidate" || true
}

doctor_row() {
  state="$1"
  label="$2"
  value="$3"
  printf '  %-8s %-24s %s\n' "[$state]" "$label" "$value"
}

doctor_fix_tool() {
  tool="$1"
  required="$2"
  active="$3"
  command_path="$BIN_DIR/$tool"

  target="$required"
  [ -n "$target" ] || target="$active"

  if [ -z "$target" ]; then
    printf '  [skip]   %-24s %s\n' "$tool" "no pinned or active version is available to repair"
    return 0
  fi

  printf '  [fix]    %-24s %s\n' "$tool" "reinstalling/activating $target"
  case "$tool" in
    ordo) install_ordo "$target" ;;
    praxis) install_praxis "$target" ;;
  esac

  if [ ! -x "$command_path" ]; then
    printf '  [error]  %-24s %s\n' "$tool" "installer completed but command is still missing"
    return 1
  fi
}

doctor_fix() {
  required_ordo="$(manifest_version ordo || true)"
  required_praxis="$(manifest_version praxis || true)"
  actual_active_ordo="$(active_version ordo)"
  actual_active_praxis="$(active_version praxis)"
  repair_ordo="$actual_active_ordo"
  repair_praxis="$actual_active_praxis"
  [ -n "$repair_ordo" ] || repair_ordo="$(single_installed_version ordo)"
  [ -n "$repair_praxis" ] || repair_praxis="$(single_installed_version praxis)"

  echo "Repairs"
  mkdir -p "$BIN_DIR" "$TOOLS_DIR"

  needs_ordo=0
  needs_praxis=0

  [ ! -x "$BIN_DIR/ordo" ] && needs_ordo=1
  [ ! -x "$BIN_DIR/sde" ] && needs_ordo=1
  [ -z "$actual_active_ordo" ] && needs_ordo=1
  [ -n "$required_ordo" ] && [ "$actual_active_ordo" != "$required_ordo" ] && needs_ordo=1

  [ ! -x "$BIN_DIR/praxis" ] && needs_praxis=1
  [ ! -x "$BIN_DIR/ros" ] && needs_praxis=1
  [ ! -x "$BIN_DIR/echelon" ] && needs_praxis=1
  [ -z "$actual_active_praxis" ] && needs_praxis=1
  [ -n "$required_praxis" ] && [ "$actual_active_praxis" != "$required_praxis" ] && needs_praxis=1

  if [ "$needs_ordo" -eq 1 ]; then
    doctor_fix_tool ordo "$required_ordo" "$repair_ordo"
  else
    doctor_row ok "Ordo" "no mechanical repair needed"
  fi

  if [ "$needs_praxis" -eq 1 ]; then
    doctor_fix_tool praxis "$required_praxis" "$repair_praxis"
  else
    doctor_row ok "Praxis" "no mechanical repair needed"
  fi

  case ":$PATH:" in
    *":$BIN_DIR:"*) ;;
    *)
      doctor_row warn "PATH" "not changed automatically; add $BIN_DIR to your shell PATH"
      ;;
  esac
  echo
}

doctor() {
  fix=0
  verbose=0

  while [ "$#" -gt 0 ]; do
    case "$1" in
      --fix) fix=1 ;;
      --verbose|-v) verbose=1 ;;
      -h|--help)
        echo "Usage: echelon doctor [--fix] [--verbose]"
        return 0
        ;;
      *)
        echo "Unknown doctor option: $1" >&2
        return 2
        ;;
    esac
    shift
  done

  if [ "$fix" -eq 1 ]; then
    doctor_fix
  fi

  errors=0
  warnings=0

  echo "Echelon Doctor"
  echo "=============="
  echo
  echo "Machine"

  platform="$(uname -s 2>/dev/null || echo unknown)"
  architecture="$(uname -m 2>/dev/null || echo unknown)"
  doctor_row ok "Platform" "$platform $architecture"
  doctor_row ok "Echelon home" "$HOME_DIR"

  case ":$PATH:" in
    *":$BIN_DIR:"*) doctor_row ok "PATH" "$BIN_DIR" ;;
    *)
      doctor_row warn "PATH" "$BIN_DIR is not on PATH"
      warnings=$((warnings + 1))
      ;;
  esac

  echo
  echo "Toolchain"

  for tool in ordo praxis; do
    active="$(active_version "$tool")"
    installed="$(installed_versions "$tool")"
    [ -n "$installed" ] || installed="none"

    if [ -n "$active" ]; then
      doctor_row ok "$tool active" "$active"
    else
      doctor_row error "$tool active" "none"
      errors=$((errors + 1))
    fi
    doctor_row ok "$tool installed" "$installed"
  done

  for command_name in ordo sde praxis ros echelon; do
    command_path="$BIN_DIR/$command_name"
    if [ -x "$command_path" ]; then
      if [ "$command_name" = "echelon" ]; then
        doctor_row ok "$command_name command" "$command_path"
      elif version_output="$("$command_path" --version 2>/dev/null | head -n 1)" && [ -n "$version_output" ]; then
        doctor_row ok "$command_name command" "$version_output"
      else
        doctor_row error "$command_name command" "present but failed --version"
        errors=$((errors + 1))
      fi
    else
      doctor_row error "$command_name command" "missing: $command_path"
      errors=$((errors + 1))
    fi
  done

  if [ -d "$TOOLS_DIR" ]; then
    extras=""
    for entry in "$TOOLS_DIR"/*; do
      [ -d "$entry" ] || continue
      name="$(basename "$entry")"
      case "$name" in
        ordo|praxis) continue ;;
      esac

      versions=""
      for version_entry in "$entry"/*; do
        [ -d "$version_entry" ] || continue
        version_name="$(basename "$version_entry")"
        [ "$version_name" = "current" ] && continue
        if [ -n "$versions" ]; then versions="$versions, $version_name"; else versions="$version_name"; fi
      done

      item="$name"
      [ -n "$versions" ] && item="$name ($versions)"
      if [ -n "$extras" ]; then extras="$extras; $item"; else extras="$item"; fi
    done
    [ -n "$extras" ] && doctor_row ok "Other installed tools" "$extras"
  fi

  echo
  echo "Repository"

  if git rev-parse --is-inside-work-tree >/dev/null 2>&1; then
    repo_root="$(repository_root)"
    doctor_row ok "Git repository" "$repo_root"
  else
    repo_root="$(pwd)"
    doctor_row warn "Git repository" "current directory is not inside a Git repository"
    warnings=$((warnings + 1))
  fi

  doctor_manifest="$(manifest_path)"
  if [ -n "$doctor_manifest" ] && [ -f "$doctor_manifest" ]; then
    doctor_row ok "Toolchain manifest" "$doctor_manifest"
    required_ordo="$(manifest_version ordo || true)"
    required_praxis="$(manifest_version praxis || true)"
    active_ordo="$(active_version ordo)"
    active_praxis="$(active_version praxis)"

    if [ -n "$required_ordo" ]; then
      if [ "$active_ordo" = "$required_ordo" ]; then
        doctor_row ok "Ordo requirement" "$required_ordo"
      else
        doctor_row error "Ordo requirement" "required $required_ordo; active ${active_ordo:-none}"
        errors=$((errors + 1))
      fi
    else
      doctor_row warn "Ordo requirement" "not pinned in manifest"
      warnings=$((warnings + 1))
    fi

    if [ -n "$required_praxis" ]; then
      if [ "$active_praxis" = "$required_praxis" ]; then
        doctor_row ok "Praxis requirement" "$required_praxis"
      else
        doctor_row error "Praxis requirement" "required $required_praxis; active ${active_praxis:-none}"
        errors=$((errors + 1))
      fi
    else
      doctor_row warn "Praxis requirement" "not pinned in manifest"
      warnings=$((warnings + 1))
    fi
  else
    doctor_row warn "Toolchain manifest" "not present; setup will use latest stable releases"
    warnings=$((warnings + 1))
  fi

  if [ -d "$repo_root/.sde" ]; then
    if [ -x "$BIN_DIR/ordo" ] && (cd "$repo_root" && "$BIN_DIR/ordo" verify >/dev/null 2>&1); then
      doctor_row ok "Ordo repository" "verify passed"
    else
      doctor_row error "Ordo repository" "verify failed"
      errors=$((errors + 1))
    fi
  else
    doctor_row ok "Ordo repository" "not installed in this repository"
  fi

  if [ -d "$repo_root/.ros" ]; then
    if [ -x "$BIN_DIR/praxis" ] && (cd "$repo_root" && "$BIN_DIR/praxis" validate >/dev/null 2>&1); then
      doctor_row ok "Praxis repository" "validation passed"
    else
      doctor_row error "Praxis repository" "validation failed"
      errors=$((errors + 1))
    fi
  else
    doctor_row ok "Praxis repository" "not installed in this repository"
  fi

  if [ "$verbose" -eq 1 ]; then
    echo
    echo "Paths"
    doctor_row ok "Binary directory" "$BIN_DIR"
    doctor_row ok "Tools directory" "$TOOLS_DIR"
    doctor_row ok "Working directory" "$(pwd)"
    if [ -L "$TOOLS_DIR/ordo/current" ]; then
      doctor_row ok "Ordo activation" "$(readlink "$TOOLS_DIR/ordo/current" 2>/dev/null || echo unknown)"
    fi
    if [ -L "$TOOLS_DIR/praxis/current" ]; then
      doctor_row ok "Praxis activation" "$(readlink "$TOOLS_DIR/praxis/current" 2>/dev/null || echo unknown)"
    fi
  fi

  echo
  echo "Summary"
  printf '  Errors:   %s\n' "$errors"
  printf '  Warnings: %s\n' "$warnings"

  if [ "$errors" -eq 0 ]; then
    if [ "$warnings" -eq 0 ]; then
      echo "  Environment healthy."
    else
      echo "  Environment usable with warnings."
    fi
    return 0
  fi

  echo "  Environment requires attention."
  echo "  Run: echelon doctor --fix"
  return 1
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
    shift
    doctor "$@"
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
