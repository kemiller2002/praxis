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
  echelon doctor [--fix] [--verbose] [--json]
  echelon inventory [--json]
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
        $candidate = @(& git rev-parse --show-toplevel 2>$null)
        $gitExit = $LASTEXITCODE
        if ($gitExit -eq 0 -and $candidate.Count -gt 0) {
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

function Normalize-VersionOutput([string]$Raw) {
    if (-not $Raw) { return "" }
    $match = [regex]::Match($Raw, '(?<![0-9A-Za-z])([0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?)')
    if ($match.Success) { return $match.Groups[1].Value }
    return ""
}

function Get-NativeToolInventory {
    if (-not (Test-Path $ToolsDir)) { return @() }

    return @(
        Get-ChildItem -Path $ToolsDir -Directory -ErrorAction SilentlyContinue |
            Sort-Object Name |
            ForEach-Object {
                [pscustomobject]@{
                    name = $_.Name
                    activeVersion = $(if (Get-ActiveVersion $_.Name) { Get-ActiveVersion $_.Name } else { $null })
                    installedVersions = @(Get-InstalledVersions $_.Name)
                }
            }
    )
}

function Get-RepositoryComponents([string]$Root) {
    $echelonDir = Join-Path $Root ".echelon"
    if (-not (Test-Path $echelonDir)) { return @() }

    $items = @()
    foreach ($file in @(Get-ChildItem -Path $echelonDir -Filter "*.json" -File -ErrorAction SilentlyContinue | Sort-Object Name)) {
        if ($file.Name -eq "toolchain.json" -or $file.Name -like "*.config.json") { continue }

        try {
            $manifest = Get-Content -Raw $file.FullName | ConvertFrom-Json
        }
        catch {
            continue
        }

        $tool = [string]$manifest.tool
        $version = [string]$manifest.installedVersion
        if (-not $version) { $version = [string]$manifest.version }
        if (-not $tool -and $version) { $tool = [IO.Path]::GetFileNameWithoutExtension($file.Name) }
        if (-not $tool) { continue }
        if (-not $version) { $version = "unknown" }

        $items += [pscustomobject]@{
            tool = $tool
            installedVersion = $version
            manifest = $file.FullName
        }
    }

    return @($items)
}

function Get-EchelonNpmPackages([string]$Root) {
    $scope = Join-Path $Root "node_modules\@echelon-foundry"
    if (-not (Test-Path $scope)) { return @() }

    $items = @()
    foreach ($directory in @(Get-ChildItem -Path $scope -Directory -ErrorAction SilentlyContinue | Sort-Object Name)) {
        $packageJson = Join-Path $directory.FullName "package.json"
        if (-not (Test-Path $packageJson)) { continue }

        try {
            $manifest = Get-Content -Raw $packageJson | ConvertFrom-Json
        }
        catch {
            continue
        }

        $name = [string]$manifest.name
        $version = [string]$manifest.version
        if (-not $name) { $name = "@echelon-foundry/$($directory.Name)" }
        if (-not $version) { $version = "unknown" }

        $items += [pscustomobject]@{
            package = $name
            version = $version
            manifest = $packageJson
        }
    }

    return @($items)
}

function Get-CommandHealth {
    $items = @()
    $activeOrdo = Get-ActiveVersion "ordo"
    $activePraxis = Get-ActiveVersion "praxis"

    foreach ($name in @("ordo", "sde", "praxis", "ros", "echelon")) {
        $cmd = Get-CommandFile $name
        $healthy = $false
        $versionOutput = $null
        $expectedVersion = $null

        switch ($name) {
            "ordo" { $expectedVersion = $activeOrdo }
            "sde" { $expectedVersion = $activeOrdo }
            "praxis" { $expectedVersion = $activePraxis }
            "ros" { $expectedVersion = $activePraxis }
        }

        if (Test-Path $cmd) {
            if ($name -eq "echelon") {
                $healthy = $true
            }
            else {
                $versionLines = @(& $cmd --version 2>$null)
                $commandExit = $LASTEXITCODE
                $firstLine = @($versionLines | Select-Object -First 1)
                if ($commandExit -eq 0 -and $firstLine.Count -gt 0 -and $firstLine[0]) {
                    $versionOutput = Normalize-VersionOutput ([string]$firstLine[0])
                    if ($versionOutput -and (-not $expectedVersion -or $versionOutput -eq $expectedVersion)) {
                        $healthy = $true
                    }
                }
            }
        }

        $items += [pscustomobject]@{
            name = $name
            healthy = $healthy
            path = $cmd
            version = $versionOutput
            expectedVersion = $(if ($expectedVersion) { $expectedVersion } else { $null })
        }
    }

    return @($items)
}

function Get-DoctorReport {
    $errors = 0
    $warnings = 0

    $pathConfigured = Test-BinOnPath
    if (-not $pathConfigured) { $warnings++ }

    $activeOrdo = Get-ActiveVersion "ordo"
    $activePraxis = Get-ActiveVersion "praxis"
    if (-not $activeOrdo) { $errors++ }
    if (-not $activePraxis) { $errors++ }

    $commands = @(Get-CommandHealth)
    foreach ($command in $commands) {
        if (-not $command.healthy) { $errors++ }
    }

    $manifestPath = Get-ManifestPath
    $required = Get-ManifestVersions
    if ($manifestPath) {
        if (-not $required.ordo) { $warnings++ }
        if (-not $required.praxis) { $warnings++ }
        if ($required.ordo -and $activeOrdo -ne $required.ordo) { $errors++ }
        if ($required.praxis -and $activePraxis -ne $required.praxis) { $errors++ }
    }
    else {
        $warnings++
    }

    $insideGit = $false
    if (Get-Command git -ErrorAction SilentlyContinue) {
        & git rev-parse --is-inside-work-tree *> $null
        $insideGit = $LASTEXITCODE -eq 0
    }
    $repoRoot = Get-RepositoryRoot
    if (-not $insideGit) { $warnings++ }

    $ordoStatus = "not-installed"
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
            if ($ordoExit -eq 0) { $ordoStatus = "valid" }
            else { $ordoStatus = "invalid"; $errors++ }
        }
        else {
            $ordoStatus = "invalid"
            $errors++
        }
    }

    $praxisStatus = "not-installed"
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
            if ($praxisExit -eq 0) { $praxisStatus = "valid" }
            else { $praxisStatus = "invalid"; $errors++ }
        }
        else {
            $praxisStatus = "invalid"
            $errors++
        }
    }

    $health = if ($errors -gt 0) { "error" } elseif ($warnings -gt 0) { "warning" } else { "healthy" }
    $exitCode = if ($errors -gt 0) { 1 } else { 0 }
    $architecture = if ($env:PROCESSOR_ARCHITECTURE) { $env:PROCESSOR_ARCHITECTURE } else { "unknown" }

    $findings = @()
    if (-not $pathConfigured) {
        $findings += [pscustomobject]@{ code = "ECHELON-DOC-001"; severity = "warning"; message = "$BinDir is not on PATH"; remediation = "Add the Echelon bin directory to PATH." }
    }
    if (-not $insideGit) {
        $findings += [pscustomobject]@{ code = "ECHELON-DOC-002"; severity = "warning"; message = "The current directory is not inside a Git repository."; remediation = "Run Doctor from a repository when repository diagnostics are required." }
    }
    if (-not $activeOrdo) {
        $findings += [pscustomobject]@{ code = "ECHELON-DOC-010"; severity = "error"; message = "Ordo has no active version."; remediation = "Run echelon doctor --fix or echelon install ordo." }
    }
    if (-not $activePraxis) {
        $findings += [pscustomobject]@{ code = "ECHELON-DOC-011"; severity = "error"; message = "Praxis has no active version."; remediation = "Run echelon doctor --fix or echelon install praxis." }
    }
    foreach ($command in $commands) {
        if (-not $command.healthy) {
            if ($command.version -and $command.expectedVersion -and $command.version -ne $command.expectedVersion) {
                $findings += [pscustomobject]@{
                    code = "ECHELON-DOC-021"
                    severity = "error"
                    message = "$($command.name) reports $($command.version) but the active version is $($command.expectedVersion)."
                    remediation = "Run echelon doctor --fix to rebuild the command wrapper."
                }
            }
            else {
                $findings += [pscustomobject]@{
                    code = "ECHELON-DOC-020"
                    severity = "error"
                    message = "$($command.name) is missing or does not execute successfully."
                    remediation = "Run echelon doctor --fix to repair Echelon command wrappers."
                }
            }
        }
    }

    if ($manifestPath) {
        if (-not $required.ordo) {
            $findings += [pscustomobject]@{ code = "ECHELON-DOC-031"; severity = "warning"; message = "The toolchain manifest does not pin Ordo."; remediation = "Add an exact Ordo version to .echelon/toolchain.json." }
        }
        if (-not $required.praxis) {
            $findings += [pscustomobject]@{ code = "ECHELON-DOC-032"; severity = "warning"; message = "The toolchain manifest does not pin Praxis."; remediation = "Add an exact Praxis version to .echelon/toolchain.json." }
        }
        if ($required.ordo -and $activeOrdo -ne $required.ordo) {
            $findings += [pscustomobject]@{ code = "ECHELON-DOC-033"; severity = "error"; message = "Ordo requires $($required.ordo) but $activeOrdo is active."; remediation = "Run echelon doctor --fix." }
        }
        if ($required.praxis -and $activePraxis -ne $required.praxis) {
            $findings += [pscustomobject]@{ code = "ECHELON-DOC-034"; severity = "error"; message = "Praxis requires $($required.praxis) but $activePraxis is active."; remediation = "Run echelon doctor --fix." }
        }
    }
    else {
        $findings += [pscustomobject]@{ code = "ECHELON-DOC-030"; severity = "warning"; message = "No .echelon/toolchain.json is present."; remediation = "Add a toolchain manifest to make the repository reproducible." }
    }

    if ($ordoStatus -eq "invalid") {
        $findings += [pscustomobject]@{ code = "ECHELON-DOC-040"; severity = "error"; message = "Ordo repository verification failed."; remediation = "Run ordo doctor in the repository for domain-specific diagnostics." }
    }
    if ($praxisStatus -eq "invalid") {
        $findings += [pscustomobject]@{ code = "ECHELON-DOC-041"; severity = "error"; message = "Praxis repository validation failed."; remediation = "Run praxis doctor in the repository for domain-specific diagnostics." }
    }

    return [pscustomobject][ordered]@{
        schemaVersion = 1
        tool = "echelon"
        command = "doctor"
        health = $health
        exitCode = $exitCode
        machine = [pscustomobject][ordered]@{
            platform = "Windows"
            architecture = $architecture
            echelonHome = $HomeDir
            binDirectory = $BinDir
            pathConfigured = [bool]$pathConfigured
        }
        nativeTools = @(Get-NativeToolInventory)
        commands = $commands
        repository = [pscustomobject][ordered]@{
            root = $repoRoot
            isGit = [bool]$insideGit
            toolchainManifest = $(if ($manifestPath) { $manifestPath } else { $null })
            requirements = [pscustomobject][ordered]@{
                ordo = $(if ($required.ordo) { $required.ordo } else { $null })
                praxis = $(if ($required.praxis) { $required.praxis } else { $null })
            }
            ordoStatus = $ordoStatus
            praxisStatus = $praxisStatus
            components = @(Get-RepositoryComponents $repoRoot)
            npmPackages = @(Get-EchelonNpmPackages $repoRoot)
        }
        findings = @($findings)
        summary = [pscustomobject][ordered]@{
            errors = $errors
            warnings = $warnings
        }
    }
}

function Invoke-Inventory([bool]$Json) {
    $repoRoot = Get-RepositoryRoot
    $nativeTools = @(Get-NativeToolInventory)
    $components = @(Get-RepositoryComponents $repoRoot)
    $npmPackages = @(Get-EchelonNpmPackages $repoRoot)

    if ($Json) {
        [pscustomobject][ordered]@{
            schemaVersion = 1
            tool = "echelon"
            command = "inventory"
            nativeTools = $nativeTools
            repository = [pscustomobject][ordered]@{
                root = $repoRoot
                components = $components
                npmPackages = $npmPackages
            }
        } | ConvertTo-Json -Depth 8 -Compress
        return
    }

    Write-Host "Echelon Inventory"
    Write-Host "================="
    Write-Host
    Write-Host "Native tools"
    if ($nativeTools.Count -eq 0) {
        Write-DoctorRow "ok" "Native tools" "none"
    }
    else {
        foreach ($native in $nativeTools) {
            $active = if ($native.activeVersion) { $native.activeVersion } else { "none" }
            $installed = if ($native.installedVersions.Count -gt 0) { $native.installedVersions -join ", " } else { "none" }
            Write-DoctorRow "ok" $native.name "active $active; installed $installed"
        }
    }

    Write-Host
    Write-Host "Repository components"
    if ($components.Count -eq 0) {
        Write-DoctorRow "ok" "Components" "none"
    }
    else {
        foreach ($component in $components) {
            Write-DoctorRow "ok" $component.tool "$($component.installedVersion) ($($component.manifest))"
        }
    }

    Write-Host
    Write-Host "Installed Echelon npm packages"
    if ($npmPackages.Count -eq 0) {
        Write-DoctorRow "ok" "npm packages" "none"
    }
    else {
        foreach ($package in $npmPackages) {
            Write-DoctorRow "ok" $package.package $package.version
        }
    }
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
    $json = $false

    foreach ($option in @($Options)) {
        if (-not $option) { continue }
        switch ($option.ToLowerInvariant()) {
            "--fix" { $fix = $true }
            "--verbose" { $verbose = $true }
            "-v" { $verbose = $true }
            "--json" { $json = $true }
            "--help" {
                Write-Host "Usage: echelon doctor [--fix] [--verbose] [--json]"
                return
            }
            "-h" {
                Write-Host "Usage: echelon doctor [--fix] [--verbose] [--json]"
                return
            }
            default {
                Write-Error "Unknown doctor option: $option"
                exit 2
            }
        }
    }

    if ($fix -and $json) {
        Write-Error "echelon doctor --fix and --json cannot be combined"
        exit 2
    }

    if ($fix) {
        Invoke-DoctorFix
    }

    if ($json) {
        $report = Get-DoctorReport
        $report | ConvertTo-Json -Depth 8 -Compress
        exit $report.exitCode
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

    foreach ($commandHealth in @(Get-CommandHealth)) {
        if ($commandHealth.healthy) {
            $shown = if ($commandHealth.version) { $commandHealth.version } else { $commandHealth.path }
            Write-DoctorRow "ok" "$($commandHealth.name) command" $shown
        }
        else {
            if ($commandHealth.version -and $commandHealth.expectedVersion -and $commandHealth.version -ne $commandHealth.expectedVersion) {
                Write-DoctorRow "error" "$($commandHealth.name) command" "$($commandHealth.version); active version is $($commandHealth.expectedVersion)"
            }
            elseif (Test-Path $commandHealth.path) {
                Write-DoctorRow "error" "$($commandHealth.name) command" "present but failed --version"
            }
            else {
                Write-DoctorRow "error" "$($commandHealth.name) command" "missing: $($commandHealth.path)"
            }
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

    Write-Host
    Write-Host "Repository components"
    $components = @(Get-RepositoryComponents $repoRoot)
    if ($components.Count -eq 0) {
        Write-DoctorRow "ok" "Components" "none"
    }
    else {
        foreach ($component in $components) {
            Write-DoctorRow "ok" $component.tool "$($component.installedVersion) ($($component.manifest))"
        }
    }

    Write-Host
    Write-Host "Installed Echelon npm packages"
    $npmPackages = @(Get-EchelonNpmPackages $repoRoot)
    if ($npmPackages.Count -eq 0) {
        Write-DoctorRow "ok" "npm packages" "none"
    }
    else {
        foreach ($package in $npmPackages) {
            Write-DoctorRow "ok" $package.package $package.version
        }
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
    "inventory" {
        $inventoryOptions = @()
        if ($Tool) { $inventoryOptions += $Tool }
        if ($Version) { $inventoryOptions += $Version }
        if ($Remaining) { $inventoryOptions += $Remaining }
        $jsonInventory = $false
        foreach ($option in $inventoryOptions) {
            if ($option -eq "--json") { $jsonInventory = $true }
            elseif ($option) { Write-Error "Usage: echelon inventory [--json]"; exit 2 }
        }
        Invoke-Inventory $jsonInventory
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
