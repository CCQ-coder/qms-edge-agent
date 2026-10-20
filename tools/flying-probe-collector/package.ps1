param(
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$toolDirectory = [IO.Path]::GetFullPath($PSScriptRoot)
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $toolDirectory '..\..'))
$defaultOutput = Join-Path $repositoryRoot 'outputs\packages'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = $defaultOutput }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)

& (Join-Path $toolDirectory 'build.cmd')
if ($LASTEXITCODE -ne 0) { throw "Collector build failed with exit code $LASTEXITCODE" }

$binaryDirectory = Join-Path $repositoryRoot 'outputs\flying-probe-collector'
$stageParent = Join-Path $repositoryRoot 'outputs\package-stage'
$stage = Join-Path $stageParent ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $stage, $OutputDirectory | Out-Null
try {
    foreach ($name in @('FlyingProbeCollector.exe', 'FlyingProbeCollector.Console.exe')) {
        Copy-Item -LiteralPath (Join-Path $binaryDirectory $name) -Destination (Join-Path $stage $name) -ErrorAction Stop
    }
    foreach ($name in @('README.md', 'start-collector.cmd')) {
        Copy-Item -LiteralPath (Join-Path $toolDirectory $name) -Destination (Join-Path $stage $name) -ErrorAction Stop
    }
    # Construct release templates here: the source or build-output INI may contain site credentials.
    $blankIni = @'
; 飞针机文件采集程序。先在窗口中填写现场 QMS 地址与网关令牌。
[Source]
SourceRoot=\\Scbgx\数字化\飞针机
ScanIntervalSeconds=30

[QMS]
QmsBaseUrl=http://127.0.0.1:8080
GatewayToken=REPLACE_WITH_QMS_GATEWAY_TOKEN
BearerToken=
RequestTimeoutSeconds=20

[Storage]
QueueFile=.\data\pending-events.json
CheckpointFile=.\data\scan-checkpoints.json
LogFile=.\logs\flying-probe-collector.log
'@
    $utf8 = [System.Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText((Join-Path $stage 'FlyingProbeCollector.ini.example'), $blankIni + [Environment]::NewLine, $utf8)
    [IO.File]::WriteAllText((Join-Path $stage 'FlyingProbeCollector.hosts.json.example'),
        '{"probeIntervalSeconds":60,"failureThreshold":3,"targets":[]}' + [Environment]::NewLine, $utf8)
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $zip = Join-Path $OutputDirectory "FlyingProbeCollector-$stamp.zip"
    if (Test-Path -LiteralPath $zip) {
        $zip = Join-Path $OutputDirectory "FlyingProbeCollector-$stamp-$([guid]::NewGuid().ToString('N').Substring(0, 6)).zip"
    }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
    Write-Output $zip
}
finally {
    $safeParent = [IO.Path]::GetFullPath($stageParent).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $safeStage = [IO.Path]::GetFullPath($stage)
    if ($safeStage.StartsWith($safeParent, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $safeStage)) {
        Remove-Item -LiteralPath $safeStage -Recurse -Force
    }
}
