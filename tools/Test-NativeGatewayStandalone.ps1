[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$GatewayExecutable,
    [string]$BuildPath = "native/build",
    [string]$OutputPath = "artifacts/native-gateway-standalone.json"
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$exe = (Resolve-Path -LiteralPath $GatewayExecutable).Path
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
if (-not [IO.Path]::IsPathRooted($BuildPath)) {
    $BuildPath = Join-Path $root $BuildPath
}
if (-not [IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath = Join-Path $root $OutputPath
}
$runtime = Join-Path $root "src/FluidRuntime/bin/Release/net10.0/fluidruntime.dll"
$target = Join-Path $BuildPath "Release/fluidruntime-hook-target.exe"
$hook = Join-Path $BuildPath "Release/fluidruntime-present-hook.dll"
foreach ($file in @($exe, $runtime, $target, $hook)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Required built file missing: $file"
    }
}
New-Item -ItemType Directory -Path (Split-Path $OutputPath -Parent) -Force | Out-Null
$sha = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
$listener.Stop()
$oldPath = $env:PATH
$oldPythonPath = $env:PYTHONPATH
$oldPythonHome = $env:PYTHONHOME
$server = $null
try {
    # Child processes inherit this test-only environment; no machine settings change.
    $env:PATH = "$env:SystemRoot\System32;$(Split-Path $dotnet -Parent)"
    $env:PYTHONPATH = $null
    $env:PYTHONHOME = $null
    if (Get-Command python,python3,py -ErrorAction SilentlyContinue) {
        throw "Python must not be available on the restricted test PATH."
    }
    $server = Start-Process -FilePath $exe -ArgumentList @("serve-events", "--port", "$port") `
        -WorkingDirectory (Split-Path $exe -Parent) -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput "$OutputPath.stdout.log" `
        -RedirectStandardError "$OutputPath.stderr.log"
    $ready = $false
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        $server.Refresh()
        if ($server.HasExited) {
            throw "Packaged Gateway exited with code $($server.ExitCode)."
        }
        if ((Test-Path -LiteralPath "$OutputPath.stdout.log") -and
            (Select-String -LiteralPath "$OutputPath.stdout.log" -Pattern "listening" -Quiet)) {
            $ready = $true
            break
        }
        Start-Sleep -Milliseconds 100
    }
    if (-not $ready) {
        throw "Packaged Gateway did not become ready."
    }
    & $dotnet $runtime gateway-update-upload-lab --target $target --hook $hook `
        --host 127.0.0.1 --port $port --gateway-pid $server.Id --gateway-executable-sha256 $sha `
        --timeout-ms 5000 --trial-pairs 2 --warmup-pairs 0 --candidate-action-count 128 `
        --authorization-max-concurrency 8 --authorization-samples-per-level 32 `
        --hold-ms 50 --gpu-timeout-ms 5000 --hardware false --out $OutputPath | Out-Host
    if ($LASTEXITCODE) {
        throw "Standalone native authorization/actuation failed: $LASTEXITCODE"
    }
    $report = Get-Content -LiteralPath $OutputPath -Raw | ConvertFrom-Json
    if (-not $report.target_owned -or -not $report.cooperative_load -or $report.remote_injection -or
        -not $report.fail_closed -or -not $report.peer_process_binding_verified -or
        $report.peer_process_id -ne $server.Id -or $report.peer_executable_sha256 -ne $sha -or
        $report.gateway_candidate_decision_count -ne 256 -or $report.native_action_mask -ne 8 -or
        -not $report.native_exact_content_final_gate -or -not $report.mutation_guard_passed -or
        -not $report.generation_guard_passed -or -not $report.content_equivalent -or
        -not $report.rollback_restored_in_all_runs -or $report.performance_claim_allowed -or
        $report.native_evidence.redundant_update_count_per_optimized_run -ne 128) {
        throw "Standalone report failed identity, native execution, content, or rollback checks."
    }
    $server.Refresh()
    $modules = @($server.Modules | ForEach-Object { $_.ModuleName })
    if ($modules | Where-Object { $_ -match '(?i)^python|^clang_rt\.asan|^vcruntime|^msvcp\d' }) {
        throw "Packaged Gateway loaded a Python, ASAN, or developer runtime dependency."
    }
    $environmentPath = [IO.Path]::ChangeExtension($OutputPath, "environment.json")
    [pscustomobject]@{
        schema = "fluidruntime-native-gateway-standalone-v1"
        full_path_passed = $true
        executable_sha256 = $sha
        python_available_on_path = $false
        restricted_path = $env:PATH
        gateway_modules = $modules
        actual_peer_binding_verified = $true
        content_and_rollback_verified = $true
        omitted_calls_per_owned_run = 128
        game_performance_claim = $false
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $environmentPath -Encoding UTF8
    Write-Host "Native Gateway + Runtime + owned D3D11 passed without Python on PATH: $OutputPath"
}
finally {
    $env:PATH = $oldPath
    $env:PYTHONPATH = $oldPythonPath
    $env:PYTHONHOME = $oldPythonHome
    if ($server) {
        if (-not $server.HasExited) {
            $server.Kill()
            $server.WaitForExit()
        }
        $server.Dispose()
    }
}
