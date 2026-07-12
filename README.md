# FluidRuntime

FluidRuntime is the actuation-oriented companion to FluidGateway. FluidGateway
measures probable waste in PresentMon traces; FluidRuntime consumes that
evidence together with live Windows process and memory telemetry, then builds a
runtime control plan.

The long-term objective is to reduce redundant CPU/GPU/RAM/VRAM movement,
late synchronization, buffer churn, and frame-pipeline stalls from normal PC
software. It is not a DLSS, FSR, or frame-generation clone.

## Current boundary

Version 0.1 is advisory-only:

- reads a FluidGateway operational ledger;
- samples process CPU, working set, private memory, thread count, and host RAM;
- produces scheduler and memory-residency action candidates;
- explicitly blocks actions that need a native backend or privilege;
- never changes process priority, affinity, RAM/VRAM residency, GPU queues,
  drivers, games, or OS state.

## Run

```powershell
dotnet run --project src/FluidRuntime -- inspect `
  --ledger samples/gpu-wait-ledger.json `
  --samples 3 `
  --interval-ms 250 `
  --out artifacts/runtime-report.json
```

Omit `--pid` to inspect the FluidRuntime process itself. Supply a PID only for
a process you are authorized to observe.

## Verify

```powershell
dotnet test FluidRuntime.slnx
dotnet build FluidRuntime.slnx -c Release
```

The synthetic trace baseline in `data/` comes from FluidGateway test fixtures.
It is development evidence, not a benchmark captured from a real game.
