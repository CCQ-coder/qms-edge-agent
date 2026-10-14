$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$sourcePath = Join-Path $repositoryRoot 'src\JingdeEapForwarder.cs'
$configPath = Join-Path $repositoryRoot 'config\JingdeEapForwarder.ini'
$source = Get-Content -Raw -LiteralPath $sourcePath
$config = Get-Content -Raw -LiteralPath $configPath

$requirements = [ordered]@{
    'default telemetry path' = $config.Contains('TelemetryPath=/api/v1/equipment/telemetry')
    'events request wrapper' = $source.Contains('body["events"] = events;')
    'gateway token header' = $source.Contains('request.Headers["X-Gateway-Token"] = settings.GatewayToken;')
    'placeholder gateway token' = $config.Contains('GatewayToken=REPLACE_WITH_QMS_GATEWAY_TOKEN')
}

$missing = @($requirements.GetEnumerator() | Where-Object { -not $_.Value } | ForEach-Object Key)
if ($missing.Count -gt 0) {
    Write-Error ('QMS Telemetry v1 contract check failed: ' + ($missing -join ', '))
    exit 1
}

Write-Output 'QMS Telemetry v1 consumer contract verified.'
