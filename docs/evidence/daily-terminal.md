# Daily Terminal Validation

Date: 2026-09-13. This milestone adds read-only daily monitoring, not new
intervention authority. Gateway v0.69.0 binaries, FluidLink 0.5.0 contracts and
the existing application-session/priority watchdog gates are unchanged.

## Local Results

| Surface | Result |
| --- | --- |
| Managed Release / Debug | 312 tests each, zero skips, trusted Gateway DLL/native integrations enabled |
| Native Release / Debug / ASAN | 39 CTest tests each, zero failures |
| New daily managed tests | 29, including real native streaming and process-table rendering |
| Static-MSVC Release probe | `/analyze`, warnings as errors, passed |
| Gateway Python regression | 343 passed; Gateway change is documentation only |
| Portable renewal validation | 115-second owned-shell run, 112 samples, final JSON 35,819 bytes |
| Probe imports | `pdh.dll` and `KERNEL32.dll`; no ASAN or separately installed MSVC runtime |

The portable renewal run used only Windows directories on PATH and a
nonexistent DOTNET_ROOT. Both helpers ran without Python or an installed .NET
resolver. The target shell remained alive and retained its original priority.
Its GPU counters were unavailable (NA); the native process/stream remained
healthy across renewal. This is not a GPU workload benchmark.

One million history insertions retained exactly the newest 600 samples and
the correct total. Tests also cover missing/invalid executables, cancellation
before and during collection, target exit, exclusive report ownership,
truncated/oversized/non-UTF-8 records, deadlines, identity/ordering mismatches,
invalid numeric counters, early stream disposal and legacy series compatibility.

## Reproduce

```powershell
$env:FLUIDGATEWAY_DLL = 'C:\verified\FluidGatewayNative.dll'
$env:FLUIDRUNTIME_NATIVE = (Resolve-Path native/build-vulkan/Release).Path
dotnet test FluidRuntime.slnx -c Release -warnaserror
dotnet test FluidRuntime.slnx -c Debug -warnaserror
ctest --test-dir native/build-vulkan -C Release --output-on-failure
ctest --test-dir native/build-vulkan -C Debug --output-on-failure
# ASAN uses the existing documented local-only ASAN runtime environment.
ctest --test-dir native/build-vulkan-asan -C Release --output-on-failure
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Publish-Daily.ps1 `
  -OutputDirectory artifacts/daily-validation
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Test-DailyPackage.ps1 `
  -PackageDirectory artifacts/daily-validation
```

Local logs are in `artifacts/daily-*.log`; they are not committed evidence
archives. CI repeats managed/native regressions, real streaming and a shorter
five-second portable smoke. Package manifests distinguish clean from dirty
source and enumerate file hashes. CI status and final commit IDs are recorded
in the workspace checkpoint, not inferred from these local counts.

## Not Established

- A full-day soak, worst-case collector CPU/RAM bounds or leak-freedom.
- Real-game GPU coverage, higher FPS, lower input delay, or optimization savings.
- Existing-PID injection, automatic scheduling or safe third-party copy elision.
- Crash-proof report finalization: forced exit can leave a stale `running` snapshot.
- Restoration of any intervention by this monitor: it never applies one.

See [daily use](../daily-use.md) for report semantics, sampling gaps and controls.
