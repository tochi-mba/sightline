#Requires -Version 7.2
<#
.SYNOPSIS
    Runs the benchmark suite and compares it with the baseline, or records a new baseline.

.DESCRIPTION
    Builds the suite in Release, runs it (all of it, or the benchmarks -Filter names), writes the
    results to artifacts/bench/<time>/, and compares them with benchmarks/baseline/dotnet through
    tools/scripts/bench_compare.py. The exit code is the comparison's: 0 no regression, 1 a
    regression, 2 nothing to compare.

    -Record replaces the baseline with this run instead, and writes benchmarks/baseline/manifest.json
    describing the machine it ran on, because a figure means nothing without the hardware behind it.
    Record on an idle machine, plugged in: a build running beside the suite is noise in every number.

.EXAMPLE
    ./tools/scripts/bench.ps1
.EXAMPLE
    ./tools/scripts/bench.ps1 -Filter '*LiveView*'
.EXAMPLE
    ./tools/scripts/bench.ps1 -Record
#>
[CmdletBinding()]
param(
    [string]$Filter = '*',
    [switch]$Record,
    [switch]$Provisional
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..' '..')
$project = Join-Path $root 'benchmarks' 'dotnet' 'Sightline.Benchmarks'
$baseline = Join-Path $root 'benchmarks' 'baseline' 'dotnet'
$run = Join-Path $root 'artifacts' 'bench' (Get-Date -Format 'yyyyMMdd-HHmmss')

Push-Location $root
try {
    dotnet run -c Release --project $project -- --filter $Filter --artifacts $run
    if ($LASTEXITCODE -ne 0) {
        Write-Error "The suite did not finish cleanly (exit $LASTEXITCODE); a figure would be missing, so nothing was compared or recorded."
    }

    if (-not $Record) {
        python (Join-Path $root 'tools' 'scripts' 'bench_compare.py') $run --baseline $baseline
        exit $LASTEXITCODE
    }

    if (Test-Path $baseline) { Remove-Item -Recurse -Force $baseline }
    New-Item -ItemType Directory -Force $baseline | Out-Null
    Get-ChildItem $run -Recurse -Filter '*-report-full.json' | Copy-Item -Destination $baseline

    $cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
    $os = Get-CimInstance Win32_OperatingSystem
    $plan = (powercfg /getactivescheme) -replace '^.*\((.*)\).*$', '$1'
    $manifest = [ordered]@{
        recorded    = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')
        commit      = (git rev-parse HEAD)
        provisional = [bool]$Provisional
        filter      = $Filter
        machine     = [ordered]@{
            cpu       = $cpu.Name.Trim()
            cores     = $cpu.NumberOfCores
            threads   = $cpu.NumberOfLogicalProcessors
            memoryGB  = [math]::Round($os.TotalVisibleMemorySize / 1MB, 1)
            os        = "$($os.Caption) $($os.Version)"
            powerPlan = $plan
        }
        dotnet      = [ordered]@{
            sdk     = (dotnet --version)
            runtime = ((dotnet --list-runtimes | Select-String 'Microsoft.NETCore.App') | Select-Object -Last 1).ToString().Split(' ')[1]
        }
    }
    $manifest | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $root 'benchmarks' 'baseline' 'manifest.json')
    Write-Host "Baseline recorded from $run."
}
finally {
    Pop-Location
}
