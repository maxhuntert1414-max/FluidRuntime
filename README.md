# FluidRuntime

FluidRuntime is the actuation-oriented companion to FluidGateway. FluidGateway
measures probable waste in PresentMon traces; FluidRuntime consumes that
evidence together with live Windows process and memory telemetry, then builds a
runtime control plan.

The long-term objective is to reduce redundant CPU/GPU/RAM/VRAM movement,
late synchronization, buffer churn, and frame-pipeline stalls from normal PC
software. It is not a DLSS, FSR, or frame-generation clone.

Documentation: [architecture](docs/architecture.md) | [roadmap](docs/roadmap.md) |
[FluidGateway](https://github.com/maxhuntert1414-max/FluidGateway)

## Current boundary

Version 0.4 remains advisory-only:

- reads a FluidGateway operational ledger;
- samples process CPU, working set, private memory, thread count, and host RAM;
- produces scheduler and memory-residency action candidates;
- explicitly blocks actions that need a native backend or privilege;
- streams owned D3D11 hook events into the managed runtime through shared memory;
- never changes process priority, affinity, RAM/VRAM residency, GPU queues,
  drivers, games, or OS state.

The v0.2 native probe is also read-only. It adds per-process Windows process
memory and GPU performance-counter telemetry through a small C++ executable.
It does not inject a DLL, install a driver, or write into the target process.

## Run

```powershell
dotnet run --project src/FluidRuntime -- inspect `
  --ledger samples/gpu-wait-ledger.json `
  --samples 3 `
  --interval-ms 250 `
  --native-probe native/build/Release/fluidruntime-native-probe.exe `
  --allow-ledger-target-mismatch true `
  --out artifacts/runtime-report.json
```

Omit `--pid` to inspect the FluidRuntime process itself. Supply a PID only for
a process you are authorized to observe. The bundled sample ledger describes a
synthetic game, so its example explicitly allows a target mismatch and holds
all trace-derived recommendations. Real monitoring rejects mismatched ledger
and process identities by default.

## Verify

```powershell
dotnet test FluidRuntime.slnx
dotnet build FluidRuntime.slnx -c Release
```

The synthetic trace baseline in `data/` comes from FluidGateway test fixtures.
It is development evidence, not a benchmark captured from a real game.

## Native probe

Configure and build with the CMake bundled in Visual Studio Build Tools:

```powershell
cmake -S native -B native/build -A x64
cmake --build native/build --config Release
ctest --test-dir native/build -C Release --output-on-failure
native/build/Release/fluidruntime-native-probe.exe --self-test
```

The probe emits a versioned JSON document to stdout. GPU counters may be
unavailable for processes with no active WDDM GPU allocation; that is reported
as missing telemetry instead of being converted into a fake zero.

`--native-probe` executes the path supplied by the operator. Use only a probe
binary built from this repository or another binary you explicitly trust.

## Present hook lab

The native build also contains a controlled D3D11 hook lab. An owned target
loads the hook DLL cooperatively. The DLL observes `IDXGISwapChain::Present`,
buffer and texture creation, write-oriented `Map/Unmap`, `UpdateSubresource`,
and `CopyResource`. Detach restores every current original vtable entry before
the DLL can be unloaded.

```powershell
native/build/Release/fluidruntime-hook-target.exe `
  --hook native/build/Release/fluidruntime-present-hook.dll `
  --frames 120 `
  --hardware `
  --out artifacts/present-hook-lab.json
```

The deterministic workload performs six resource copies. Three repeat an
unchanged source/destination generation and are reported as candidates, never
skipped. The expected result is 49,152 bytes copied and 24,576 bytes potentially
avoidable.

Run the managed IPC lab to consume those events while the target is alive:

```powershell
dotnet run --project src/FluidRuntime -c Release -- hook-lab `
  --target native/build/Release/fluidruntime-hook-target.exe `
  --hook native/build/Release/fluidruntime-present-hook.dll `
  --frames 300 `
  --hold-ms 1000 `
  --hardware true `
  --out artifacts/hook-ipc-lab.json
```

The hook publishes fixed-size events to a versioned, per-process shared-memory
ring. Events carry opaque resource IDs instead of pointer addresses. The managed
reader checks the ABI, sequence continuity, native overrun count, event totals,
exact deterministic event order, resource IDs and source/destination pairs,
generations, flags, copy counts, and byte estimates against the target snapshot
before accepting a report.

This is not remote injection and is not intended for protected or anti-cheat
processes. It exists to verify resource observation, runtime thunk transitions,
concurrent-call draining, and rollback in software we own before any
external-process work is considered. Candidate detection currently assumes the
controlled workload; GPU shader/UAV writes and resource release tracking are
not covered yet.
