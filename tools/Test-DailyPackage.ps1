[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackageDirectory,
    [ValidateRange(5, 120)][int]$Seconds = 115
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path
$manifest = Get-Content -LiteralPath (Join-Path $package 'package-manifest.json') -Raw | ConvertFrom-Json
foreach ($file in $manifest.files) {
    if ([IO.Path]::GetFileName($file.name) -cne $file.name) { throw 'Manifest path is not a flat filename.' }
    $actual = (Get-FileHash -LiteralPath (Join-Path $package $file.name) -Algorithm SHA256).Hash
    if ($actual -ine $file.sha256) { throw "Package hash mismatch: $($file.name)" }
}
$runtime = Join-Path $package 'fluidruntime.exe'
$report = Join-Path $package 'smoke-monitor.json'
$beforePriority = (Get-Process -Id $PID).PriorityClass
$oldPath = $env:PATH
$oldRoot = $env:DOTNET_ROOT
try {
    # Child resolves neither dotnet, Python, nor the Visual Studio/ASAN runtimes.
    $env:PATH = "$env:SystemRoot\System32;$env:SystemRoot"
    $env:DOTNET_ROOT = Join-Path $package 'no-system-dotnet'
    & $runtime monitor --pid $PID --seconds $Seconds --out $report
    if ($LASTEXITCODE) { throw 'Portable monitoring failed.' }
} finally {
    $env:PATH = $oldPath
    $env:DOTNET_ROOT = $oldRoot
}
$result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
if ($result.mode -ne 'fluidruntime-monitor-v0.1' -or -not $result.read_only -or
    $result.would_modify_system -or $result.state -ne 'stopped' -or $result.stop_reason -ne 'duration' -or
    $result.total_samples -lt 1 -or $result.samples.Count -gt 600) {
    throw 'Portable monitoring report failed its read-only/lifecycle contract.'
}
if ($Seconds -ge 115 -and $result.total_samples -le 100) {
    throw 'Long run did not cross the native session renewal boundary.'
}
if ($result.gpu_status -in @('unavailable', 'disabled', 'starting') -or $null -ne $result.warning) {
    throw 'The packaged native probe did not stay healthy. Missing individual GPU counters are permitted.'
}
if ((Get-Process -Id $PID).PriorityClass -ne $beforePriority) { throw 'Target priority changed.' }
if ((Get-Item -LiteralPath $report).Length -gt 1MB) { throw 'Rolling report exceeded the expected test bound.' }
Write-Host "PASS: $($result.total_samples) samples; healthy native stream; target untouched; $report"
