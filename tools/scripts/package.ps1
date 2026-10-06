#Requires -Version 7.2
<#
.SYNOPSIS
    Builds what people download for Windows: the installer, the portable exe, and the command line.

.DESCRIPTION
    Two halves, because a release publishes the bytes that were built once rather than a rebuild of them.

    -Stage publishes the app and the command line, self-contained, into one folder: nobody should have to
    install .NET before finding out whether their camera works.

    -Pack turns the staged folder into the downloads:

      Sightline-Setup.exe       A per-person installer: no administrator, a Start menu entry, an entry in
                                Installed apps with an uninstaller, sightline on the PATH, and updates in place.
      Sightline-Portable.exe    The app as one file, run from anywhere, installing nothing.
      sightline-cli.exe         The command line as one file.
      *.nupkg, releases.win.json  The feed an installed copy updates itself from.
      windows-release.json      The version, and each download's size and SHA-256, for the site and the app.
      SHA256SUMS.txt            The same hashes, in the form sha256sum checks.

    Each download is also copied under a name with its version, so a release keeps every version's file
    while the stable names always mean the newest. Given neither switch, it does both halves.

    None of it is code-signed: Windows says the publisher is unknown, and the site and the release notes
    say so plainly.

.PARAMETER Version
    The version stamped into everything. Defaults to the VERSION file.

.PARAMETER OutputDirectory
    Where the downloads go. Defaults to ./dist/windows.
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputDirectory,
    [switch]$Stage,
    [switch]$Pack
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$app = Join-Path $root 'apps/windows/src/Sightline.App/Sightline.App.csproj'
$cli = Join-Path $root 'apps/windows/src/Sightline.Cli/Sightline.Cli.csproj'
$icon = Join-Path $root 'apps/windows/src/Sightline.App/Assets/sightline.ico'

function Invoke-Publish {
    param([string]$Project, [string]$Output, [string]$Version, [switch]$SingleFile)

    $arguments = @(
        'publish', $Project,
        '--configuration', 'Release',
        '--runtime', 'win-x64',
        '--self-contained', 'true',
        "-p:Version=$Version",
        '-p:DebugType=none',
        '--output', $Output,
        '--nologo'
    )
    if ($SingleFile) {
        # Skia's native libraries are unpacked from the one file at start, so the exe needs nothing beside it.
        $arguments += '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true'
    }

    dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $([IO.Path]::GetFileName($Project)) failed ($LASTEXITCODE)." }
}

function Invoke-Stage {
    param([string]$Version, [string]$Staging)

    if (Test-Path -LiteralPath $Staging) { Remove-Item -LiteralPath $Staging -Recurse -Force }
    Write-Host "`n  Publishing the app and the command line, version $Version" -ForegroundColor Green
    Invoke-Publish -Project $app -Output $Staging -Version $Version
    Invoke-Publish -Project $cli -Output $Staging -Version $Version
    foreach ($required in 'Sightline.App.exe', 'sightline.exe') {
        if (-not (Test-Path -LiteralPath (Join-Path $Staging $required))) {
            throw "$required is missing from the staged folder."
        }
    }

    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $Staging
}

function Copy-Download {
    param([string]$From, [string]$Output, [string]$Stable, [string]$Versioned)

    Copy-Item -LiteralPath $From -Destination (Join-Path $Output $Stable) -Force
    Copy-Item -LiteralPath $From -Destination (Join-Path $Output $Versioned) -Force
}

function Invoke-Pack {
    param([string]$Version, [string]$Staging, [string]$Output)

    Write-Host "`n  Building the installer" -ForegroundColor Green
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed; the vpk tool is unavailable.' }

    $velopack = Join-Path $Output 'velopack'
    dotnet vpk pack `
        --packId Sightline `
        --packVersion $Version `
        --packDir $Staging `
        --mainExe Sightline.App.exe `
        --packTitle Sightline `
        --packAuthors 'REX Technologies' `
        --icon $icon `
        --outputDir $velopack
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed ($LASTEXITCODE)." }

    $setup = Get-ChildItem -LiteralPath $velopack -Filter '*Setup.exe' | Select-Object -First 1
    if ($null -eq $setup) { throw "vpk produced no installer in $velopack." }
    Copy-Download -From $setup.FullName -Output $Output -Stable 'Sightline-Setup.exe' -Versioned "Sightline-Setup-$Version.exe"
    # The feed an installed copy reads to update itself: without it an install can never move forward.
    foreach ($feed in Get-ChildItem -LiteralPath $velopack -Include '*.nupkg', 'releases.win.json' -Recurse) {
        Copy-Item -LiteralPath $feed.FullName -Destination $Output -Force
    }

    Write-Host "`n  Building the portable exe and the command line" -ForegroundColor Green
    $single = Join-Path $Output 'single'
    Invoke-Publish -Project $app -Output (Join-Path $single 'app') -Version $Version -SingleFile
    Invoke-Publish -Project $cli -Output (Join-Path $single 'cli') -Version $Version -SingleFile
    Copy-Download -From (Join-Path $single 'app/Sightline.App.exe') -Output $Output -Stable 'Sightline-Portable.exe' -Versioned "Sightline-Portable-$Version.exe"
    Copy-Download -From (Join-Path $single 'cli/sightline.exe') -Output $Output -Stable 'sightline-cli.exe' -Versioned "sightline-cli-$Version.exe"

    Write-Host "`n  Writing the checksums and the release description" -ForegroundColor Green
    $downloads = foreach ($name in 'Sightline-Setup.exe', 'Sightline-Portable.exe', 'sightline-cli.exe') {
        $file = Get-Item -LiteralPath (Join-Path $Output $name)
        [ordered]@{
            name   = $name
            size   = $file.Length
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }

    $release = [ordered]@{ schema = 1; version = $Version; minimumWindows = '10.0.19041'; downloads = @($downloads) }
    $release | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Output 'windows-release.json') -Encoding utf8NoBOM
    $sums = Get-ChildItem -LiteralPath $Output -File |
        Where-Object { $_.Name -ne 'SHA256SUMS.txt' } |
        Sort-Object Name |
        ForEach-Object { "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)" }
    $sums | Set-Content -LiteralPath (Join-Path $Output 'SHA256SUMS.txt') -Encoding utf8NoBOM

    Remove-Item -LiteralPath $velopack, $single -Recurse -Force
    foreach ($download in $downloads) {
        Write-Host ("  {0,-26} {1,8:N1} MB" -f $download.name, ($download.size / 1MB)) -ForegroundColor Green
    }
}

Push-Location $root
try {
    if ([string]::IsNullOrWhiteSpace($Version)) {
        $Version = (Get-Content -LiteralPath (Join-Path $root 'VERSION') -Raw).Trim()
    }

    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        $OutputDirectory = Join-Path $root 'dist/windows'
    }

    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
    $staging = Join-Path (Split-Path -Parent $OutputDirectory) 'windows-staging'
    $both = -not $Stage -and -not $Pack

    if ($Stage -or $both) { Invoke-Stage -Version $Version -Staging $staging }
    if ($Pack -or $both) { Invoke-Pack -Version $Version -Staging $staging -Output $OutputDirectory }
}
finally {
    Pop-Location
}
