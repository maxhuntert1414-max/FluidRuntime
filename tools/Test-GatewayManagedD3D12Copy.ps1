[CmdletBinding()]
param(
    [string]$GatewayPath = "",
    [string]$TargetPath = "",
    [string]$HookPath = "",
    [string]$OutputPath = "",
    [string]$InvalidResponseOutputPath = "",
    [string]$FailClosedOutputPath = "",
    [string]$SlowResponseOutputPath = "",
    [ValidateRange(1, 30)]
    [int]$TrialPairs = 2,
    [ValidateRange(0, 5)]
    [int]$WarmupPairs = 0,
    [ValidateRange(1, 128)]
    [int]$CandidateActionCount = 128,
    [object]$Hardware = $false
)

$ErrorActionPreference = "Stop"
$hardwareText = "$Hardware".Trim().ToLowerInvariant()
if ($hardwareText -notin @("true", "false", "1", "0")) {
    throw "Hardware must be true, false, 1, or 0."
}
$hardwareEnabled = $hardwareText -in @("true", "1")
$bufferBytes = [uint64]4194304
$expectedLogicalBytes = [uint64]$CandidateActionCount * $bufferBytes
$expectedTrackedCopyCount = $CandidateActionCount + 8
$expectedRuntimeEventCount = $CandidateActionCount + 17
$runtimeRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($GatewayPath)) {
    $GatewayPath = Join-Path (Split-Path $runtimeRoot -Parent) "FluidGateway"
}
$gatewayRoot = (Resolve-Path -LiteralPath $GatewayPath).Path
$artifactDirectory = Join-Path $runtimeRoot "artifacts"
New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null

function Get-FreeTcpPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try {
        return ([Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

if ([string]::IsNullOrWhiteSpace($TargetPath)) {
    $TargetPath = Join-Path $runtimeRoot `
        "native\build\Release\fluidruntime-d3d12-transfer-target.exe"
}
if ([string]::IsNullOrWhiteSpace($HookPath)) {
    $HookPath = Join-Path $runtimeRoot `
        "native\build\Release\fluidruntime-d3d12-transfer-hook.dll"
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $artifactDirectory `
        "gateway-d3d12-transfer-control.json"
}
if ([string]::IsNullOrWhiteSpace($InvalidResponseOutputPath)) {
    $InvalidResponseOutputPath = Join-Path $artifactDirectory `
        "gateway-d3d12-transfer-invalid-response-fail-closed.json"
}
if ([string]::IsNullOrWhiteSpace($FailClosedOutputPath)) {
    $FailClosedOutputPath = Join-Path $artifactDirectory `
        "gateway-d3d12-transfer-fail-closed.json"
}
if ([string]::IsNullOrWhiteSpace($SlowResponseOutputPath)) {
    $SlowResponseOutputPath = Join-Path $artifactDirectory `
        "gateway-d3d12-transfer-slow-response-fail-closed.json"
}
$TargetPath = (Resolve-Path -LiteralPath $TargetPath).Path
$HookPath = (Resolve-Path -LiteralPath $HookPath).Path
$python = (Get-Command python -ErrorAction Stop).Source
$pythonSha256 = (Get-FileHash -LiteralPath $python -Algorithm SHA256).Hash.ToLowerInvariant()

function Invoke-FaultCase {
    param(
        [Parameter(Mandatory = $true)] [string]$Mode,
        [Parameter(Mandatory = $true)] [string]$CaseOutputPath,
        [Parameter(Mandatory = $true)] [string]$ExpectedFailureType
    )

    $faultPort = Get-FreeTcpPort
    $faultTimeoutMs = if ($Mode -eq "slow") { 1200 } else { 500 }
    $faultDelayMs = if ($Mode -eq "slow") { 200 } else { 100 }
    $readyPath = Join-Path $artifactDirectory "fluidlink-d3d12-$Mode.ready"
    $faultOutput = Join-Path $artifactDirectory "fluidlink-d3d12-$Mode.stdout.log"
    $faultError = Join-Path $artifactDirectory "fluidlink-d3d12-$Mode.stderr.log"
    Remove-Item -LiteralPath $readyPath -Force -ErrorAction SilentlyContinue
    $faultServer = Start-Process `
        -FilePath $python `
        -ArgumentList @(
            "-u", (Join-Path $PSScriptRoot "fluidlink_fault_server.py"),
            "--port", "$faultPort",
            "--mode", $Mode,
            "--delay-ms", "$faultDelayMs",
            "--ready", $readyPath,
            "--gateway-path", $gatewayRoot
        ) `
        -WorkingDirectory $runtimeRoot `
        -RedirectStandardOutput $faultOutput `
        -RedirectStandardError $faultError `
        -WindowStyle Hidden `
        -PassThru
    try {
        for ($attempt = 0; $attempt -lt 50; $attempt += 1) {
            if ($faultServer.HasExited) {
                $stderr = Get-Content -LiteralPath $faultError -Raw `
                    -ErrorAction SilentlyContinue
                throw "FluidLink $Mode peer exited before startup: $stderr"
            }
            if (Test-Path -LiteralPath $readyPath) {
                break
            }
            Start-Sleep -Milliseconds 100
        }
        if (-not (Test-Path -LiteralPath $readyPath)) {
            throw "FluidLink $Mode peer did not become ready."
        }

        & dotnet run `
            --project (Join-Path $runtimeRoot "src\FluidRuntime") `
            -c Release `
            --no-build `
            -- `
            gateway-d3d12-copy-lab `
            --target $TargetPath `
            --hook $HookPath `
            --host 127.0.0.1 `
            --port $faultPort `
            --timeout-ms $faultTimeoutMs `
            --gateway-pid $faultServer.Id `
            --gateway-executable-sha256 $pythonSha256 `
            --trial-pairs 1 `
            --warmup-pairs 0 `
            --hold-ms 50 `
            --gpu-timeout-ms 10000 `
            --candidate-action-count $CandidateActionCount `
            --hardware $hardwareEnabled `
            --out $CaseOutputPath | Out-Host
        if ($LASTEXITCODE -ne 3) {
            throw "FluidLink $Mode control did not return fail-closed exit code 3."
        }
    }
    finally {
        if (-not $faultServer.HasExited) {
            Stop-Process -Id $faultServer.Id -Force
            Wait-Process -Id $faultServer.Id -ErrorAction SilentlyContinue
        }
    }

    $fallback = Get-Content -LiteralPath $CaseOutputPath -Raw | ConvertFrom-Json
    $native = $fallback.baseline_fallback
    if ($fallback.mode -ne
            "fluidruntime-gateway-d3d12-transfer-fail-closed-v0.21.0" -or
        -not $fallback.fail_closed -or
        $fallback.native_policy_published -or
        $fallback.failure_type -ne $ExpectedFailureType -or
        $fallback.authorization_deadline_milliseconds -ne $faultTimeoutMs -or
        $fallback.authorization_elapsed_microseconds -le 0 -or
        $fallback.complete_fallback_elapsed_microseconds -lt
            $fallback.authorization_elapsed_microseconds -or
        $fallback.complete_fallback_elapsed_microseconds -lt
            $native.managed_end_to_end_elapsed_microseconds -or
        $fallback.completed_authorization_round_trips -lt 0 -or
        $fallback.completed_authorization_round_trips -ge 10 -or
        $fallback.target_sha256 -notmatch "^[0-9a-f]{64}$" -or
        $fallback.hook_sha256 -notmatch "^[0-9a-f]{64}$" -or
        -not $fallback.all_tracked_copies_forwarded -or
        -not $fallback.no_copies_skipped -or
        -not $fallback.content_equivalent -or
        -not $fallback.fence_completed -or
        -not $fallback.rollback_restored -or
        $native.optimized -or
        $native.tracked_copy_count -ne $expectedTrackedCopyCount -or
        $native.forwarded_copy_count -ne $expectedTrackedCopyCount -or
        $native.skipped_copy_count -ne 0 -or
        $native.automatic_invalidation_count -ne 2 -or
        $native.explicit_invalidation_count -ne 2 -or
        -not $native.lane_isolation_verified -or
        -not $native.queue_submission_verified -or
        $native.published_policy_expires_at_qpc -ne 0 -or
        $native.published_policy_action_mask -ne 0 -or
        $native.published_policy_action_budget -ne 0 -or
        -not $native.immutable_sources_verified -or
        -not $native.content_equivalent -or
        -not $native.fence_completed -or
        -not $native.debug_validation_passed -or
        -not $native.rollback_restored) {
        throw "FluidLink $Mode fallback violated the D3D12 baseline contract."
    }
    return $fallback
}

$port = Get-FreeTcpPort
$serverOutput = Join-Path $artifactDirectory "gateway-d3d12-server.stdout.log"
$serverError = Join-Path $artifactDirectory "gateway-d3d12-server.stderr.log"
$server = Start-Process `
    -FilePath $python `
    -ArgumentList @(
        "-u",
        "-m", "fluidgateway",
        "runtime", "serve-events",
        "--host", "127.0.0.1",
        "--port", "$port"
    ) `
    -WorkingDirectory $gatewayRoot `
    -RedirectStandardOutput $serverOutput `
    -RedirectStandardError $serverError `
    -WindowStyle Hidden `
    -PassThru

try {
    $ready = $false
    for ($attempt = 0; $attempt -lt 50; $attempt += 1) {
        if ($server.HasExited) {
            $stderr = Get-Content -LiteralPath $serverError -Raw `
                -ErrorAction SilentlyContinue
            throw "FluidGateway server exited before startup: $stderr"
        }
        if ((Test-Path -LiteralPath $serverOutput) -and
            (Select-String -LiteralPath $serverOutput -Pattern "listening" -Quiet)) {
            $ready = $true
            break
        }
        Start-Sleep -Milliseconds 100
    }
    if (-not $ready) {
        throw "FluidGateway server did not report readiness within 5 seconds."
    }

    & dotnet run `
        --project (Join-Path $runtimeRoot "src\FluidRuntime") `
        -c Release `
        --no-build `
        -- `
        gateway-d3d12-copy-lab `
        --target $TargetPath `
        --hook $HookPath `
        --host 127.0.0.1 `
        --port $port `
        --timeout-ms 5000 `
        --gateway-pid $server.Id `
        --gateway-executable-sha256 $pythonSha256 `
        --trial-pairs $TrialPairs `
        --warmup-pairs $WarmupPairs `
        --hold-ms 50 `
        --gpu-timeout-ms 10000 `
        --candidate-action-count $CandidateActionCount `
        --hardware $hardwareEnabled `
        --out $OutputPath
    if ($LASTEXITCODE -ne 0) {
        throw "Gateway-managed D3D12 lab exited with code $LASTEXITCODE."
    }
}
finally {
    if (-not $server.HasExited) {
        Stop-Process -Id $server.Id -Force
        Wait-Process -Id $server.Id -ErrorAction SilentlyContinue
    }
}

$report = Get-Content -LiteralPath $OutputPath -Raw | ConvertFrom-Json
$expectedAuthorizationRuns = $TrialPairs + $WarmupPairs
if ($report.mode -ne
        "fluidruntime-gateway-d3d12-transfer-control-trace-v0.21.0" -or
    -not $report.target_owned -or
    -not $report.cooperative_load -or
    $report.remote_injection -or
    -not $report.fail_closed -or
    $report.physical_transfer_bytes_measured -or
    $report.policy_origin -ne "fluidgateway-live-fluidlink-v2-decisions" -or
    $report.protocol -ne "fluidlink-v2" -or
    $report.advertised_server_name -ne "fluidgateway" -or
    -not $report.peer_process_binding_verified -or
    $report.peer_cryptographically_authenticated -or
    $report.peer_process_id -ne $server.Id -or
    $report.peer_executable_sha256 -ne $pythonSha256 -or
    $report.target_sha256 -notmatch "^[0-9a-f]{64}$" -or
    $report.hook_sha256 -notmatch "^[0-9a-f]{64}$" -or
    $report.trial_pairs_requested -ne $TrialPairs -or
    $report.warmup_pairs -ne $WarmupPairs -or
    $report.included_trial_pairs -ne $TrialPairs -or
    $report.buffer_bytes -ne $bufferBytes -or
    $report.source_snapshot_mode -ne
        "registration-copy-cpu-shadow-upload-unmapped-until-fence" -or
    $report.source_snapshot_bytes -ne (4 * $bufferBytes) -or
    -not $report.upload_unmapped_after_registration -or
    $report.candidate_action_count -ne $CandidateActionCount -or
    $report.avoided_logical_bytes_per_optimized_run -ne $expectedLogicalBytes -or
    -not $report.content_equivalent -or
    -not $report.immutable_source_guard_passed -or
    -not $report.automatic_invalidation_guard_passed -or
    -not $report.explicit_invalidation_guard_passed -or
    -not $report.fence_completed_in_all_runs -or
    -not $report.rollback_restored_in_all_runs -or
    -not $report.lane_isolation_verified_in_all_runs -or
    -not $report.queue_submission_verified_in_all_runs -or
    $report.native_execution_gate_passed -ne
        ($hardwareEnabled -and $TrialPairs -ge 10) -or
    $report.required_forwarded_copies_per_optimized_run -ne 8 -or
    $report.transfer_descriptor.backend -ne 2 -or
    $report.transfer_backend_id -ne 2 -or
    $report.transfer_descriptor.operation -ne 2 -or
    $report.transfer_topology.queue_count -ne 1 -or
    $report.transfer_topology.execution_scope_count -ne 2 -or
    $report.transfer_topology.source_resource_count -ne 2 -or
    $report.transfer_topology.destination_resource_count -ne 2 -or
    $report.transfer_topology.lane_count -ne 2 -or
    $report.transfer_topology.fence_count -ne 1 -or
    $report.transfer_topology.runtime_event_count -ne $expectedRuntimeEventCount -or
    $report.authorization_run_count -ne $expectedAuthorizationRuns -or
    $report.authorization_latency_microseconds.count -ne
        $expectedAuthorizationRuns -or
    $report.managed_end_to_end_microseconds.baseline.count -ne $TrialPairs -or
    $report.managed_end_to_end_microseconds.optimized.count -ne $TrialPairs -or
    $report.managed_end_to_end_microseconds.delta.count -ne $TrialPairs -or
    $report.claim_scope -ne
        "owned-d3d12-multi-lane-copy-buffer-fluidgateway-authorized-exact-content-elision") {
    throw "Gateway-managed D3D12 report violated the v0.21 transfer contract."
}

if ($hardwareEnabled -and $TrialPairs -ge 10 -and
    -not $report.native_execution_gate_passed) {
    throw "Hardware D3D12 native execution evidence did not pass its fixed gate."
}
if (-not $hardwareEnabled -and
    ($report.native_execution_gate_passed -or
     $report.performance_claim_allowed -or
     $report.native_execution_gate_blockers -notcontains
        "software-adapter-not-hardware" -or
     $report.performance_claim_blockers -notcontains
        "software-adapter-not-hardware")) {
    throw "WARP report did not preserve the hardware-only performance boundary."
}
if ($report.authorizations | Where-Object {
    -not $_.authorized -or
    $_.backend -ne 1 -or
    -not $_.heartbeat_verified -or
    -not $_.seed_upload_executed -or
    -not $_.all_candidate_decisions_accepted -or
    -not $_.all_candidate_executions_deferred_to_native -or
    -not $_.peer_process_binding_verified -or
    $_.peer_cryptographically_authenticated -or
    $_.peer_process_id -ne $server.Id -or
    $_.peer_executable_sha256 -ne $pythonSha256 -or
    $_.authorization_context_sha256 -notmatch "^[0-9a-f]{64}$" -or
    $_.target_sha256 -ne $report.target_sha256 -or
    $_.hook_sha256 -ne $report.hook_sha256 -or
    $_.authorization_deadline_milliseconds -ne 5000 -or
    $_.candidate_decision_opcode -ne 2 -or
    $_.candidate_policy -ne "deduplicate-identical-transfer" -or
    $_.candidate_decision_count -ne $CandidateActionCount -or
    $_.authorized_logical_bytes -ne $expectedLogicalBytes -or
    $_.native_action_mask -ne 16 -or
    $_.native_action_budget -ne $CandidateActionCount -or
    $_.runtime_event_count -ne $expectedRuntimeEventCount -or
    $_.transfer_descriptor.backend -ne 2 -or
    $_.transfer_descriptor.operation -ne 2 -or
    $_.transfer_topology.queue_count -ne 1 -or
    $_.transfer_topology.execution_scope_count -ne 2 -or
    $_.transfer_topology.source_resource_count -ne 2 -or
    $_.transfer_topology.destination_resource_count -ne 2 -or
    $_.transfer_topology.lane_count -ne 2 -or
    $_.transfer_topology.fence_count -ne 1 -or
    $_.transfer_topology.runtime_event_count -ne $expectedRuntimeEventCount -or
    $_.round_trip_count -ne 10 -or
    $_.authorization_scope -ne
        "owned-d3d12-process-bound-multi-lane-copy-buffer-final-gate"
}) {
    throw "A D3D12 authorization drifted from the exact FluidGateway contract."
}

if ($report.trials | Where-Object {
    $_.baseline.optimized -or
    $_.baseline.gateway_authorization -ne $null -or
    $_.baseline.tracked_copy_count -ne $expectedTrackedCopyCount -or
    $_.baseline.transfer_backend_id -ne 2 -or
    $_.baseline.source_snapshot_bytes -ne (4 * $bufferBytes) -or
    -not $_.baseline.upload_unmapped_after_registration -or
    $_.baseline.forwarded_copy_count -ne $expectedTrackedCopyCount -or
    $_.baseline.skipped_copy_count -ne 0 -or
    $_.baseline.automatic_invalidation_count -ne 2 -or
    $_.baseline.explicit_invalidation_count -ne 2 -or
    -not $_.baseline.lane_isolation_verified -or
    -not $_.baseline.queue_submission_verified -or
    $_.baseline.queue_execute_count -ne 1 -or
    $_.baseline.queue_signal_count -ne 1 -or
    $_.baseline.submitted_scope_count -ne 2 -or
    $_.baseline.published_policy_action_mask -ne 0 -or
    $_.baseline.published_policy_action_budget -ne 0 -or
    -not $_.optimized.optimized -or
    $_.optimized.gateway_authorization -eq $null -or
    $_.optimized.tracked_copy_count -ne $expectedTrackedCopyCount -or
    $_.optimized.transfer_backend_id -ne 2 -or
    $_.optimized.source_snapshot_bytes -ne (4 * $bufferBytes) -or
    -not $_.optimized.upload_unmapped_after_registration -or
    $_.optimized.forwarded_copy_count -ne 8 -or
    $_.optimized.skipped_copy_count -ne $CandidateActionCount -or
    $_.optimized.automatic_invalidation_count -ne 2 -or
    $_.optimized.explicit_invalidation_count -ne 2 -or
    -not $_.optimized.lane_isolation_verified -or
    -not $_.optimized.queue_submission_verified -or
    $_.optimized.skipped_copy_bytes -ne $expectedLogicalBytes -or
    $_.optimized.exact_comparison_count -ne ($CandidateActionCount + 2) -or
    $_.optimized.published_policy_action_mask -ne 16 -or
    $_.optimized.published_policy_action_budget -ne $CandidateActionCount -or
    -not $_.content_equivalent -or
    -not $_.fence_completed_in_both_runs -or
    -not $_.rollback_restored_in_both_runs -or
    -not $_.adapter_identity_matched
}) {
    throw "A paired D3D12 run violated the authorized native contract."
}

$invalidFallback = Invoke-FaultCase `
    -Mode "invalid" `
    -CaseOutputPath $InvalidResponseOutputPath `
    -ExpectedFailureType "FluidLinkV2ProtocolException"
$stallFallback = Invoke-FaultCase `
    -Mode "stall" `
    -CaseOutputPath $FailClosedOutputPath `
    -ExpectedFailureType "TimeoutException"
$slowFallback = Invoke-FaultCase `
    -Mode "slow" `
    -CaseOutputPath $SlowResponseOutputPath `
    -ExpectedFailureType "TimeoutException"
if ($slowFallback.completed_authorization_round_trips -le 0) {
    throw "Slow FluidLink control did not prove a mid-sequence total deadline."
}

[pscustomobject]@{
    protocol = $report.protocol
    contract_sha256 = $report.contract_sha256
    authorization_runs = $report.authorization_run_count
    native_skips_per_run = $CandidateActionCount
    native_avoided_logical_bytes_per_run = $expectedLogicalBytes
    managed_end_to_end_delta_p99_us =
        $report.managed_end_to_end_microseconds.delta.p99
    submit_to_fence_delta_p99_us =
        $report.submit_to_fence_microseconds.delta.p99
    gpu_workload_delta_p99_us = $report.gpu_workload_microseconds.delta.p99
    performance_claim_allowed = $report.performance_claim_allowed
    fail_closed_forwarded_calls =
        $stallFallback.baseline_fallback.forwarded_copy_count
    fail_closed_skipped_calls =
        $stallFallback.baseline_fallback.skipped_copy_count
    invalid_response_forwarded_calls =
        $invalidFallback.baseline_fallback.forwarded_copy_count
    slow_response_completed_round_trips =
        $slowFallback.completed_authorization_round_trips
    report = $OutputPath
    invalid_response_report = $InvalidResponseOutputPath
    fail_closed_report = $FailClosedOutputPath
    slow_response_report = $SlowResponseOutputPath
}

exit 0
