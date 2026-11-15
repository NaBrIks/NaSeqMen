# SPDX-License-Identifier: MIT
[CmdletBinding()]
param([switch]$NoBrowser, [switch]$Rebuild)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$webExe = Join-Path $projectRoot 'artifacts\alpha\web\NaSeqMen.Editor.Web.exe'
$url = 'http://127.0.0.1:17964'
$health = $null
try { $health = Invoke-RestMethod "$url/web-health" -TimeoutSec 2 } catch { }
if ($health -and $health.name -ne 'NaSeqMen.Editor.Web') { throw 'Порт 17964 занят другой программой.' }
if ($Rebuild -and $health) { throw 'Для пересборки сначала завершите NaSeqMen.Editor.Web; WPF и исполнитель можно оставить работающими.' }
if ($Rebuild -or !(Test-Path -LiteralPath $webExe)) {
    Push-Location $projectRoot
    try {
        & dotnet publish 'src\NaSeqMen.Editor.Web\NaSeqMen.Editor.Web.csproj' -c Release --no-self-contained -o 'artifacts\alpha\web'
        if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать HTML-редактор.' }
    } finally { Pop-Location }
}
& (Join-Path $PSScriptRoot 'start.ps1') -ExecutorOnly
if (!$health) {
    $localDir = Join-Path $projectRoot 'local'
    $process = Start-Process -FilePath $webExe -ArgumentList @('--root', ('"' + $projectRoot + '"')) -WorkingDirectory $projectRoot -WindowStyle Hidden -RedirectStandardOutput (Join-Path $localDir 'web.stdout.log') -RedirectStandardError (Join-Path $localDir 'web.stderr.log') -PassThru
    $process.Id | Set-Content -LiteralPath (Join-Path $localDir 'web.pid')
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        Start-Sleep -Milliseconds 250
        if ($process.HasExited) { throw 'HTML-редактор завершился; см. local\web.stderr.log.' }
        try { $health = Invoke-RestMethod "$url/web-health" -TimeoutSec 1; break } catch { }
    }
    if (!$health -or $health.name -ne 'NaSeqMen.Editor.Web') { throw 'HTML-редактор не подтвердил готовность за 10 секунд.' }
}
if (!$NoBrowser) { Start-Process $url | Out-Null }
Write-Output "NaSeqMen HTML: $url"
