<#
.SYNOPSIS
  Windows development helper for FinXml Processor.
.DESCRIPTION
  dev.ps1 build      Restore and build the solution (Release).
  dev.ps1 test       Run all test projects.
  dev.ps1 run        Launch the desktop app against an isolated data folder (.dev-home).
  dev.ps1 cli ...    Run the worker CLI, e.g. dev.ps1 cli self-test
  dev.ps1 gen        Generate a 200 MB synthetic benchmark input under benchmarks/.
  dev.ps1 bench      Run the benchmark against benchmarks/synthetic-200mb.xml and write results JSON.
  dev.ps1 format     Verify formatting (dotnet format --verify-no-changes).
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)] [ValidateSet('build', 'test', 'run', 'cli', 'gen', 'bench', 'format')] [string] $Command = 'build',
    [Parameter(ValueFromRemainingArguments = $true)] [string[]] $Rest
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Set-Location $root
$env:FINXML_HOME = Join-Path $root '.dev-home'

switch ($Command) {
    'build' { dotnet build FinXmlProcessor.sln -c Release }
    'test' { dotnet test FinXmlProcessor.sln -c Release --nologo }
    'run' { dotnet run --project src/FinXmlProcessor.Desktop -c Release }
    'cli' { dotnet run --project src/FinXmlProcessor.Worker -c Release -- @Rest }
    'gen' {
        New-Item -ItemType Directory -Force benchmarks | Out-Null
        dotnet run --project tools/FinXmlProcessor.TestDataGenerator -c Release -- --output benchmarks/synthetic-200mb.xml --approx-size 200MB --seed 2026 --missing-rate 0.005 --invalid-date-rate 0.003 --invalid-decimal-rate 0.003 --duplicate-rate 0.005 --special-rate 0.02 --long-field-rate 0.001 --invalid-status-rate 0.002
    }
    'bench' {
        New-Item -ItemType Directory -Force benchmarks/out | Out-Null
        dotnet run --project src/FinXmlProcessor.Worker -c Release -- benchmark --input benchmarks/synthetic-200mb.xml --output benchmarks/out --result benchmarks/result-200mb-windows.json --quiet
    }
    'format' { dotnet format FinXmlProcessor.sln --verify-no-changes }
}
