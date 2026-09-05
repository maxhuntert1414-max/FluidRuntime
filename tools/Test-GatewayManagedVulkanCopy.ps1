[CmdletBinding()]
param(
    [string]$GatewayPath = "",
    [string]$BuildPath = "native/build",
    [ValidateSet("Release", "Debug")][string]$Configuration = "Release",
    [ValidateRange(1, 30)][int]$TrialPairs = 2,
    [ValidateRange(0, 5)][int]$WarmupPairs = 0,
    [ValidateRange(1, 128)][int]$CandidateActionCount = 128,
    [string]$OutputPrefix = "gateway-vulkan",
    [switch]$Software,
    [switch]$Validation,
    [switch]$SkipFaultCases
)

$ErrorActionPreference = "Stop"
$runtimeRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if (-not $GatewayPath) { $GatewayPath = Join-Path (Split-Path $runtimeRoot -Parent) "FluidGateway" }
$gatewayRoot = (Resolve-Path -LiteralPath $GatewayPath).Path
if (-not [IO.Path]::IsPathRooted($BuildPath)) { $BuildPath = Join-Path $runtimeRoot $BuildPath }
$target = (Resolve-Path -LiteralPath (Join-Path $BuildPath "$Configuration/fluidruntime-vulkan-transfer-target.exe")).Path
$library = (Resolve-Path -LiteralPath (Join-Path $BuildPath "$Configuration/fluidruntime-vulkan-transfer.dll")).Path
$runtime = Join-Path $runtimeRoot "src/FluidRuntime/bin/Release/net10.0/fluidruntime.dll"
$artifacts = Join-Path $runtimeRoot "artifacts"
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
if ($OutputPrefix -notmatch '^[a-zA-Z0-9_-]+$') { throw "OutputPrefix must be a filename prefix, not a path." }
$python = (Get-Command python -ErrorAction Stop).Source
$pythonSha = (Get-FileHash -LiteralPath $python -Algorithm SHA256).Hash.ToLowerInvariant()

function Invoke-Case([string]$Mode) {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
    $listener.Stop()
    $nonce = [Guid]::NewGuid().ToString("N")
    $ready = Join-Path $artifacts "$OutputPrefix-$Mode-$nonce.ready"
    $stdout = Join-Path $artifacts "$OutputPrefix-$Mode.stdout.log"
    $stderr = Join-Path $artifacts "$OutputPrefix-$Mode.stderr.log"
    $output = Join-Path $artifacts "$OutputPrefix-$Mode.json"
    if ($Mode -eq "success") {
        $arguments = @("-u", "-m", "fluidgateway", "runtime", "serve-events", "--host", "127.0.0.1", "--port", "$port")
        $working = $gatewayRoot
    } else {
        $faultScript = Join-Path $PSScriptRoot "fluidlink_fault_server.py"
        $arguments = @("-u", "`"$faultScript`"", "--port", "$port", "--mode", $Mode,
            "--ready", "`"$ready`"", "--gateway-path", "`"$gatewayRoot`"", "--delay-ms", "200")
        $working = $runtimeRoot
    }
    $server = Start-Process -FilePath $python -ArgumentList $arguments -WorkingDirectory $working `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr -WindowStyle Hidden -PassThru
    try {
        $started = $false
        for ($attempt = 0; $attempt -lt 100; ++$attempt) {
            $server.Refresh()
            if ($server.HasExited) { throw "Vulkan test peer exited: $(Get-Content -LiteralPath $stderr -Raw)" }
            $started = if ($Mode -eq "success") {
                (Test-Path -LiteralPath $stdout) -and (Select-String -LiteralPath $stdout -Pattern "listening" -Quiet)
            } else { Test-Path -LiteralPath $ready }
            if ($started) { break }
            Start-Sleep -Milliseconds 100
        }
        if (-not $started) { throw "Vulkan test peer failed to become ready." }
        $pairs = if ($Mode -eq "success") { $TrialPairs } else { 1 }
        $warmups = if ($Mode -eq "success") { $WarmupPairs } else { 0 }
        $timeout = if ($Mode -eq "success") { 5000 } elseif ($Mode -eq "slow") { 1200 } else { 500 }
        & dotnet $runtime gateway-vulkan-copy-lab --target $target --library $library --out $output `
            --port $port --timeout-ms $timeout --gateway-pid $server.Id --gateway-executable-sha256 $pythonSha `
            --trial-pairs $pairs --warmup-pairs $warmups --candidate-action-count $CandidateActionCount `
            --hardware (-not $Software.IsPresent).ToString().ToLowerInvariant() `
            --validation $Validation.IsPresent.ToString().ToLowerInvariant() | Out-Host
        $expectedExit = if ($Mode -eq "success") { 0 } else { 3 }
        if ($LASTEXITCODE -ne $expectedExit) { throw "Vulkan $Mode returned $LASTEXITCODE; expected $expectedExit." }
        $report = Get-Content -LiteralPath $output -Raw | ConvertFrom-Json
        if ($report.performance_claim_allowed) { throw "Vulkan lab must not claim general performance." }
        if ($Mode -eq "success") {
            if (-not $report.native_execution_gate_passed -or $report.fail_closed -or
                $report.pairs.Count -ne ($pairs + $warmups)) { throw "Vulkan success gate failed." }
        } else {
            $fallback = $report.fail_closed.baseline_fallback
            if (-not $report.fail_closed -or $report.native_execution_gate_passed -or
                $report.fail_closed.native_policy_published -or $fallback.optimized -or
                $fallback.native_evidence.skipped_copies -ne 0 -or
                -not $fallback.native_evidence.content_equivalent -or -not $fallback.native_evidence.rollback_verified) {
                throw "Vulkan $Mode failed closed incorrectly."
            }
        }
        Write-Host "Verified Vulkan $Mode evidence: $output"
    } finally {
        $server.Refresh()
        if (-not $server.HasExited) {
            Stop-Process -Id $server.Id -Force
            Wait-Process -Id $server.Id -ErrorAction SilentlyContinue
        }
        $server.Dispose()
        Remove-Item -LiteralPath $ready -ErrorAction SilentlyContinue
    }
}

Invoke-Case "success"
if (-not $SkipFaultCases) {
    Invoke-Case "invalid"
    Invoke-Case "stall"
    Invoke-Case "slow"
}
