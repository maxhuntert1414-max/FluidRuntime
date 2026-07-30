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

$gatewayContract = Join-Path $gatewayRoot "contracts\fluidlink-v1.contract.json"
$runtimeContract = Join-Path $runtimeRoot "contracts\fluidlink-v1.contract.json"
$gatewayHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $gatewayContract).Hash
$runtimeHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $runtimeContract).Hash
if ($gatewayHash -ne $runtimeHash) {
    throw "FluidLink contract drift detected between FluidGateway and FluidRuntime."
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
        "--port", "$port",
        "--once"
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

    if (-not $server.HasExited) {
        Wait-Process -InputObject $server -Timeout 10
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
    if (-not $report.binary_framing -or -not $report.numeric_opcodes -or
        -not $report.compact_decisions) {
        throw "FluidLink did not negotiate binary framing and compact decisions."
    }
    if (-not $report.contract_verified -or $report.max_json_depth -ne 64 -or
        $report.max_payload_bytes -ne 1048576) {
        throw "FluidLink did not negotiate the exact bounded contract."
    }
    if ($report.duplicate_decision_opcode -ne 2) {
        throw "FluidLink returned an unexpected duplicate decision opcode."
    }
    if ($report.bytes_sent -le 0 -or $report.bytes_received -le 0 -or
        $report.total_frame_bytes -le 0) {
        throw "FluidLink did not record valid frame byte counters."
    }
    if ($report.binary_bytes_saved -le 0 -or
        $report.binary_byte_reduction_percent -le 0) {
        throw "FluidLink binary framing did not beat its equivalent JSON envelope."
    }

    [pscustomobject]@{
        protocol = $report.protocol
        server = "$($report.server_name) $($report.server_version)"
        round_trips = $report.round_trip_count
        duplicate_policy = $report.duplicate_policy
        duplicate_opcode = $report.duplicate_decision_opcode
        estimated_saved_mb = $report.estimated_saved_mb
        frame_bytes = $report.total_frame_bytes
        equivalent_json_bytes = $report.equivalent_json_envelope_bytes
        binary_reduction = "$($report.binary_byte_reduction_percent)%"
        contract_sha256 = $runtimeHash
        report = $reportPath
    }
}
finally {
    if (-not $server.HasExited) {
        Stop-Process -Id $server.Id -Force
        Wait-Process -Id $server.Id -ErrorAction SilentlyContinue
    }
}
