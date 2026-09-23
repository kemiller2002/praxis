param(
    [string]$Version,
    [string]$InstallBase = $(if ($env:ECHELON_HOME) { $env:ECHELON_HOME } else { Join-Path $HOME ".echelon" })
)

$ErrorActionPreference = "Stop"
$repo = "kemiller2002/praxis"

if (-not $Version) {
    $release = Invoke-RestMethod -Headers @{ "User-Agent" = "echelon-installer" } -Uri "https://api.github.com/repos/$repo/releases/latest"
    $Version = $release.tag_name -replace '^v', ''
}

$asset = "praxis-win-x64.zip"
$baseUrl = "https://github.com/$repo/releases/download/v$Version"
$temp = Join-Path ([IO.Path]::GetTempPath()) ("praxis-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $temp | Out-Null

try {
    $archive = Join-Path $temp $asset
    $checksums = Join-Path $temp "native-checksums.txt"
    Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl/$asset" -OutFile $archive
    Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl/native-checksums.txt" -OutFile $checksums

    $line = Get-Content $checksums | Where-Object { $_ -match ("\s" + [Regex]::Escape($asset) + "$") } | Select-Object -First 1
    if (-not $line) { throw "No checksum found for $asset." }

    $expected = ($line -split '\s+')[0].ToLowerInvariant()
    $actual = (Get-FileHash -Algorithm SHA256 $archive).Hash.ToLowerInvariant()
    if ($expected -ne $actual) { throw "Checksum verification failed for $asset." }

    Expand-Archive -Path $archive -DestinationPath $temp -Force
    $sourceRoot = Join-Path $temp "praxis-win-x64"
    $toolRoot = Join-Path $InstallBase "tools\praxis"
    $target = Join-Path $toolRoot $Version
    $binDir = Join-Path $InstallBase "bin"

    New-Item -ItemType Directory -Force -Path $toolRoot, $binDir | Out-Null
    $nativeCmd = Join-Path $target "praxis.cmd"
    $nativeExe = Join-Path $target "praxis-bin.exe"
    $versionFile = Join-Path $target "VERSION"
    if (Test-Path $target) {
        if (-not (Test-Path $nativeCmd) -or -not (Test-Path $nativeExe) -or -not (Test-Path $versionFile)) {
            throw "Existing Praxis $Version installation is incomplete. Remove $target and retry."
        }
    }
    else {
        Copy-Item -Recurse -Force $sourceRoot $target
    }
    foreach ($name in @("praxis", "ros")) {
        $cmd = Join-Path $binDir "$name.cmd"
        $cmdContent = "@echo off" + [Environment]::NewLine + 'call "' + $nativeCmd + '" %*' + [Environment]::NewLine
        Set-Content -Encoding Ascii -Path $cmd -Value $cmdContent
    }

    Copy-Item -Force (Join-Path $target "echelon.ps1") (Join-Path $binDir "echelon.ps1")
    $echelonCmd = Join-Path $binDir "echelon.cmd"
    $echelonContent = "@echo off" + [Environment]::NewLine + 'powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0echelon.ps1" %*' + [Environment]::NewLine
    Set-Content -Encoding Ascii -Path $echelonCmd -Value $echelonContent

    Set-Content -Encoding Ascii -Path (Join-Path $toolRoot "current-version") -Value $Version

    Write-Host "Installed Praxis $Version to $target"
    Write-Host "Commands: praxis, ros, and echelon under $binDir"
    Write-Host "Add $binDir to PATH if it is not already present."
}
finally {
    if (Test-Path $temp) { Remove-Item -Recurse -Force $temp }
}
