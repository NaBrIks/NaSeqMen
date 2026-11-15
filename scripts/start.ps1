# SPDX-License-Identifier: MIT
[CmdletBinding()]
param([switch]$ExecutorOnly)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$executor = Join-Path $projectRoot 'artifacts\alpha\executor\NaSeqMen.Executor.exe'
$editor = Join-Path $projectRoot 'artifacts\alpha\editor\NaSeqMen.Editor.exe'
if (!(Test-Path -LiteralPath $executor) -or !(Test-Path -LiteralPath $editor)) {
    throw 'Сначала выполните scripts\build.ps1.'
}
$localDir = Join-Path $projectRoot 'local'
New-Item -ItemType Directory -Path $localDir -Force | Out-Null
$health = $null
try { $health = Invoke-RestMethod 'http://127.0.0.1:17963/api/health' -TimeoutSec 2 } catch { }
if ($health -and $health.name -ne 'NaSeqMen.Executor') { throw 'Порт 17963 занят другой программой.' }
if (!$health) {
    $process = Start-Process -FilePath $executor -ArgumentList @('--root', ('"' + $projectRoot + '"')) -WorkingDirectory $projectRoot -WindowStyle Hidden -RedirectStandardOutput (Join-Path $localDir 'executor.stdout.log') -RedirectStandardError (Join-Path $localDir 'executor.stderr.log') -PassThru
    $process.Id | Set-Content -LiteralPath (Join-Path $localDir 'executor.pid')
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        Start-Sleep -Milliseconds 250
        if ($process.HasExited) { throw 'Исполнитель завершился; см. local\executor.stderr.log.' }
        try { $health = Invoke-RestMethod 'http://127.0.0.1:17963/api/health' -TimeoutSec 1; break } catch { }
    }
    if (!$health) { throw 'Исполнитель не подтвердил готовность за 10 секунд.' }
}
if (!$ExecutorOnly) { Start-Process -FilePath $editor -ArgumentList @('--root', ('"' + $projectRoot + '"')) -WorkingDirectory $projectRoot | Out-Null }
Write-Output 'NaSeqMen: http://127.0.0.1:17963'
