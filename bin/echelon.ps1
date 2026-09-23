param(
    [Parameter(Position=0)][string]$Command = "help",
    [Parameter(Position=1)][string]$Tool,
    [Parameter(Position=2)][string]$Version,
    [Parameter(ValueFromRemainingArguments=$true)][string[]]$Remaining
)

$ErrorActionPreference = "Stop"
$HomeDir = if ($env:ECHELON_HOME) { $env:ECHELON_HOME } else { Join-Path $HOME ".echelon" }
$BinDir = Join-Path $HomeDir "bin"
$ToolsDir = Join-Path $HomeDir "tools"

function Show-Usage {
    @"
Usage:
  echelon setup
  echelon upgrade
  echelon install ordo [VERSION]
  echelon install praxis [VERSION]
  echelon doctor [--fix] [--verbose]
  echelon version
"@ | Write-Host
}

function Invoke-Installer([string]$Name, [string]$RequestedVersion) {
    $ref = if ($RequestedVersion) { "v$RequestedVersion" } else { "main" }
    $uri = "https://raw.githubusercontent.com/kemiller2002/$Name/$ref/scripts/install-native.ps1"
    $temp = Join-Path ([IO.Path]::GetTempPath()) ("echelon-" + [Guid]::NewGuid().ToString("N") + ".ps1")
    try {
        Invoke-WebRequest -UseBasicParsing -Uri $uri -OutFile $temp
        $installerArgs = @("-InstallBase", $HomeDir)
        if ($RequestedVersion) { $installerArgs += @("-Version", $RequestedVersion) }
        & powershell -NoProfile -ExecutionPolicy Bypass -File $temp @installerArgs
        if ($LASTEXITCODE -ne 0) { throw "$Name installer exited with code $LASTEXITCODE." }
    }
    finally {
        if (Test-Path $temp) { Remove-Item -Force $temp }
    }
}

function Get-RepositoryRoot {
    if (Get-Command git -ErrorAction SilentlyContinue) {
        $candidate = @(& git rev-parse --show-toplevel 2>$null | Select-Object -First 1)
        if ($LASTEXITCODE -eq 0 -and $candidate.Count -gt 0) {
            return [string]$candidate[0]
        }
    }

    return (Get-Location).Path
}

function Get-ManifestPath {
    $root = Get-RepositoryRoot
    $candidate = Join-Path $root ".echelon\toolchain.json"
    if (Test-Path $candidate) { return $candidate }

    $local = Join-Path (Get-Location) ".echelon\toolchain.json"
    if (Test-Path $local) { return $local }

    return $null
}

function Get-ManifestVersions {
    $path = Get-ManifestPath
    if (-not $path) {
        return @{ ordo = $null; praxis = $null }
    }

    $manifest = Get-Content -Raw $path | ConvertFrom-Json
    return @{
        ordo = [string]$manifest.ordo
        praxis = [string]$manifest.praxis
    }
}

function Install-All {
    $versions = Get-ManifestVersions
    Invoke-Installer "ordo" $versions.ordo
    Invoke-Installer "praxis" $versions.praxis
    Write-Host
    Invoke-Doctor @()
}

function Get-ActiveVersion([string]$Name) {
    $versionFile = Join-Path $ToolsDir "$Name\current-version"
    if (Test-Path $versionFile) {
        return (Get-Content -Raw $versionFile).Trim()
    }

    $portableVersionFile = Join-Path $ToolsDir "$Name\current\VERSION"
    if (Test-Path $portableVersionFile) {
        return (Get-Content -Raw $portableVersionFile).Trim()
    }

    return ""
}

function Get-InstalledVersions([string]$Name) {
    $root = Join-Path $ToolsDir $Name
    if (-not (Test-Path $root)) { return @() }

    return @(
        Get-ChildItem -Path $root -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -ne "current" } |
            Sort-Object Name |
            ForEach-Object { $_.Name }
    )
}

function Get-SingleInstalledVersion([string]$Name) {
    $versions = @(Get-InstalledVersions $Name)
    if ($versions.Count -eq 1) { return [string]$versions[0] }
    return ""
}

function Write-DoctorRow([string]$State, [string]$Label, [string]$Value) {
    Write-Host ("  [{0,-5}] {1,-24} {2}" -f $State, $Label, $Value)
}

function Test-BinOnPath {
    $entries = @($env:PATH -split ';' | ForEach-Object { $_.TrimEnd('\') })
    return $entries -contains $BinDir.TrimEnd('\')
}

function Get-CommandFile([string]$Name) {
    return Join-Path $BinDir "$Name.cmd"
}

function Repair-DoctorTool([string]$Name, [string]$Required, [string]$Active) {
    $target = if ($Required) { $Required } else { $Active }
    if (-not $target) {
        Write-DoctorRow "skip" $Name "no pinned or active version is available to repair"
        return
    }

    Write-DoctorRow "fix" $Name "reinstalling/activating $target"
    Invoke-Installer $Name $target

    $command = Get-CommandFile $Name
    if (-not (Test-Path $command)) {
        throw "$Name installer completed but $command is still missing."
    }
}

function Invoke-DoctorFix {
    $required = Get-ManifestVersions
    $actualActiveOrdo = Get-ActiveVersion "ordo"
    $actualActivePraxis = Get-ActiveVersion "praxis"
    $repairOrdo = if ($actualActiveOrdo) { $actualActiveOrdo } else { Get-SingleInstalledVersion "ordo" }
    $repairPraxis = if ($actualActivePraxis) { $actualActivePraxis } else { Get-SingleInstalledVersion "praxis" }

    Write-Host "Repairs"
    New-Item -ItemType Directory -Force -Path $BinDir, $ToolsDir | Out-Null

    $needsOrdo =
        -not (Test-Path (Get-CommandFile "ordo")) -or
        -not (Test-Path (Get-CommandFile "sde")) -or
        (-not $actualActiveOrdo) -or
        ($required.ordo -and $actualActiveOrdo -ne $required.ordo)

    $needsPraxis =
        -not (Test-Path (Get-CommandFile "praxis")) -or
        -not (Test-Path (Get-CommandFile "ros")) -or
        -not (Test-Path (Get-CommandFile "echelon")) -or
        (-not $actualActivePraxis) -or
        ($required.praxis -and $actualActivePraxis -ne $required.praxis)

    if ($needsOrdo) {
        Repair-DoctorTool "ordo" $required.ordo $repairOrdo
    }
    else {
        Write-DoctorRow "ok" "Ordo" "no mechanical repair needed"
    }

    if ($needsPraxis) {
        Repair-DoctorTool "praxis" $required.praxis $repairPraxis
    }
    else {
        Write-DoctorRow "ok" "Praxis" "no mechanical repair needed"
    }

    if (-not (Test-BinOnPath)) {
        Write-DoctorRow "warn" "PATH" "not changed automatically; add $BinDir to PATH"
    }

    Write-Host
}

function Invoke-Doctor([string[]]$Options) {
    $fix = $false
    $verbose = $false

    foreach ($option in @($Options)) {
        if (-not $option) { continue }
        switch ($option.ToLowerInvariant()) {
            "--fix" { $fix = $true }
            "--verbose" { $verbose = $true }
            "-v" { $verbose = $true }
            "--help" {
                Write-Host "Usage: echelon doctor [--fix] [--verbose]"
                return
            }
            "-h" {
                Write-Host "Usage: echelon doctor [--fix] [--verbose]"
                return
            }
            default {
                Write-Error "Unknown doctor option: $option"
                exit 2
            }
        }
    }

    if ($fix) {
        Invoke-DoctorFix
    }

    $errors = 0
    $warnings = 0

    Write-Host "Echelon Doctor"
    Write-Host "=============="
    Write-Host
    Write-Host "Machine"

    $arch = if ($env:PROCESSOR_ARCHITECTURE) { $env:PROCESSOR_ARCHITECTURE } else { "unknown" }
    Write-DoctorRow "ok" "Platform" "Windows $arch"
    Write-DoctorRow "ok" "Echelon home" $HomeDir

    if (Test-BinOnPath) {
        Write-DoctorRow "ok" "PATH" $BinDir
    }
    else {
        Write-DoctorRow "warn" "PATH" "$BinDir is not on PATH"
        $warnings++
    }

    Write-Host
    Write-Host "Toolchain"

    foreach ($name in @("ordo", "praxis")) {
        $active = Get-ActiveVersion $name
        $installed = @(Get-InstalledVersions $name)
        if ($active) {
            Write-DoctorRow "ok" "$name active" $active
        }
        else {
            Write-DoctorRow "error" "$name active" "none"
            $errors++
        }

        $installedText = if ($installed.Count -gt 0) { $installed -join ", " } else { "none" }
        Write-DoctorRow "ok" "$name installed" $installedText
    }

    foreach ($name in @("ordo", "sde", "praxis", "ros", "echelon")) {
        $cmd = Get-CommandFile $name
        if (Test-Path $cmd) {
            if ($name -eq "echelon") {
                Write-DoctorRow "ok" "$name command" $cmd
            }
            else {
                $versionOutput = @(& $cmd --version 2>$null | Select-Object -First 1)
                if ($LASTEXITCODE -eq 0 -and $versionOutput.Count -gt 0 -and $versionOutput[0]) {
                    Write-DoctorRow "ok" "$name command" ([string]$versionOutput[0])
                }
                else {
                    Write-DoctorRow "error" "$name command" "present but failed --version"
                    $errors++
                }
            }
        }
        else {
            Write-DoctorRow "error" "$name command" "missing: $cmd"
            $errors++
        }
    }

    if (Test-Path $ToolsDir) {
        $extras = @(
            Get-ChildItem -Path $ToolsDir -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -notin @("ordo", "praxis") } |
                Sort-Object Name |
                ForEach-Object {
                    $versions = @(
                        Get-ChildItem -Path $_.FullName -Directory -ErrorAction SilentlyContinue |
                            Where-Object { $_.Name -ne "current" } |
                            Sort-Object Name |
                            ForEach-Object { $_.Name }
                    )
                    if ($versions.Count -gt 0) {
                        "$($_.Name) ($($versions -join ', '))"
                    }
                    else {
                        $_.Name
                    }
                }
        )
        if ($extras.Count -gt 0) {
            Write-DoctorRow "ok" "Other installed tools" ($extras -join "; ")
        }
    }

    Write-Host
    Write-Host "Repository"

    $insideGit = $false
    if (Get-Command git -ErrorAction SilentlyContinue) {
        & git rev-parse --is-inside-work-tree *> $null
        $insideGit = $LASTEXITCODE -eq 0
    }

    $repoRoot = Get-RepositoryRoot
    if ($insideGit) {
        Write-DoctorRow "ok" "Git repository" $repoRoot
    }
    else {
        Write-DoctorRow "warn" "Git repository" "current directory is not inside a Git repository"
        $warnings++
    }

    $manifestPath = Get-ManifestPath
    if ($manifestPath) {
        Write-DoctorRow "ok" "Toolchain manifest" ".echelon/toolchain.json"
        $required = Get-ManifestVersions
        $activeOrdo = Get-ActiveVersion "ordo"
        $activePraxis = Get-ActiveVersion "praxis"

        if ($required.ordo) {
            if ($activeOrdo -eq $required.ordo) {
                Write-DoctorRow "ok" "Ordo requirement" $required.ordo
            }
            else {
                $shown = if ($activeOrdo) { $activeOrdo } else { "none" }
                Write-DoctorRow "error" "Ordo requirement" "required $($required.ordo); active $shown"
                $errors++
            }
        }
        else {
            Write-DoctorRow "warn" "Ordo requirement" "not pinned in manifest"
            $warnings++
        }

        if ($required.praxis) {
            if ($activePraxis -eq $required.praxis) {
                Write-DoctorRow "ok" "Praxis requirement" $required.praxis
            }
            else {
                $shown = if ($activePraxis) { $activePraxis } else { "none" }
                Write-DoctorRow "error" "Praxis requirement" "required $($required.praxis); active $shown"
                $errors++
            }
        }
        else {
            Write-DoctorRow "warn" "Praxis requirement" "not pinned in manifest"
            $warnings++
        }
    }
    else {
        Write-DoctorRow "warn" "Toolchain manifest" "not present; setup will use latest stable releases"
        $warnings++
    }

    if (Test-Path (Join-Path $repoRoot ".sde")) {
        $ordo = Get-CommandFile "ordo"
        if (Test-Path $ordo) {
            Push-Location $repoRoot
            try {
                & $ordo verify *> $null
                $ordoExit = $LASTEXITCODE
            }
            finally {
                Pop-Location
            }
            if ($ordoExit -eq 0) {
                Write-DoctorRow "ok" "Ordo repository" "verify passed"
            }
            else {
                Write-DoctorRow "error" "Ordo repository" "verify failed"
                $errors++
            }
        }
        else {
            Write-DoctorRow "error" "Ordo repository" "cannot verify because Ordo is missing"
            $errors++
        }
    }
    else {
        Write-DoctorRow "ok" "Ordo repository" "not installed in this repository"
    }

    if (Test-Path (Join-Path $repoRoot ".ros")) {
        $praxis = Get-CommandFile "praxis"
        if (Test-Path $praxis) {
            Push-Location $repoRoot
            try {
                & $praxis validate *> $null
                $praxisExit = $LASTEXITCODE
            }
            finally {
                Pop-Location
            }
            if ($praxisExit -eq 0) {
                Write-DoctorRow "ok" "Praxis repository" "validation passed"
            }
            else {
                Write-DoctorRow "error" "Praxis repository" "validation failed"
                $errors++
            }
        }
        else {
            Write-DoctorRow "error" "Praxis repository" "cannot validate because Praxis is missing"
            $errors++
        }
    }
    else {
        Write-DoctorRow "ok" "Praxis repository" "not installed in this repository"
    }

    if ($verbose) {
        Write-Host
        Write-Host "Paths"
        Write-DoctorRow "ok" "Binary directory" $BinDir
        Write-DoctorRow "ok" "Tools directory" $ToolsDir
        Write-DoctorRow "ok" "Working directory" (Get-Location).Path

        $ordoVersion = Get-ActiveVersion "ordo"
        if ($ordoVersion) { Write-DoctorRow "ok" "Ordo activation" $ordoVersion }
        $praxisVersion = Get-ActiveVersion "praxis"
        if ($praxisVersion) { Write-DoctorRow "ok" "Praxis activation" $praxisVersion }
    }

    Write-Host
    Write-Host "Summary"
    Write-Host ("  Errors:   {0}" -f $errors)
    Write-Host ("  Warnings: {0}" -f $warnings)

    if ($errors -eq 0) {
        if ($warnings -eq 0) {
            Write-Host "  Environment healthy."
        }
        else {
            Write-Host "  Environment usable with warnings."
        }
        exit 0
    }

    Write-Host "  Environment requires attention."
    Write-Host "  Run: echelon doctor --fix"
    exit 1
}

switch ($Command.ToLowerInvariant()) {
    "setup" { Install-All }
    "upgrade" { Install-All }
    "install" {
        $toolName = if ($null -eq $Tool) { "" } else { $Tool }
        switch ($toolName.ToLowerInvariant()) {
            "ordo" { Invoke-Installer "ordo" $Version }
            "praxis" { Invoke-Installer "praxis" $Version }
            default { Show-Usage; exit 2 }
        }
    }
    "doctor" {
        $doctorOptions = @()
        if ($Tool) { $doctorOptions += $Tool }
        if ($Version) { $doctorOptions += $Version }
        if ($Remaining) { $doctorOptions += $Remaining }
        Invoke-Doctor $doctorOptions
    }
    "version" {
        $cmd = Get-CommandFile "praxis"
        if (Test-Path $cmd) { & $cmd --version } else { Write-Host "echelon bootstrap" }
    }
    "--version" {
        $cmd = Get-CommandFile "praxis"
        if (Test-Path $cmd) { & $cmd --version } else { Write-Host "echelon bootstrap" }
    }
    "-v" {
        $cmd = Get-CommandFile "praxis"
        if (Test-Path $cmd) { & $cmd --version } else { Write-Host "echelon bootstrap" }
    }
    "help" { Show-Usage }
    "--help" { Show-Usage }
    "-h" { Show-Usage }
    default { Show-Usage; exit 2 }
}
