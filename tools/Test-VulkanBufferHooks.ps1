[CmdletBinding()]
param(
    [string]$BuildPath = "native/build-vulkan",
    [ValidateSet("Release", "Debug")][string]$Configuration = "Release",
    [ValidateRange(1, 10)][int]$Pairs = 2,
    [string]$OutputPrefix = "vulkan-buffer-hook"
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ($OutputPrefix -notmatch '^[a-zA-Z0-9_-]+$') { throw "Invalid output prefix." }
if (-not [IO.Path]::IsPathRooted($BuildPath)) { $BuildPath = Join-Path $root $BuildPath }
$native = (Resolve-Path (Join-Path $BuildPath $Configuration)).Path
$target = Join-Path $native "fluidruntime-vulkan-transfer-target.exe"
$library = Join-Path $native "fluidruntime-vulkan-transfer.dll"
$runtime = Join-Path $root "src/FluidRuntime/bin/Release/net10.0/fluidruntime.dll"
$artifacts = Join-Path $root "artifacts"
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
for ($pair = 0; $pair -lt $Pairs; $pair++) {
    $reports = @()
    $order = if ($pair % 2 -eq 0) { @($false, $true) } else { @($true, $false) }
    foreach ($observe in $order) {
        $mode = if ($observe) { "observed" } else { "baseline" }
        $out = Join-Path $artifacts "$OutputPrefix-$pair-$mode.json"
        # The owned target checks exact GPU readback and original-copy execution
        # internally. Its nonzero exit is also propagated into the session failure.
        & dotnet $runtime app-session --exe $target --layer-dir $native --out $out `
            --seconds 10 --acknowledge-no-anticheat true `
            --observe-vulkan $observe.ToString().ToLowerInvariant() -- `
            --library $library --mode baseline --candidate-count 16 --hold-ms 1000
        if ($LASTEXITCODE -ne 0) { throw "Buffer hook $mode session failed: $out" }
        $report = Get-Content -LiteralPath $out -Raw | ConvertFrom-Json
        if ($report.schema -ne "fluidruntime-application-session-v4" -or $report.failure -or
            -not $report.process_exited -or $report.exit_code -ne 0 -or
            $report.layer_verified -ne $observe -or $report.native_actuation_enabled -or
            $report.performance_claim_allowed -or $null -ne $report.windows_priority) {
            throw "Unexpected session identity/lifecycle/authority: $out"
        }
        $c = $report.samples[-1].vulkan
        if ($observe) {
            $classified = $c.host_to_device_copy_bytes + $c.device_to_host_copy_bytes +
                $c.device_to_device_copy_bytes + $c.host_to_host_copy_bytes + $c.shared_memory_copy_bytes
            if ($c.buffers_created -ne 6 -or $c.buffers_destroyed -ne 6 -or $c.live_buffers -ne 0 -or
                $c.live_bytes -ne 0 -or $c.active_devices -ne 0 -or $c.active_instances -ne 0 -or
                $c.buffer_copies -ne 32 -or $c.buffer_copy_bytes -ne 134217728 -or
                $classified -ne $c.buffer_copy_bytes -or $c.unknown_buffer_copy_bytes -ne 0 -or
                $c.api_errors -ne 0 -or $c.untracked_buffers -ne 0 -or $c.untracked_allocations -ne 0 -or
                $c.buffer_binding_failures -ne 0 -or $c.unclassified_buffer_bindings -ne 0 -or
                $c.counter_overflows -ne 0 -or $c.telemetry_failures -ne 0 -or $c.submits -ne 2 -or
                $c.successful_submit_calls -ne 2 -or $c.failed_submit_calls -ne 0 -or
                $c.submitted_buffer_copies -ne 32 -or $c.submitted_buffer_copy_bytes -ne 134217728 -or
                $c.submitted_primary_command_buffers -ne 4 -or $c.submitted_secondary_command_buffers -ne 0 -or
                $c.resubmitted_command_buffers -ne 0 -or $c.live_command_buffers -ne 0 -or
                $c.unresolved_submit_calls -ne 0 -or $c.command_tracking_failures -ne 0 -or
                $c.command_tracking_overflows -ne 0 -or $c.untracked_command_buffers -ne 0 -or
                $c.untracked_command_pools -ne 0 -or $c.completed_submit_calls -ne 2 -or
                $c.completed_buffer_copies -ne 32 -or $c.completed_buffer_copy_bytes -ne 134217728 -or
                $c.pending_tracked_submits -ne 0 -or $c.abandoned_tracked_submits -ne 0 -or
                $c.completion_tracking_failures -ne 0 -or $c.ambiguous_fence_waits -ne 0 -or
                $c.live_fences -ne 0 -or $c.untracked_fences -ne 0) {
                throw "Buffer lifecycle or recorded-transfer attribution mismatch: $out"
            }
        } elseif ($c.PSObject.Properties | Where-Object { $_.Value -ne 0 }) {
            throw "Uninstrumented baseline unexpectedly contains hook counters: $out"
        }
        $reports += $report
    }
    foreach ($key in @("executable_sha256", "layer_sha256", "arguments_sha256")) {
        if ($reports[0].$key -ne $reports[1].$key) { throw "A/B identity changed: $key" }
    }
}
Write-Host "$Pairs alternating buffer-hook pairs passed. No performance claim is inferred."
