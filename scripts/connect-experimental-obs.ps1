# SPDX-License-Identifier: MIT
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$settingsPath = Join-Path $projectRoot 'local\obs-experiment.json'
$settings = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
$configPath = Join-Path $projectRoot 'local\config.json'
$health = $null
try { $health = Invoke-RestMethod 'http://127.0.0.1:17963/api/health' -TimeoutSec 2 } catch { }
$headers = @{}
if ($health) {
    if ($health.name -ne 'NaSeqMen.Executor') { throw 'Порт занят другой программой.' }
    $headers['X-NaSeqMen-Token'] = (Get-Content -LiteralPath (Join-Path $projectRoot 'local\api-token.txt') -Raw).Trim()
    $config = Invoke-RestMethod 'http://127.0.0.1:17963/api/config' -Headers $headers
} else {
    $source = if (Test-Path -LiteralPath $configPath) { $configPath } else { Join-Path $projectRoot 'examples\default.json' }
    $config = Get-Content -LiteralPath $source -Raw -Encoding UTF8 | ConvertFrom-Json
}
$config.dryRun = $true
$config.connections = @($config.connections | Where-Object { $_.id -ne 'obs-experiment' }) + @([pscustomobject]@{
    id = 'obs-experiment'; name = 'OBS для экспериментов'; module = 'obs'; enabled = $true
    settings = @{ url = $settings.address; password = $settings.password }
})
$newNodes = @(
    @{ id='obs-query-input'; name='Проверить OBS'; kind='source'; module='test'; order=2; settings=@{control='obs-info'} },
    @{ id='obs-query'; name='OBS: запрос версии'; kind='action'; module='obs'; connectionId='obs-experiment'; operation='request'; order=2; settings=@{type='GetVersion';data='{}'} },
    @{ id='obs-response'; name='OBS: ответ с версией'; kind='source'; module='obs'; connectionId='obs-experiment'; order=3; outputType='text'; settings=@{source='response';control='GetVersion'} },
    @{ id='obs-response-text'; name='Показать ответ в диагностике'; kind='transform'; operation='text'; order=3; settings=@{template='OBS: {value}'} }
)
$ids = @($newNodes | ForEach-Object { $_.id })
$config.nodes = @($config.nodes | Where-Object { $_.id -notin $ids }) + $newNodes
$config.edges = @($config.edges | Where-Object { $_.from -notin $ids -and $_.to -notin $ids }) + @(
    @{from='obs-query-input';to='obs-query'}, @{from='obs-response';to='obs-response-text'}
)
$json = $config | ConvertTo-Json -Depth 30
if ($health) {
    $result = Invoke-RestMethod 'http://127.0.0.1:17963/api/config' -Method Put -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($json))
    if (!$result.valid) { throw ($result.errors -join '; ') }
} else {
    [IO.File]::WriteAllText($configPath, $json, [Text.UTF8Encoding]::new($false))
}
Write-Output 'Соединение OBS и две цепочки добавлены. Пробный режим включён; пароль сохранён только локально.'
