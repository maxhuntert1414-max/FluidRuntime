[CmdletBinding()]
param(
    [string]$NativeDirectory = (Join-Path $PSScriptRoot '../native/build/Release'),
    [string]$OutputPath = (Join-Path $PSScriptRoot '../artifacts/memory-foundation.json'),
    [switch]$Hardware
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$native = (Resolve-Path -LiteralPath $NativeDirectory).Path
& (Join-Path $native 'fluidruntime-memory-coherence-tests.exe')
if ($LASTEXITCODE) { throw 'Memory coherence tests failed.' }
$adapter = if ($Hardware) { '--hardware' } else { '--warp' }
$lines = & (Join-Path $native 'fluidruntime-memory-readiness.exe') $adapter
if ($LASTEXITCODE) { throw 'Owned memory foundation lab failed; no readiness claim.' }
$report = $lines -join "`n" | ConvertFrom-Json
if ($report.mode -ne 'fluidruntime-memory-foundation-v0.1' -or -not $report.foundation_passed -or
    $report.unified_memory_active -or $report.third_party_authority -or
    -not $report.cross_process_read_only_mapping -or $report.verified_roundtrips -ne 4 -or
    $report.payload_bytes -ne 16384 -or $report.logical_gpu_copy_bytes -ne 131072 -or
    $report.logical_gpu_bytes_omitted -ne 0 -or $report.debug_errors -ne 0 -or
    [bool]$report.hardware -ne $Hardware.IsPresent) {
    throw 'Memory foundation report violated its evidence contract.'
}
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
[IO.Directory]::CreateDirectory((Split-Path $output -Parent)) | Out-Null
[IO.File]::WriteAllText($output, ($report | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
Write-Host "Foundation passed on $($report.adapter). Report: $output"
Write-Host 'No copy was removed. Unified memory is not enabled; the cooperative arena is next.'
