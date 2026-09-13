# Daily Terminal Use

Windows x64, read-only, no Python, ledger, administrator session or Gateway
server needed. This is process monitoring, **not automatic optimization**.
It installs no service, driver, autostart, hook, keyboard handler or global setting.

From the portable package directory:

```powershell
.\fluidruntime.exe processes
.\fluidruntime.exe processes --name eldenring
.\fluidruntime.exe monitor --pid 1234
```

Use the actual PID from the list. Stop with **Ctrl+C**. The monitor also stops
when the original process exits; it never follows a reused PID or closes your
application. Administrator/protected processes may be inaccessible. Do not
elevate just to bypass that boundary.

```powershell
.\fluidruntime.exe monitor --pid 1234 --seconds 60 --out session.json
.\fluidruntime.exe monitor --pid 1234 --cpu-only
```

## What You See

- CPU percentage normalized across the logical processors available to Runtime.
- Resident working set and private committed bytes, displayed in MiB.
- GPU busiest engine percentage, dedicated GPU memory and shared GPU memory
  from Windows PDH. These are **not** transfer bandwidth, free VRAM, global GPU
  utilization, or measured savings. Memory categories must not be added blindly.
- `NA` means unavailable, not idle or zero. Counter support varies by driver and
  workload; no GPU instances for a CPU-only process is a normal outcome.

Sampling defaults to one second; `--interval-ms` accepts 1000..10000. The native
helper retains its counters and streams individual snapshots, renewing after
at most 100 samples (100 seconds of scheduled intervals, plus PDH/IO overhead).
Renewal introduces a small collection gap; the
elapsed timestamps include it. CPU includes measured elapsed time, not nominal
sampling time. No constant-rate or zero-overhead claim is made.

The default report is `%LOCALAPPDATA%\FluidRuntime\monitor-latest.json`. It is
atomically replaced every five seconds and at orderly shutdown, retaining the
latest **600 samples** and a total count. Earlier samples are intentionally
discarded; this is not an archival trace. The companion `.lock` file prevents
two monitors from overwriting the same report; it stays on disk but releases
automatically. Choose different `--out` files for simultaneous sessions.
An abrupt power loss/forced termination may leave a last report marked `running`;
check `updated_at`, not that field alone. No intervention requires rollback in
this mode. The telemetry child exits when its bounded session finishes or its
output pipe closes; orderly stop terminates only that owned helper.

If the probe is absent or fails, CPU/RAM monitoring continues with an explicit
warning and no automatic retry loop. To select a separate Release probe, use
`--native-probe C:\path\fluidruntime-native-probe.exe`. Do not use an ASAN build
for daily use. Missing/invalid records never authorize an action.

The monitor report has its own `fluidruntime-monitor-v0.1` schema. It is not an
`app-session` report or a PresentMon trace and is not yet an `analyze-app` input.
PresentMon remains the separate source for actual frame timing and FPS.

## Intervention Boundary

The existing `app-session` command remains available in the full development
build for explicit Vulkan observation and optional 1..30-second Normal to
AboveNormal leases with an independent watchdog. Its verification, same-user,
anti-cheat acknowledgement and restoration gates are unchanged. The daily
monitor neither injects into existing applications nor renews priority leases.
Third-party copy removal/general Windows scheduling are not production features.

## Build a Portable Package

Build requirements: .NET 10 SDK, CMake, Visual Studio x64 C++ tools. Runtime
requirements: Windows x64 only; the package bundles .NET and uses the static
MSVC runtime for the native probe. No Python or separate .NET installation.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Publish-Daily.ps1
```

Pass `-CMake 'C:\path\cmake.exe'` when needed. Output defaults to a new
`artifacts/daily-win-x64-TIMESTAMP` directory. Existing outputs are never removed.
`package-manifest.json` records SHA256 per file, source commit and whether the
source was dirty. A manifest is an integrity inventory, not a code signature.
The package omits experimental hook DLLs and the Gateway decision backend.

## Protocol and Verification

`--stream --start-time <FILETIME>` is a private probe option: a four-byte
little-endian length followed by the existing UTF-8 probe JSON, up to 256 KiB
per record. It is not FluidLink. The consumer checks PID, retained creation
identity, interval, ordering, numeric values, exact count, pipe closure and
deadlines. Legacy single/series probe output and FluidLink contracts are retained.
Individual PDH wildcard buffers are capped at 4 MiB.

Tests cover one million history insertions, framing faults, identity mismatch,
cancellation, output contention and real native streaming. That history test is
not a million-event performance benchmark or a full-day soak.

Portable verification (including native renewal at the default 115 seconds):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Test-DailyPackage.ps1 `
  -PackageDirectory artifacts/daily-win-x64-TIMESTAMP
```

This restricts the child's PATH to Windows, points DOTNET_ROOT at a nonexistent
directory, verifies package hashes and monitors only the test shell. It does
not modify persistent environment variables or any game.

References: [Windows process identity](https://learn.microsoft.com/en-us/windows/win32/procthread/process-handles-and-identifiers),
[process creation time](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocesstimes),
[PDH sampling](https://learn.microsoft.com/en-us/windows/win32/perfctrs/collecting-performance-data).
