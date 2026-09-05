[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$VkcubePath,
    [string]$BuildPath = "native/build",
    [ValidateSet("Release", "Debug")][string]$Configuration = "Release",
    [ValidateRange(1, 20)][int]$Pairs = 4,
    [ValidateRange(60, 300)][int]$Frames = 120,
    [string]$OutputPrefix = "application-session",
    [switch]$SkipCollectorCrash
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$cube = (Resolve-Path -LiteralPath $VkcubePath).Path
if ([IO.Path]::GetFileName($cube) -notin @("vkcube.exe", "vkcubepp.exe")) {
    throw "This bounded destructive fault test accepts only explicitly supplied Khronos cube test applications."
}
if (-not [IO.Path]::IsPathRooted($BuildPath)) { $BuildPath = Join-Path $root $BuildPath }
$layer = (Resolve-Path (Join-Path $BuildPath $Configuration)).Path
$runtime = Join-Path $root "src/FluidRuntime/bin/Release/net10.0/fluidruntime.dll"
$artifacts = Join-Path $root "artifacts"
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
if ($OutputPrefix -notmatch '^[a-zA-Z0-9_-]+$') { throw "Invalid evidence filename prefix." }
$pairsEvidence = @()
function Invoke-Sample([int]$Pair, [bool]$Observe, [int]$Priority = 0) {
    $suffix = if ($Priority) { "priority" } elseif ($Observe) { "observed" } else { "baseline" }
    $out = Join-Path $artifacts "$OutputPrefix-$Pair-$suffix.json"
    & dotnet $runtime app-session --exe $cube --layer-dir $layer --out $out `
        --acknowledge-no-anticheat true --seconds 20 --priority-seconds $Priority `
        --observe-vulkan $Observe.ToString().ToLowerInvariant() -- --c $Frames --use_staging --suppress_popups | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Application $suffix run failed: $out" }
    $r = Get-Content -LiteralPath $out -Raw | ConvertFrom-Json
    if ($r.failure -or -not $r.process_exited -or $r.exit_code -ne 0 -or $r.native_actuation_enabled -or
        $r.performance_claim_allowed -or $r.layer_verified -ne $Observe) { throw "Application session contract mismatch." }
    if ($Observe) {
        $v = $r.samples[-1].vulkan
        if ($v.presents -ne $Frames -or $v.devices -ne 1 -or $v.active_devices -ne 0 -or
            $v.active_instances -ne 0 -or $v.live_bytes -ne 0 -or $v.api_errors -ne 0 -or
            $v.untracked_allocations -ne 0 -or $v.allocations -ne $v.frees) {
            throw "Vulkan observation lifecycle/count mismatch."
        }
    }
    if ($Priority -and ($r.windows_priority.restoration -ne "restored" -or
        $r.windows_priority.after -ne "Normal" -or -not $r.windows_priority.applied)) {
        throw "Windows priority restoration was not verified."
    }
    return $r
}
for ($i = 0; $i -lt $Pairs; ++$i) {
    if ($i % 2 -eq 0) { $a = Invoke-Sample $i $false; $b = Invoke-Sample $i $true }
    else { $b = Invoke-Sample $i $true; $a = Invoke-Sample $i $false }
    if ($a.executable_sha256 -ne $b.executable_sha256 -or $a.arguments_sha256 -ne $b.arguments_sha256 -or
        $a.layer_sha256 -ne $b.layer_sha256) { throw "A/B inputs changed." }
    $pairsEvidence += [pscustomobject]@{pair=$i; order=$(if ($i % 2) {"BA"} else {"AB"});
        baseline_ms=$a.elapsed_milliseconds; observed_ms=$b.elapsed_milliseconds;
        observed_minus_baseline_ms=($b.elapsed_milliseconds-$a.elapsed_milliseconds)}
}
$priority = Invoke-Sample 0 $true 1
$crash = $null
if (-not $SkipCollectorCrash) {
    $nonce = [Guid]::NewGuid().ToString("N")
    $out = Join-Path $artifacts "$OutputPrefix-crash-$nonce.json"
    $collector = $null
    $target = $null
    try {
        $arguments = @("`"$runtime`"", "app-session", "--exe", "`"$cube`"", "--layer-dir", "`"$layer`"",
            "--out", "`"$out`"", "--acknowledge-no-anticheat", "true", "--seconds", "20",
            "--priority-seconds", "2", "--", "--c", "600", "--use_staging", "--suppress_popups")
        $collector = Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList $arguments `
            -WindowStyle Hidden -PassThru -RedirectStandardError "$out.stderr.log" -RedirectStandardOutput "$out.stdout.log"
        for ($attempt = 0; $attempt -lt 100; ++$attempt) {
            $child = Get-CimInstance Win32_Process -Filter "ParentProcessId=$($collector.Id)" |
                Where-Object { $_.ExecutablePath -eq $cube } | Select-Object -First 1
            if ($child) {
                $target = Get-Process -Id $child.ProcessId
                if ($target.PriorityClass -eq 'AboveNormal') { break }
            }
            Start-Sleep -Milliseconds 50
        }
        if (-not $target -or $target.PriorityClass -ne 'AboveNormal') { throw "Priority was not applied before crash test." }
        Stop-Process -Id $collector.Id -Force
        $collector.WaitForExit()
        for ($attempt = 0; $attempt -lt 100; ++$attempt) {
            $target.Refresh()
            if ($target.PriorityClass -eq 'Normal') { break }
            Start-Sleep -Milliseconds 50
        }
        if ($target.PriorityClass -ne 'Normal') { throw "Watchdog did not restore priority after collector termination." }
        if (-not $target.WaitForExit(20000)) { throw "Owned cube failed to exit after crash test." }
        $leaseFile = Get-ChildItem -LiteralPath $artifacts -Filter "$([IO.Path]::GetFileName($out)).priority-*.json" |
            Select-Object -First 1
        if (-not $leaseFile) { throw "Watchdog evidence missing." }
        $lease = Get-Content -LiteralPath $leaseFile.FullName -Raw | ConvertFrom-Json
        if ($lease.restoration -ne 'restored' -or $lease.after -ne 'Normal') { throw "Watchdog evidence is not restored." }
        $crash = [pscustomobject]@{collector_terminated=$true; priority_restored=$true;
            target_pid=$target.Id; lease=$lease; target_exited=$true}
    } finally {
        if ($collector -and -not $collector.HasExited) { Stop-Process -Id $collector.Id -Force }
        if ($target -and -not $target.HasExited) {
            # Only the explicitly launched cube is disposable, never user applications.
            if (-not $target.WaitForExit(25000)) { Stop-Process -Id $target.Id -Force }
        }
        if ($target) { $target.Dispose() }
        if ($collector) { $collector.Dispose() }
    }
}
$summary = [pscustomobject]@{schema="fluidruntime-application-integration-v1";
    target_sha256=$priority.executable_sha256; layer_sha256=$priority.layer_sha256;
    frames_per_run=$Frames; pairs=$pairsEvidence; priority=$priority.windows_priority;
    collector_crash=$crash; performance_claim_allowed=$false;
    limits=@("Descriptive wall-clock overhead on a frame-limited Khronos sample, not game FPS.",
        "A/B sampling and presentation quantization affect short-run differences.",
        "Priority scheduling benefit is not established by successful restoration.")}
$summary | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $artifacts "$OutputPrefix-summary.json") -Encoding UTF8
Write-Host "Application A/B, priority and collector-lifetime evidence verified."
