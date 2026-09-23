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
  echelon doctor [--fix] [--verbose] [--json]
  echelon inventory [--json]
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
  echo
  doctor
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

manifest_field() {
  file="$1"
  key="$2"
  [ -f "$file" ] || return 0
  tr -d '\r\n' < "$file" | sed -n 's/.*"'"$key"'"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -n 1
}

repository_components_text() {
  root="$1"
  echelon_dir="$root/.echelon"
  [ -d "$echelon_dir" ] || return 0

  for file in "$echelon_dir"/*.json; do
    [ -f "$file" ] || continue
    base="$(basename "$file")"
    case "$base" in
      toolchain.json|*.config.json) continue ;;
    esac

    tool="$(manifest_field "$file" tool)"
    version="$(manifest_field "$file" installedVersion)"
    [ -n "$version" ] || version="$(manifest_field "$file" version)"

    if [ -z "$tool" ] && [ -n "$version" ]; then
      tool="${base%.json}"
    fi

    [ -n "$tool" ] || continue
    [ -n "$version" ] || version="unknown"
    printf '%s\t%s\t%s\n' "$tool" "$version" "$file"
  done
}

npm_packages_text() {
  root="$1"
  scope="$root/node_modules/@echelon-foundry"
  [ -d "$scope" ] || return 0

  for package_dir in "$scope"/*; do
    [ -d "$package_dir" ] || continue
    package_json="$package_dir/package.json"
    [ -f "$package_json" ] || continue
    name="$(manifest_field "$package_json" name)"
    version="$(manifest_field "$package_json" version)"
    [ -n "$name" ] || name="@echelon-foundry/$(basename "$package_dir")"
    [ -n "$version" ] || version="unknown"
    printf '%s\t%s\t%s\n' "$name" "$version" "$package_json"
  done
}

json_escape() {
  printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'
}

json_string() {
  printf '"%s"' "$(json_escape "$1")"
}

json_bool() {
  if [ "$1" -eq 1 ]; then printf 'true'; else printf 'false'; fi
}

json_native_tools() {
  first_tool=1
  printf '['
  if [ -d "$TOOLS_DIR" ]; then
    for tool_dir in "$TOOLS_DIR"/*; do
      [ -d "$tool_dir" ] || continue
      tool_name="$(basename "$tool_dir")"
      active="$(active_version "$tool_name")"
      [ "$first_tool" -eq 1 ] || printf ','
      first_tool=0
      printf '{"name":'
      json_string "$tool_name"
      printf ',"activeVersion":'
      if [ -n "$active" ]; then json_string "$active"; else printf 'null'; fi
      printf ',"installedVersions":['
      first_version=1
      for version_dir in "$tool_dir"/*; do
        [ -d "$version_dir" ] || continue
        version_name="$(basename "$version_dir")"
        [ "$version_name" = "current" ] && continue
        [ "$first_version" -eq 1 ] || printf ','
        first_version=0
        json_string "$version_name"
      done
      printf ']}'
    done
  fi
  printf ']'
}

json_repository_components() {
  root="$1"
  first=1
  printf '['
  repository_components_text "$root" | while IFS="$(printf '\t')" read -r tool version file; do
    [ -n "$tool" ] || continue
    [ "$first" -eq 1 ] || printf ','
    first=0
    printf '{"tool":'
    json_string "$tool"
    printf ',"installedVersion":'
    json_string "$version"
    printf ',"manifest":'
    json_string "$file"
    printf '}'
  done
  printf ']'
}

json_npm_packages() {
  root="$1"
  first=1
  printf '['
  npm_packages_text "$root" | while IFS="$(printf '\t')" read -r name version file; do
    [ -n "$name" ] || continue
    [ "$first" -eq 1 ] || printf ','
    first=0
    printf '{"package":'
    json_string "$name"
    printf ',"version":'
    json_string "$version"
    printf ',"manifest":'
    json_string "$file"
    printf '}'
  done
  printf ']'
}

json_finding() {
  code="$1"
  severity="$2"
  message="$3"
  remediation="$4"
  [ "$findings_first" -eq 1 ] || printf ','
  findings_first=0
  printf '{"code":'
  json_string "$code"
  printf ',"severity":'
  json_string "$severity"
  printf ',"message":'
  json_string "$message"
  printf ',"remediation":'
  json_string "$remediation"
  printf '}'
}

json_findings() {
  findings_first=1
  printf '['

  [ "$path_ok" -eq 1 ] || json_finding "ECHELON-DOC-001" "warning" "$BIN_DIR is not on PATH" "Add the Echelon bin directory to PATH."
  [ "$is_git" -eq 1 ] || json_finding "ECHELON-DOC-002" "warning" "The current directory is not inside a Git repository." "Run Doctor from a repository when repository diagnostics are required."
  [ -n "$active_ordo" ] || json_finding "ECHELON-DOC-010" "error" "Ordo has no active version." "Run echelon doctor --fix or echelon install ordo."
  [ -n "$active_praxis" ] || json_finding "ECHELON-DOC-011" "error" "Praxis has no active version." "Run echelon doctor --fix or echelon install praxis."

  for command_name in ordo sde praxis ros echelon; do
    command_path="$BIN_DIR/$command_name"
    command_ok=0
    command_version=""
    expected_version=""
    if [ -x "$command_path" ]; then
      if [ "$command_name" = "echelon" ]; then
        command_ok=1
      elif command_version="$("$command_path" --version 2>/dev/null | head -n 1)" && [ -n "$command_version" ]; then
        case "$command_name" in
          ordo|sde) expected_version="$active_ordo" ;;
          praxis|ros) expected_version="$active_praxis" ;;
        esac
        if [ -z "$expected_version" ] || [ "$command_version" = "$expected_version" ]; then
          command_ok=1
        fi
      fi
    fi

    if [ "$command_ok" -ne 1 ]; then
      if [ -n "$command_version" ] && [ -n "$expected_version" ] && [ "$command_version" != "$expected_version" ]; then
        json_finding "ECHELON-DOC-021" "error" "$command_name reports $command_version but the active version is $expected_version." "Run echelon doctor --fix to rebuild the command wrapper."
      else
        json_finding "ECHELON-DOC-020" "error" "$command_name is missing or does not execute successfully." "Run echelon doctor --fix to repair Echelon command wrappers."
      fi
    fi
  done

  if [ -n "$doctor_manifest" ] && [ -f "$doctor_manifest" ]; then
    [ -n "$required_ordo" ] || json_finding "ECHELON-DOC-031" "warning" "The toolchain manifest does not pin Ordo." "Add an exact Ordo version to .echelon/toolchain.json."
    [ -n "$required_praxis" ] || json_finding "ECHELON-DOC-032" "warning" "The toolchain manifest does not pin Praxis." "Add an exact Praxis version to .echelon/toolchain.json."
    [ -z "$required_ordo" ] || [ "$active_ordo" = "$required_ordo" ] || json_finding "ECHELON-DOC-033" "error" "Ordo requires $required_ordo but $active_ordo is active." "Run echelon doctor --fix."
    [ -z "$required_praxis" ] || [ "$active_praxis" = "$required_praxis" ] || json_finding "ECHELON-DOC-034" "error" "Praxis requires $required_praxis but $active_praxis is active." "Run echelon doctor --fix."
  else
    json_finding "ECHELON-DOC-030" "warning" "No .echelon/toolchain.json is present." "Add a toolchain manifest to make the repository reproducible."
  fi

  [ "$ordo_repo_status" != "invalid" ] || json_finding "ECHELON-DOC-040" "error" "Ordo repository verification failed." "Run ordo doctor in the repository for domain-specific diagnostics."
  [ "$praxis_repo_status" != "invalid" ] || json_finding "ECHELON-DOC-041" "error" "Praxis repository validation failed." "Run praxis doctor in the repository for domain-specific diagnostics."

  printf ']'
}

doctor_json() {
  platform="$(uname -s 2>/dev/null || echo unknown)"
  architecture="$(uname -m 2>/dev/null || echo unknown)"
  repo_root="$(repository_root)"
  is_git=0
  git rev-parse --is-inside-work-tree >/dev/null 2>&1 && is_git=1

  path_ok=0
  case ":$PATH:" in
    *":$BIN_DIR:"*) path_ok=1 ;;
  esac

  required_ordo="$(manifest_version ordo || true)"
  required_praxis="$(manifest_version praxis || true)"
  active_ordo="$(active_version ordo)"
  active_praxis="$(active_version praxis)"
  doctor_manifest="$(manifest_path)"

  errors=0
  warnings=0

  [ "$path_ok" -eq 1 ] || warnings=$((warnings + 1))
  [ "$is_git" -eq 1 ] || warnings=$((warnings + 1))
  [ -n "$active_ordo" ] || errors=$((errors + 1))
  [ -n "$active_praxis" ] || errors=$((errors + 1))

  command_health_json=""
  first_command=1
  for command_name in ordo sde praxis ros echelon; do
    command_path="$BIN_DIR/$command_name"
    healthy=0
    version_output=""
    if [ -x "$command_path" ]; then
      if [ "$command_name" = "echelon" ]; then
        healthy=1
      elif version_output="$("$command_path" --version 2>/dev/null | head -n 1)" && [ -n "$version_output" ]; then
        expected_version=""
        case "$command_name" in
          ordo|sde) expected_version="$active_ordo" ;;
          praxis|ros) expected_version="$active_praxis" ;;
        esac
        if [ -z "$expected_version" ] || [ "$version_output" = "$expected_version" ]; then
          healthy=1
        fi
      fi
    fi
    [ "$healthy" -eq 1 ] || errors=$((errors + 1))

    [ "$first_command" -eq 1 ] || command_health_json="$command_health_json,"
    first_command=0
    command_health_json="$command_health_json{\"name\":\"$(json_escape "$command_name")\",\"healthy\":$(if [ "$healthy" -eq 1 ]; then printf true; else printf false; fi),\"path\":\"$(json_escape "$command_path")\""
    if [ -n "$version_output" ]; then
      command_health_json="$command_health_json,\"version\":\"$(json_escape "$version_output")\""
    else
      command_health_json="$command_health_json,\"version\":null"
    fi
    command_health_json="$command_health_json}"
  done

  if [ -n "$doctor_manifest" ] && [ -f "$doctor_manifest" ]; then
    [ -n "$required_ordo" ] || warnings=$((warnings + 1))
    [ -n "$required_praxis" ] || warnings=$((warnings + 1))
    [ -z "$required_ordo" ] || [ "$active_ordo" = "$required_ordo" ] || errors=$((errors + 1))
    [ -z "$required_praxis" ] || [ "$active_praxis" = "$required_praxis" ] || errors=$((errors + 1))
  else
    warnings=$((warnings + 1))
  fi

  ordo_repo_status="not-installed"
  if [ -d "$repo_root/.sde" ]; then
    if [ -x "$BIN_DIR/ordo" ] && (cd "$repo_root" && "$BIN_DIR/ordo" verify >/dev/null 2>&1); then
      ordo_repo_status="valid"
    else
      ordo_repo_status="invalid"
      errors=$((errors + 1))
    fi
  fi

  praxis_repo_status="not-installed"
  if [ -d "$repo_root/.ros" ]; then
    if [ -x "$BIN_DIR/praxis" ] && (cd "$repo_root" && "$BIN_DIR/praxis" validate >/dev/null 2>&1); then
      praxis_repo_status="valid"
    else
      praxis_repo_status="invalid"
      errors=$((errors + 1))
    fi
  fi

  if [ "$errors" -gt 0 ]; then
    health="error"
    exit_code=1
  elif [ "$warnings" -gt 0 ]; then
    health="warning"
    exit_code=0
  else
    health="healthy"
    exit_code=0
  fi

  printf '{'
  printf '"schemaVersion":1,"tool":"echelon","command":"doctor","health":'
  json_string "$health"
  printf ',"exitCode":%s' "$exit_code"
  printf ',"machine":{"platform":'
  json_string "$platform"
  printf ',"architecture":'
  json_string "$architecture"
  printf ',"echelonHome":'
  json_string "$HOME_DIR"
  printf ',"binDirectory":'
  json_string "$BIN_DIR"
  printf ',"pathConfigured":'
  json_bool "$path_ok"
  printf '}'
  printf ',"nativeTools":'
  json_native_tools
  printf ',"commands":[%s]' "$command_health_json"
  printf ',"repository":{"root":'
  json_string "$repo_root"
  printf ',"isGit":'
  json_bool "$is_git"
  printf ',"toolchainManifest":'
  if [ -n "$doctor_manifest" ]; then json_string "$doctor_manifest"; else printf 'null'; fi
  printf ',"requirements":{"ordo":'
  if [ -n "$required_ordo" ]; then json_string "$required_ordo"; else printf 'null'; fi
  printf ',"praxis":'
  if [ -n "$required_praxis" ]; then json_string "$required_praxis"; else printf 'null'; fi
  printf '}'
  printf ',"ordoStatus":'
  json_string "$ordo_repo_status"
  printf ',"praxisStatus":'
  json_string "$praxis_repo_status"
  printf ',"components":'
  json_repository_components "$repo_root"
  printf ',"npmPackages":'
  json_npm_packages "$repo_root"
  printf '}'
  printf ',"findings":'
  json_findings
  printf ',"summary":{"errors":%s,"warnings":%s}' "$errors" "$warnings"
  printf '}\n'
  return "$exit_code"
}

inventory() {
  json=0
  if [ "${1:-}" = "--json" ]; then json=1; shift; fi
  if [ "$#" -gt 0 ]; then
    echo "Usage: echelon inventory [--json]" >&2
    return 2
  fi

  repo_root="$(repository_root)"

  if [ "$json" -eq 1 ]; then
    printf '{"schemaVersion":1,"tool":"echelon","command":"inventory","nativeTools":'
    json_native_tools
    printf ',"repository":{"root":'
    json_string "$repo_root"
    printf ',"components":'
    json_repository_components "$repo_root"
    printf ',"npmPackages":'
    json_npm_packages "$repo_root"
    printf '}}\n'
    return 0
  fi

  echo "Echelon Inventory"
  echo "================="
  echo
  echo "Native tools"
  if [ -d "$TOOLS_DIR" ]; then
    found=0
    for tool_dir in "$TOOLS_DIR"/*; do
      [ -d "$tool_dir" ] || continue
      found=1
      tool_name="$(basename "$tool_dir")"
      active="$(active_version "$tool_name")"
      versions="$(installed_versions "$tool_name")"
      [ -n "$active" ] || active="none"
      [ -n "$versions" ] || versions="none"
      doctor_row ok "$tool_name" "active $active; installed $versions"
    done
    [ "$found" -eq 1 ] || doctor_row ok "Native tools" "none"
  else
    doctor_row ok "Native tools" "none"
  fi

  echo
  echo "Repository components"
  components="$(repository_components_text "$repo_root")"
  if [ -n "$components" ]; then
    printf '%s\n' "$components" | while IFS="$(printf '\t')" read -r tool version file; do
      doctor_row ok "$tool" "$version ($file)"
    done
  else
    doctor_row ok "Components" "none"
  fi

  echo
  echo "Installed Echelon npm packages"
  packages="$(npm_packages_text "$repo_root")"
  if [ -n "$packages" ]; then
    printf '%s\n' "$packages" | while IFS="$(printf '\t')" read -r name version file; do
      doctor_row ok "$name" "$version"
    done
  else
    doctor_row ok "npm packages" "none"
  fi
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
  json=0

  while [ "$#" -gt 0 ]; do
    case "$1" in
      --fix) fix=1 ;;
      --verbose|-v) verbose=1 ;;
      --json) json=1 ;;
      -h|--help)
        echo "Usage: echelon doctor [--fix] [--verbose] [--json]"
        return 0
        ;;
      *)
        echo "Unknown doctor option: $1" >&2
        return 2
        ;;
    esac
    shift
  done

  if [ "$fix" -eq 1 ] && [ "$json" -eq 1 ]; then
    echo "echelon doctor --fix and --json cannot be combined" >&2
    return 2
  fi

  if [ "$fix" -eq 1 ]; then
    doctor_fix
  fi

  if [ "$json" -eq 1 ]; then
    doctor_json
    return $?
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
        expected_version=""
        case "$command_name" in
          ordo|sde) expected_version="$(active_version ordo)" ;;
          praxis|ros) expected_version="$(active_version praxis)" ;;
        esac
        if [ -n "$expected_version" ] && [ "$version_output" != "$expected_version" ]; then
          doctor_row error "$command_name command" "$version_output; active version is $expected_version"
          errors=$((errors + 1))
        else
          doctor_row ok "$command_name command" "$version_output"
        fi
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

  echo
  echo "Repository components"
  components="$(repository_components_text "$repo_root")"
  if [ -n "$components" ]; then
    printf '%s\n' "$components" | while IFS="$(printf '\t')" read -r component_tool component_version component_file; do
      doctor_row ok "$component_tool" "$component_version ($component_file)"
    done
  else
    doctor_row ok "Components" "none"
  fi

  echo
  echo "Installed Echelon npm packages"
  packages="$(npm_packages_text "$repo_root")"
  if [ -n "$packages" ]; then
    printf '%s\n' "$packages" | while IFS="$(printf '\t')" read -r package_name package_version package_file; do
      doctor_row ok "$package_name" "$package_version"
    done
  else
    doctor_row ok "npm packages" "none"
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
  inventory)
    shift
    inventory "$@"
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
