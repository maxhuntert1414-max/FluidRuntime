[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$GatewayLibrary,
    [string]$GatewayExecutable = '',
    [string]$NativeDirectory = 'native/build/Release',
    [string]$OutputDirectory = 'artifacts/gateway-backends',
    [ValidateSet('server', 'inprocess', 'both')][string]$GatewayMode = 'both',
    [switch]$SkipVulkan
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$library = (Resolve-Path -LiteralPath $GatewayLibrary).Path
$librarySha = (Get-FileHash -LiteralPath $library -Algorithm SHA256).Hash.ToLowerInvariant()
$native = (Resolve-Path -LiteralPath $NativeDirectory).Path
$runtime = Join-Path $root 'src/FluidRuntime/bin/Release/net10.0/fluidruntime.exe'
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$modes = if ($GatewayMode -eq 'both') { @('server', 'inprocess') } else { @($GatewayMode) }
foreach ($mode in $modes) {
    $process = $null
    try {
        $backendArgs = @('--gateway-backend', $mode)
        if ($mode -eq 'inprocess') {
            $backendArgs += @('--gateway-library', $library, '--gateway-library-sha256', $librarySha)
        } else {
            $exe = (Resolve-Path -LiteralPath $GatewayExecutable).Path
            $sha = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
            $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
            $listener.Start()
            $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
            $listener.Stop()
            $start = [Diagnostics.ProcessStartInfo]::new()
            $start.FileName = $exe
            $start.Arguments = "serve-events --host 127.0.0.1 --port $port"
            $start.UseShellExecute = $false
            $start.CreateNoWindow = $true
            $process = [Diagnostics.Process]::Start($start)
            $readyBy = [DateTime]::UtcNow.AddSeconds(10)
            while ($true) {
                if ($process.HasExited) { throw 'Owned Gateway server exited.' }
                $probe = [Net.Sockets.TcpClient]::new()
                try { $probe.Connect('127.0.0.1', $port); break }
                catch { if ([DateTime]::UtcNow -gt $readyBy) { throw }; Start-Sleep -Milliseconds 25 }
                finally { $probe.Dispose() }
            }
            $backendArgs += @('--port', "$port", '--gateway-pid', "$($process.Id)", '--gateway-executable-sha256', $sha)
        }
        $common = @('--trial-pairs', '2', '--warmup-pairs', '0', '--candidate-action-count', '128') + $backendArgs
        & $runtime gateway-update-upload-lab --target "$native/fluidruntime-hook-target.exe" `
            --hook "$native/fluidruntime-present-hook.dll" --out "$output/$mode-d3d11.json" `
            --authorization-max-concurrency 8 --authorization-samples-per-level 8 --hardware false @common
        if ($LASTEXITCODE) { throw "D3D11 $mode failed: $LASTEXITCODE" }
        & $runtime gateway-d3d12-copy-lab --target "$native/fluidruntime-d3d12-transfer-target.exe" `
            --hook "$native/fluidruntime-d3d12-transfer-hook.dll" --out "$output/$mode-d3d12.json" --hardware false @common
        if ($LASTEXITCODE) { throw "D3D12 $mode failed: $LASTEXITCODE" }
        & $runtime gateway-readback-lab --target "$native/fluidruntime-hook-target.exe" `
            --hook "$native/fluidruntime-present-hook.dll" --out "$output/$mode-readback.json" `
            --trial-pairs 2 --warmup-pairs 0 --hardware false @backendArgs
        if ($LASTEXITCODE) { throw "Readback $mode failed: $LASTEXITCODE" }
        $readback = Get-Content -LiteralPath "$output/$mode-readback.json" -Raw | ConvertFrom-Json
        if ($readback.gateway_backend -ne $mode -or $readback.authorizations.Count -ne 2 -or
            -not $readback.native_evidence.content_equivalent -or
            -not $readback.native_evidence.rollback_restored_in_all_runs -or
            $readback.native_evidence.avoided_readback_bytes_per_optimized_run -ne 268435456 -or
            $readback.performance_claim_allowed -or $readback.physical_transfer_bytes_measured) {
            throw 'Gateway readback violated its evidence contract.'
        }
        $expectedTrips = if ($mode -eq 'inprocess') { 0 } else { 20 }
        if ($readback.transport_round_trip_count -ne $expectedTrips -or $readback.wire_exchange_count -ne 20) {
            throw 'Readback transport counters confused wire exchanges with TCP trips.'
        }
        if ($mode -eq 'server') {
            & $runtime gateway-readback-lab --target "$native/fluidruntime-hook-target.exe" `
                --hook "$native/fluidruntime-present-hook.dll" --out "$output/server-readback-denied.json" `
                --trial-pairs 1 --warmup-pairs 0 --hardware false --port "$port" `
                --gateway-pid "$($process.Id)" --gateway-executable-sha256 ('0' * 64)
            if ($LASTEXITCODE -ne 3) { throw 'Readback accepted an untrusted Gateway identity.' }
            $denied = Get-Content -LiteralPath "$output/server-readback-denied.json" -Raw | ConvertFrom-Json
            if (-not $denied.fail_closed -or $denied.rejected_run_policy_published -or
                $denied.baseline_fallback.skipped_readback_copy_count -ne 0 -or
                $denied.baseline_fallback.published_policy_epoch -ne 0 -or
                -not $denied.baseline_fallback.content_equivalent -or
                -not $denied.baseline_fallback.rollback_restored) {
                throw 'Denied readback did not preserve original execution.'
            }
        }
        if (-not $SkipVulkan) {
            & $runtime gateway-vulkan-copy-lab --target "$native/fluidruntime-vulkan-transfer-target.exe" `
                --library "$native/fluidruntime-vulkan-transfer.dll" --out "$output/$mode-vulkan.json" --hardware true @common
            if ($LASTEXITCODE) { throw "Vulkan $mode failed: $LASTEXITCODE" }
        }
    } finally {
        if ($process) {
            if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
            $process.Dispose()
        }
    }
}
Write-Output "Gateway backend reports: $output"
