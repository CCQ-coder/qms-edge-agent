param(
    [string]$ReleaseDirectory = (Join-Path $PSScriptRoot "..\outputs\jingde-eap-forwarder")
)

$ErrorActionPreference = "Stop"
$releaseDirectory = [System.IO.Path]::GetFullPath($ReleaseDirectory)
$requiredFiles = @(
    "JingdeEapForwarder.exe",
    "JingdeEapForwarder.ini",
    "README.md",
    "start-gateway.cmd",
    "install-autostart.cmd"
)

foreach ($file in $requiredFiles) {
    $path = Join-Path $releaseDirectory $file
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required release file is missing: $path"
    }
}

$scratchDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("qms-edge-agent-smoke-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $scratchDirectory | Out-Null

try {
    $executable = Join-Path $releaseDirectory "JingdeEapForwarder.exe"
    $config = Join-Path $releaseDirectory "JingdeEapForwarder.ini"
    $scratchExecutable = Join-Path $scratchDirectory "JingdeEapForwarder.exe"
    $unrelatedDirectory = Join-Path $scratchDirectory "unrelated-working-directory"
    New-Item -ItemType Directory -Path $unrelatedDirectory | Out-Null
    Copy-Item -LiteralPath $executable -Destination $scratchExecutable

    $withoutConfig = Start-Process -FilePath $scratchExecutable -ArgumentList "--self-test" -WorkingDirectory $unrelatedDirectory -Wait -PassThru
    if ($withoutConfig.ExitCode -eq 0) {
        throw "Release self-test succeeded without its co-located INI file."
    }

    Copy-Item -LiteralPath $config -Destination (Join-Path $scratchDirectory "JingdeEapForwarder.ini")
    $withConfig = Start-Process -FilePath $scratchExecutable -ArgumentList "--self-test" -WorkingDirectory $unrelatedDirectory -Wait -PassThru
    if ($withConfig.ExitCode -ne 0) {
        throw "Release self-test failed after restoring its co-located INI file (exit $($withConfig.ExitCode))."
    }
}
finally {
    if (Test-Path -LiteralPath $scratchDirectory) {
        Remove-Item -LiteralPath $scratchDirectory -Recurse -Force
    }
}

Write-Host "Release resource smoke check passed."
