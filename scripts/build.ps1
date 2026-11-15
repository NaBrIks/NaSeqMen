# SPDX-License-Identifier: MIT
[CmdletBinding()]
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$outputRoot = Join-Path $projectRoot 'artifacts\alpha'
Push-Location $projectRoot
try {
    $projects = @(
        @('src\NaSeqMen.Executor\NaSeqMen.Executor.csproj', 'executor'),
        @('src\NaSeqMen.Editor\NaSeqMen.Editor.csproj', 'editor'),
        @('src\NaSeqMen.Editor.Web\NaSeqMen.Editor.Web.csproj', 'web'),
        @('src\NaSeqMen.Desktop\NaSeqMen.Desktop.csproj', 'desktop'),
        @('modules\NaSeqMen.Input.Windows\NaSeqMen.Input.Windows.csproj', 'modules'),
        @('modules\NaSeqMen.Desktop.Windows\NaSeqMen.Desktop.Windows.csproj', 'modules'),
        @('modules\NaSeqMen.Obs\NaSeqMen.Obs.csproj', 'modules')
    )
    foreach ($project in $projects) {
        & dotnet publish $project[0] -c $Configuration --no-self-contained -o (Join-Path $outputRoot $project[1])
        if ($LASTEXITCODE -ne 0) { throw "Publish failed: $($project[0])" }
    }
    Write-Output "Alpha assembled: $outputRoot"
} finally { Pop-Location }
