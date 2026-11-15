# SPDX-License-Identifier: MIT
[CmdletBinding()]
param([switch]$Rebuild)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$desktop = Join-Path $projectRoot 'artifacts\alpha\desktop\NaSeqMen.Desktop.exe'
if ($Rebuild -or !(Test-Path -LiteralPath $desktop)) {
    Push-Location $projectRoot
    try {
        & dotnet publish 'src\NaSeqMen.Desktop\NaSeqMen.Desktop.csproj' -c Release --no-self-contained -o 'artifacts\alpha\desktop'
        if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать настольное окно NaSeqMen.' }
    } finally { Pop-Location }
}
Start-Process -FilePath $desktop -ArgumentList @('--root', ('"' + $projectRoot + '"')) -WorkingDirectory $projectRoot | Out-Null
