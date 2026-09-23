param(
    [string]$InstallBase = $(if ($env:ECHELON_HOME) { $env:ECHELON_HOME } else { Join-Path $HOME ".echelon" })
)

$ErrorActionPreference = "Stop"
$temp = Join-Path ([IO.Path]::GetTempPath()) ("echelon-toolchain-" + [Guid]::NewGuid().ToString("N") + ".ps1")

try {
    Invoke-WebRequest -UseBasicParsing -Uri "https://raw.githubusercontent.com/kemiller2002/praxis/main/scripts/install-native.ps1" -OutFile $temp
    & powershell -NoProfile -ExecutionPolicy Bypass -File $temp -InstallBase $InstallBase
    if ($LASTEXITCODE -ne 0) { throw "Praxis installer exited with code $LASTEXITCODE." }

    & (Join-Path $InstallBase "bin\echelon.cmd") install ordo
    if ($LASTEXITCODE -ne 0) { throw "Ordo installation failed with code $LASTEXITCODE." }

    Write-Host "Echelon engineering toolchain installed."
    & (Join-Path $InstallBase "bin\echelon.cmd") doctor
    if ($LASTEXITCODE -ne 0) { throw "Echelon Doctor reported an unhealthy toolchain." }
}
finally {
    if (Test-Path $temp) { Remove-Item -Force $temp }
}
