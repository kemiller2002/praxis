param(
    [Parameter(Position=0)][string]$Command = "help",
    [Parameter(Position=1)][string]$Tool,
    [Parameter(Position=2)][string]$Version
)

$ErrorActionPreference = "Stop"
$HomeDir = if ($env:ECHELON_HOME) { $env:ECHELON_HOME } else { Join-Path $HOME ".echelon" }
$BinDir = Join-Path $HomeDir "bin"

function Show-Usage {
    @"
Usage:
  echelon setup
  echelon upgrade
  echelon install ordo [VERSION]
  echelon install praxis [VERSION]
  echelon doctor
  echelon version
"@ | Write-Host
}

function Invoke-Installer([string]$Name, [string]$RequestedVersion) {
    $ref = if ($RequestedVersion) { "v$RequestedVersion" } else { "main" }
    $uri = "https://raw.githubusercontent.com/kemiller2002/$Name/$ref/scripts/install-native.ps1"
    $temp = Join-Path ([IO.Path]::GetTempPath()) ("echelon-" + [Guid]::NewGuid().ToString("N") + ".ps1")
    try {
        Invoke-WebRequest -UseBasicParsing -Uri $uri -OutFile $temp
        $args = @("-InstallBase", $HomeDir)
        if ($RequestedVersion) { $args += @("-Version", $RequestedVersion) }
        & powershell -NoProfile -ExecutionPolicy Bypass -File $temp @args
        if ($LASTEXITCODE -ne 0) { throw "$Name installer exited with code $LASTEXITCODE." }
    }
    finally {
        if (Test-Path $temp) { Remove-Item -Force $temp }
    }
}

function Get-ManifestVersions {
    $path = Join-Path (Get-Location) ".echelon\toolchain.json"
    if (-not (Test-Path $path)) {
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
}

function Invoke-Doctor {
    $failed = $false
    foreach ($name in @("ordo", "praxis")) {
        $cmd = Join-Path $BinDir "$name.cmd"
        if (Test-Path $cmd) {
            Write-Host -NoNewline "$name: "
            & $cmd --version
            if ($LASTEXITCODE -ne 0) { $failed = $true }
        }
        else {
            Write-Host "$name: not installed"
            $failed = $true
        }
    }

    $manifest = Join-Path (Get-Location) ".echelon\toolchain.json"
    if (Test-Path $manifest) {
        Write-Host "toolchain manifest: .echelon/toolchain.json"
        $required = Get-Content -Raw $manifest | ConvertFrom-Json

        $ordoVersionFile = Join-Path $HomeDir "tools\ordo\current-version"
        $praxisVersionFile = Join-Path $HomeDir "tools\praxis\current-version"
        $activeOrdo = if (Test-Path $ordoVersionFile) { (Get-Content -Raw $ordoVersionFile).Trim() } else { "" }
        $activePraxis = if (Test-Path $praxisVersionFile) { (Get-Content -Raw $praxisVersionFile).Trim() } else { "" }

        if ($required.ordo -and ([string]$required.ordo -ne $activeOrdo)) {
            Write-Host "ordo requirement mismatch: required $($required.ordo), active $(if ($activeOrdo) { $activeOrdo } else { 'none' })"
            $failed = $true
        }

        if ($required.praxis -and ([string]$required.praxis -ne $activePraxis)) {
            Write-Host "praxis requirement mismatch: required $($required.praxis), active $(if ($activePraxis) { $activePraxis } else { 'none' })"
            $failed = $true
        }
    }
    else {
        Write-Host "toolchain manifest: not present; latest releases will be used by setup"
    }

    if ($failed) { exit 1 }
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
    "doctor" { Invoke-Doctor }
    "version" {
        $cmd = Join-Path $BinDir "praxis.cmd"
        if (Test-Path $cmd) { & $cmd --version } else { Write-Host "echelon bootstrap" }
    }
    "--version" {
        $cmd = Join-Path $BinDir "praxis.cmd"
        if (Test-Path $cmd) { & $cmd --version } else { Write-Host "echelon bootstrap" }
    }
    "-v" {
        $cmd = Join-Path $BinDir "praxis.cmd"
        if (Test-Path $cmd) { & $cmd --version } else { Write-Host "echelon bootstrap" }
    }
    "help" { Show-Usage }
    "--help" { Show-Usage }
    "-h" { Show-Usage }
    default { Show-Usage; exit 2 }
}
