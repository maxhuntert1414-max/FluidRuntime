[CmdletBinding()]
param(
    [string]$GatewayPath = "",
    [string]$TargetPath = "",
    [string]$HookPath = "",
    [string]$OutputPath = "",
    [string]$InvalidResponseOutputPath = "",
    [string]$FailClosedOutputPath = "",
    [string]$SlowResponseOutputPath = "",
    [int]$TrialPairs = 2,
    [int]$WarmupPairs = 0,
    [object]$Hardware = $false
)

$ErrorActionPreference = "Stop"
$hardwareText = "$Hardware".Trim().ToLowerInvariant()
if ($hardwareText -notin @("true", "false", "1", "0")) {
    throw "Hardware must be true, false, 1, or 0."
}
$hardwareEnabled = $hardwareText -in @("true", "1")
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
        "native\build\Release\fluidruntime-hook-target.exe"
}
if ([string]::IsNullOrWhiteSpace($HookPath)) {
    $HookPath = Join-Path $runtimeRoot `
        "native\build\Release\fluidruntime-present-hook.dll"
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $artifactDirectory `
        "gateway-update-upload-control.json"
}
if ([string]::IsNullOrWhiteSpace($FailClosedOutputPath)) {
    $FailClosedOutputPath = Join-Path $artifactDirectory `
        "gateway-update-upload-fail-closed.json"
}
if ([string]::IsNullOrWhiteSpace($InvalidResponseOutputPath)) {
    $InvalidResponseOutputPath = Join-Path $artifactDirectory `
        "gateway-update-upload-invalid-response-fail-closed.json"
}
if ([string]::IsNullOrWhiteSpace($SlowResponseOutputPath)) {
    $SlowResponseOutputPath = Join-Path $artifactDirectory `
        "gateway-update-upload-slow-response-fail-closed.json"
}
$TargetPath = (Resolve-Path -LiteralPath $TargetPath).Path
$HookPath = (Resolve-Path -LiteralPath $HookPath).Path

$port = Get-FreeTcpPort

$serverOutput = Join-Path $artifactDirectory "gateway-manager-server.stdout.log"
$serverError = Join-Path $artifactDirectory "gateway-manager-server.stderr.log"
$python = (Get-Command python -ErrorAction Stop).Source
$pythonSha256 = (Get-FileHash -LiteralPath $python -Algorithm SHA256).Hash.ToLowerInvariant()

function Invoke-FaultCase {
    param(
        [Parameter(Mandatory = $true)] [string]$Mode,
        [Parameter(Mandatory = $true)] [string]$CaseOutputPath,
        [Parameter(Mandatory = $true)] [string]$ExpectedFailureType
    )

    $faultPort = Get-FreeTcpPort
    $readyPath = Join-Path $artifactDirectory "fluidlink-$Mode.ready"
    $faultOutput = Join-Path $artifactDirectory "fluidlink-$Mode.stdout.log"
    $faultError = Join-Path $artifactDirectory "fluidlink-$Mode.stderr.log"
    Remove-Item -LiteralPath $readyPath -Force -ErrorAction SilentlyContinue
    $faultServer = Start-Process `
        -FilePath $python `
        -ArgumentList @(
            "-u", (Join-Path $PSScriptRoot "fluidlink_fault_server.py"),
            "--port", "$faultPort",
            "--mode", $Mode,
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
            gateway-update-upload-lab `
            --target $TargetPath `
            --hook $HookPath `
            --host 127.0.0.1 `
            --port $faultPort `
            --timeout-ms 500 `
            --gateway-pid $faultServer.Id `
            --gateway-executable-sha256 $pythonSha256 `
            --trial-pairs 1 `
            --warmup-pairs 0 `
            --hold-ms 50 `
            --gpu-timeout-ms 5000 `
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
    if ($fallback.mode -ne
            "fluidruntime-gateway-update-upload-fail-closed-v0.17.0" -or
        $fallback.failure_stage -ne "gateway-authorization-before-target-launch" -or
        $fallback.authorization_failure_type -ne $ExpectedFailureType -or
        $fallback.authorization_deadline_milliseconds -ne 500 -or
        $fallback.authorization_elapsed_microseconds -le 0 -or
        $fallback.completed_round_trip_count -lt 0 -or
        $fallback.completed_round_trip_count -ge 10 -or
        $fallback.authorization_accepted -or
        $fallback.native_policy_published -or
        -not $fallback.baseline_fallback_completed -or
        $fallback.target_sha256 -notmatch "^[0-9a-f]{64}$" -or
        $fallback.hook_sha256 -notmatch "^[0-9a-f]{64}$" -or
        $fallback.forwarded_update_subresource_count -ne 70 -or
        $fallback.skipped_update_subresource_count -ne 0 -or
        -not $fallback.content_equivalent -or
        -not $fallback.rollback_restored -or
        $fallback.baseline_fallback.published_policy_epoch -ne 0 -or
        $fallback.baseline_fallback.published_policy_expires_at_qpc -ne 0 -or
        $fallback.baseline_fallback.published_policy_action_mask -ne 0 -or
        $fallback.baseline_fallback.published_policy_action_budget -ne 0 -or
        $fallback.baseline_fallback.applied_policy_actions -ne 0 -or
        $fallback.baseline_fallback.policy_status -ne "none") {
        throw "FluidLink $Mode fallback violated the baseline contract."
    }
    return $fallback
}

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
        gateway-update-upload-lab `
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
        --gpu-timeout-ms 5000 `
        --hardware $hardwareEnabled `
        --out $OutputPath
    if ($LASTEXITCODE -ne 0) {
        throw "Gateway-managed lab exited with code $LASTEXITCODE."
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
if ($report.mode -ne "fluidruntime-gateway-update-upload-control-trace-v0.17.0" -or
    -not $report.target_owned -or
    -not $report.cooperative_load -or
    $report.remote_injection -or
    -not $report.fail_closed -or
    $report.policy_origin -ne "fluidgateway-live-fluidlink-v2-decisions" -or
    $report.protocol -ne "fluidlink-v2" -or
    $report.advertised_server_name -ne "fluidgateway" -or
    -not $report.peer_process_binding_verified -or
    $report.peer_cryptographically_authenticated -or
    $report.peer_process_id -ne $server.Id -or
    $report.peer_executable_sha256 -ne $pythonSha256 -or
    $report.authorization_deadline_milliseconds -ne 5000 -or
    $report.target_sha256 -notmatch "^[0-9a-f]{64}$" -or
    $report.hook_sha256 -notmatch "^[0-9a-f]{64}$" -or
    $report.authorization_run_count -ne $expectedAuthorizationRuns -or
    $report.measured_authorization_run_count -ne $TrialPairs -or
    $report.gateway_candidate_decision_count -ne
        ($expectedAuthorizationRuns * 64) -or
    $report.authorized_logical_bytes_per_optimized_run -ne 268435456 -or
    $report.native_action_mask -ne 8 -or
    $report.native_action_budget_per_optimized_run -ne 64 -or
    $report.performance_claim_allowed -or
    $report.performance_claim_blockers -notcontains
        "gateway-authorization-outside-native-timing-window" -or
    -not $report.native_exact_content_final_gate -or
    -not $report.content_equivalent -or
    -not $report.rollback_restored_in_all_runs) {
    throw "Gateway-managed report violated the v0.15 closed-loop contract."
}
if ($report.authorizations | Where-Object {
    -not $_.authorized -or
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
    $_.candidate_decision_count -ne 64 -or
    $_.native_action_mask -ne 8 -or
    $_.native_action_budget -ne 64 -or
    $_.runtime_event_count -ne 71 -or
    $_.round_trip_count -ne 10
}) {
    throw "A FluidGateway authorization drifted from the exact decision contract."
}
$native = $report.native_evidence
if ($native.mode -ne "fluidruntime-update-upload-elision-trace-v0.12.0" -or
    $native.included_trial_pairs -ne $TrialPairs -or
    $native.avoided_update_bytes_per_optimized_run -ne 268435456 -or
    -not $native.mutation_guard_passed -or
    -not $native.generation_guard_passed -or
    -not $native.content_equivalent -or
    -not $native.rollback_restored_in_all_runs) {
    throw "Native evidence drifted under Gateway authorization."
}
if ($native.trials | Where-Object {
    $_.baseline.gateway_authorization -ne $null -or
    $_.baseline.skipped_update_subresource_count -ne 0 -or
    $_.optimized.gateway_authorization -eq $null -or
    $_.optimized.published_policy_expires_at_qpc -le 0 -or
    $_.optimized.published_policy_action_mask -ne 8 -or
    $_.optimized.published_policy_action_budget -ne 64 -or
    $_.optimized.skipped_update_subresource_count -ne 64 -or
    $_.optimized.applied_policy_actions -ne 64 -or
    $_.optimized.policy_status -ne "exhausted" -or
    -not $_.content_equivalent -or
    -not $_.rollback_restored_in_both_runs
}) {
    throw "A paired native run violated the Gateway-managed evidence contract."
}

$invalidFallback = Invoke-FaultCase `
    -Mode "invalid" `
    -CaseOutputPath $InvalidResponseOutputPath `
    -ExpectedFailureType "FluidLinkV2ProtocolException"
$fallback = Invoke-FaultCase `
    -Mode "stall" `
    -CaseOutputPath $FailClosedOutputPath `
    -ExpectedFailureType "TimeoutException"
$slowFallback = Invoke-FaultCase `
    -Mode "slow" `
    -CaseOutputPath $SlowResponseOutputPath `
    -ExpectedFailureType "TimeoutException"
if ($slowFallback.completed_round_trip_count -le 0) {
    throw "Slow FluidLink control did not prove a mid-sequence total deadline."
}

[pscustomobject]@{
    protocol = $report.protocol
    contract_sha256 = $report.contract_sha256
    authorization_runs = $report.authorization_run_count
    candidate_decisions = $report.gateway_candidate_decision_count
    authorized_bytes_per_run = $report.authorized_logical_bytes_per_optimized_run
    native_skips_per_run =
        $native.redundant_update_count_per_optimized_run
    native_avoided_bytes_per_run = $native.avoided_update_bytes_per_optimized_run
    fail_closed_forwarded_calls = $fallback.forwarded_update_subresource_count
    fail_closed_skipped_calls = $fallback.skipped_update_subresource_count
    invalid_response_forwarded_calls =
        $invalidFallback.forwarded_update_subresource_count
    slow_response_completed_round_trips =
        $slowFallback.completed_round_trip_count
    report = $OutputPath
    invalid_response_report = $InvalidResponseOutputPath
    fail_closed_report = $FailClosedOutputPath
    slow_response_report = $SlowResponseOutputPath
}

exit 0
