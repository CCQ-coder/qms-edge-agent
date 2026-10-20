$ErrorActionPreference = 'Stop'

$outputDirectory = Join-Path ([IO.Path]::GetTempPath()) ('fpt-package-test-' + [guid]::NewGuid().ToString('N'))
$safeTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar

try {
    $zipPath = & (Join-Path $PSScriptRoot 'package.ps1') -OutputDirectory $outputDirectory | Select-Object -Last 1
    if (-not (Test-Path -LiteralPath $zipPath)) { throw 'Package ZIP was not created.' }

    $installDirectory = Join-Path $outputDirectory 'existing-install'
    New-Item -ItemType Directory -Path $installDirectory | Out-Null
    $liveIni = Join-Path $installDirectory 'FlyingProbeCollector.ini'
    $liveHosts = Join-Path $installDirectory 'FlyingProbeCollector.hosts.json'
    [IO.File]::WriteAllText($liveIni, 'SITE_LIVE_INI')
    [IO.File]::WriteAllText($liveHosts, 'SITE_LIVE_HOSTS')
    Expand-Archive -LiteralPath $zipPath -DestinationPath $installDirectory -Force
    if ([IO.File]::ReadAllText($liveIni) -cne 'SITE_LIVE_INI' -or
        [IO.File]::ReadAllText($liveHosts) -cne 'SITE_LIVE_HOSTS') {
        throw 'Package extraction overwrote existing site configuration.'
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $expectedFiles = @(
            'FlyingProbeCollector.exe', 'FlyingProbeCollector.Console.exe',
            'FlyingProbeCollector.ini.example', 'FlyingProbeCollector.hosts.json.example',
            'README.md', 'start-collector.cmd'
        )
        if ($archive.Entries.Count -ne $expectedFiles.Count) { throw 'Package includes unexpected files.' }
        foreach ($name in $expectedFiles) {
            if ($null -eq $archive.GetEntry($name)) { throw "Package is missing $name." }
        }

        $reader = [IO.StreamReader]::new($archive.GetEntry('FlyingProbeCollector.ini.example').Open(), [Text.Encoding]::UTF8)
        try { $ini = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $sourceRoot = $ini -split "`r?`n" | Where-Object { $_.StartsWith('SourceRoot=') } | Select-Object -First 1
        $expectedSourceRoot = '\\Scbgx\' + (-join [char[]]@(0x6570, 0x5B57, 0x5316)) + '\' +
            (-join [char[]]@(0x98DE, 0x9488, 0x673A))
        if ($sourceRoot -cne ('SourceRoot=' + $expectedSourceRoot)) {
            throw 'Packaged default SourceRoot is not the expected UNC path.'
        }
        $gatewayLines = @($ini -split "`r?`n" | Where-Object { $_.StartsWith('GatewayToken=') })
        $bearerLines = @($ini -split "`r?`n" | Where-Object { $_.StartsWith('BearerToken=') })
        if ($gatewayLines.Count -ne 1 -or $gatewayLines[0] -cne 'GatewayToken=REPLACE_WITH_QMS_GATEWAY_TOKEN' -or
            $bearerLines.Count -ne 1 -or $bearerLines[0] -cne 'BearerToken=') {
            throw 'Package contains a nonblank credential.'
        }

        $reader = [IO.StreamReader]::new($archive.GetEntry('FlyingProbeCollector.hosts.json.example').Open(), [Text.Encoding]::UTF8)
        try { $hosts = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($hosts.targets.Count -ne 0) { throw 'Package contains site host mappings.' }
        Write-Output 'PASS collector package templates and file list'
    }
    finally { $archive.Dispose() }
}
finally {
    $safeOutput = [IO.Path]::GetFullPath($outputDirectory)
    if ($safeOutput.StartsWith($safeTempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        $safeOutput -match '[\\/]fpt-package-test-[0-9a-f]{32}$' -and
        (Test-Path -LiteralPath $safeOutput)) {
        Remove-Item -LiteralPath $safeOutput -Recurse -Force
    }
}
