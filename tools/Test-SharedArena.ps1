[CmdletBinding()]
param(
    [string]$NativeDirectory = (Join-Path $PSScriptRoot '../native/build/Release'),
    [string]$OutputPath = (Join-Path $PSScriptRoot '../artifacts/shared-arena.json'),
    [switch]$Hardware
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$native = (Resolve-Path -LiteralPath $NativeDirectory).Path
& (Join-Path $native 'fluidruntime-memory-arena-fault-tests.exe')
if ($LASTEXITCODE) { throw 'Shared arena timeout/cleanup tests failed.' }
$mode = if ($Hardware) { '--hardware' } else { '--warp' }
$lines = & (Join-Path $native 'fluidruntime-memory-arena-lab.exe') $mode
if ($LASTEXITCODE) { throw 'Shared arena laboratory failed.' }
$report = $lines -join "`n" | ConvertFrom-Json
if ($report.mode -ne 'fluidruntime-shared-arena-v1' -or -not $report.passed -or
    -not $report.content_equivalent -or -not $report.read_only_peer_verified -or
    $report.unified_memory_active -or $report.third_party_authority -or
    [bool]$report.hardware -ne $Hardware.IsPresent -or $report.debug_errors -ne 0 -or
    $report.trial_pairs -ne 30 -or $report.operations_per_arm -ne 240 -or
    $report.payload_bytes -ne 16384 -or $report.backing_budget_bytes -ne 67108864 -or
    $report.peak_charged_bytes -gt $report.backing_budget_bytes -or
    $report.gpu_bytes_omitted -ne 0 -or $report.baseline.backing_allocations -ne 960 -or
    $report.reused.backing_allocations -ne 120) {
    throw 'Shared arena report violated its evidence contract.'
}
foreach ($arm in @($report.baseline, $report.reused)) {
    if ($arm.cpu_copy_bytes -ne 7864320 -or $arm.gpu_copy_bytes -ne 7864320 -or
        $arm.p50_us -le 0 -or $arm.p95_us -lt $arm.p50_us -or $arm.p99_us -lt $arm.p95_us -or
        $arm.operations_per_second -le 0 -or $arm.cpu_cycles_per_operation -le 0) {
        throw 'Invalid benchmark counters or latency distribution.'
    }
}
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
[IO.Directory]::CreateDirectory((Split-Path $output -Parent)) | Out-Null
$json = ($report | ConvertTo-Json -Depth 5).Replace("`r`n", "`n")
[IO.File]::WriteAllText($output, $json, [Text.UTF8Encoding]::new($false))
Write-Host "Shared arena passed. Report: $output"
Write-Host 'Owned allocation reuse only; no GPU copy elision or automatic intervention.'
