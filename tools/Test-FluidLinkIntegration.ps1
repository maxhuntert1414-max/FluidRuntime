[CmdletBinding()]
param(
    [string]$GatewayPath = ""
)

$ErrorActionPreference = "Stop"
$runtimeRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($GatewayPath)) {
    $GatewayPath = Join-Path (Split-Path $runtimeRoot -Parent) "FluidGateway"
}
$gatewayRoot = (Resolve-Path -LiteralPath $GatewayPath).Path

$sharedArtifacts = @(
    "fluidlink-v1.contract.json",
    "fluidlink-v2.contract.json",
    "fluidlink-v2.golden.json",
    "fluidlink-v2-batch.contract.json",
    "fluidlink-v2-batch.golden.json"
)
$artifactHashes = @{}
foreach ($artifact in $sharedArtifacts) {
    $gatewayArtifact = Join-Path $gatewayRoot "contracts\$artifact"
    $runtimeArtifact = Join-Path $runtimeRoot "contracts\$artifact"
    $gatewayHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $gatewayArtifact).Hash
    $runtimeHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $runtimeArtifact).Hash
    if ($gatewayHash -ne $runtimeHash) {
        throw "FluidLink shared artifact drift detected for $artifact."
    }
    $artifactHashes[$artifact] = $runtimeHash.ToLowerInvariant()
}

$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
$listener.Stop()

$artifactDirectory = Join-Path $runtimeRoot "artifacts"
New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null
$reportPath = Join-Path $artifactDirectory "fluidlink-cross-process.json"
$serverOutput = Join-Path $artifactDirectory "fluidlink-server.stdout.log"
$serverError = Join-Path $artifactDirectory "fluidlink-server.stderr.log"
$python = (Get-Command python -ErrorAction Stop).Source
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
            $stderr = Get-Content -LiteralPath $serverError -Raw -ErrorAction SilentlyContinue
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
        --no-restore `
        -- `
        link-probe `
        --host 127.0.0.1 `
        --port $port `
        --timeout-ms 5000 `
        --out $reportPath
    if ($LASTEXITCODE -ne 0) {
        throw "FluidRuntime link-probe exited with code $LASTEXITCODE."
    }

    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if (-not $report.intercommunication_verified) {
        throw "FluidLink intercommunication gate did not pass."
    }
    if ($report.duplicate_policy -ne "deduplicate-identical-transfer") {
        throw "FluidLink returned an unexpected duplicate policy."
    }
    if ($report.duplicate_upload_executed) {
        throw "FluidLink failed to reject the synthetic duplicate upload."
    }
    if ($report.protocol -ne "fluidlink-v2" -or
        $report.transport -ne "tcp-loopback") {
        throw "FluidLink did not negotiate the expected v2 loopback transport."
    }
    if (-not $report.binary_framing -or -not $report.numeric_opcodes -or
        -not $report.fixed_point_units -or $report.json_payloads -or
        $report.payload_encoding -ne "opcode-specific-positional-binary") {
        throw "FluidLink v2 did not preserve its binary positional contract."
    }
    if (-not $report.contract_verified -or
        $report.contract_sha256 -ne $artifactHashes["fluidlink-v2.contract.json"] -or
        $report.max_payload_bytes -ne 65535) {
        throw "FluidLink did not negotiate the exact bounded contract."
    }
    if ($report.duplicate_decision_opcode -ne 2) {
        throw "FluidLink returned an unexpected duplicate decision opcode."
    }
    if ($report.bytes_sent -le 0 -or $report.bytes_received -le 0 -or
        $report.total_frame_bytes -le 0) {
        throw "FluidLink did not record valid frame byte counters."
    }
    if ($report.runtime_event_count -ne 8 -or $report.round_trip_count -ne 11 -or
        $report.v1_baseline_round_trip_count -ne 11) {
        throw "FluidLink probe did not execute the complete same-flow comparison."
    }
    if ($report.estimated_saved_microseconds -ne 800 -or
        $report.estimated_saved_bytes -ne 67108864) {
        throw "FluidLink v2 fixed-point decision evidence drifted."
    }
    if ($report.v1_baseline_total_frame_bytes -ne 3189 -or
        $report.total_frame_bytes -ne 1880 -or
        $report.bytes_saved_vs_v1 -ne 1309 -or
        $report.byte_reduction_vs_v1_percent -ne 41.05) {
        throw "FluidLink v2 same-flow byte budget drifted."
    }
    if ($report.round_trip_p50_microseconds -le 0 -or
        $report.round_trip_p95_microseconds -lt $report.round_trip_p50_microseconds -or
        $report.round_trip_max_microseconds -lt $report.round_trip_p95_microseconds) {
        throw "FluidLink v2 returned invalid application RTT percentiles."
    }
    if ($report.delta_encoding_enabled -or
        $report.shared_memory_transport_enabled) {
        throw "FluidLink v2 reported unimplemented transport capabilities."
    }

    [pscustomobject]@{
        protocol = $report.protocol
        server = "$($report.server_name) $($report.server_version)"
        round_trips = $report.round_trip_count
        duplicate_policy = $report.duplicate_policy
        duplicate_opcode = $report.duplicate_decision_opcode
        estimated_saved_microseconds = $report.estimated_saved_microseconds
        estimated_saved_bytes = $report.estimated_saved_bytes
        v1_frame_bytes = $report.v1_baseline_total_frame_bytes
        v2_frame_bytes = $report.total_frame_bytes
        bytes_saved = $report.bytes_saved_vs_v1
        byte_reduction = "$($report.byte_reduction_vs_v1_percent)%"
        rtt_p50_microseconds = $report.round_trip_p50_microseconds
        rtt_p95_microseconds = $report.round_trip_p95_microseconds
        contract_sha256 = $artifactHashes["fluidlink-v2.contract.json"]
        golden_sha256 = $artifactHashes["fluidlink-v2.golden.json"]
        batch_contract_sha256 =
            $artifactHashes["fluidlink-v2-batch.contract.json"]
        batch_golden_sha256 =
            $artifactHashes["fluidlink-v2-batch.golden.json"]
        report = $reportPath
    }
}
finally {
    if (-not $server.HasExited) {
        Stop-Process -Id $server.Id -Force
        Wait-Process -Id $server.Id -ErrorAction SilentlyContinue
    }
}
