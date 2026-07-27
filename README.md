# FluidRuntime

FluidRuntime is the actuation-oriented companion to FluidGateway. FluidGateway
measures probable waste in PresentMon traces; FluidRuntime consumes that
evidence together with live Windows process and memory telemetry, then builds a
runtime control plan.

The long-term objective is to reduce redundant CPU/GPU/RAM/VRAM movement,
late synchronization, buffer churn, and frame-pipeline stalls from normal PC
software. It is not a DLSS, FSR, or frame-generation clone.

Documentation: [status](docs/STATUS.md) | [briefing](docs/BRIEFING-CLAUDE-CODE.md) | [architecture](docs/architecture.md) | [roadmap](docs/roadmap.md) |
[v0.6 evidence](docs/evidence/v0.6-copy-elision.md) |
[v0.7 lifecycle evidence](docs/evidence/v0.7-resource-lifecycle.md) |
[v0.7.1 destruction evidence](docs/evidence/v0.7.1-automatic-destruction.md) |
[v0.7.2 subresource evidence](docs/evidence/v0.7.2-subresource-provenance.md) |
[v0.7.3 GPU-view write evidence](docs/evidence/v0.7.3-gpu-view-writes.md) |
[v0.8 managed control evidence](docs/evidence/v0.8.0-managed-control-plane.md) |
[v0.9 sustained copy-elision evidence](docs/evidence/v0.9.0-sustained-copy-elision.md) |
[v0.10 readback-elision evidence](docs/evidence/v0.10.0-readback-elision.md) |
[FluidGateway](https://github.com/maxhuntert1414-max/FluidGateway)

## Current boundary

Version 0.10.0 keeps normal inspection and external-process behavior advisory-only:

- reads a FluidGateway operational ledger;
- samples process CPU, working set, private memory, thread count, and host RAM;
- produces scheduler and memory-residency action candidates;
- explicitly blocks actions that need a native backend or privilege;
- streams owned D3D11 hook events into the managed runtime through shared memory;
- never changes process priority, affinity, RAM/VRAM residency, GPU queues,
  drivers, games, or OS state.

It also contains deliberately narrow actuation experiments. The original
`copy-elision-lab` command runs repeated baseline/optimized pairs of the owned
deterministic target, then allows each optimized run to skip at most one proven
redundant `CopyResource`. The command fails unless readback hashes, event
accounting, and hook rollback agree across all runs. This is not enabled for
external software.

Version 0.10.0 adds the first memory-direction-specific intervention:
`readback-elision-lab`. The owned target performs one required and 64 unchanged
4 MiB `CopyResource` operations from an API-visible `DEFAULT` resource into a
CPU-readable `STAGING` resource, mapping and comparing all bytes after every
copy. A dedicated action-2 policy lets the optimized run forward one readback
copy and skip 64 while retaining all 65 maps. On the RX 580, 22/22 raw runs
preserved content, event accounting, adapter identity, and rollback; all ten
measured pairs favored the optimized path in CPU and guarded GPU timestamp
intervals.

Version 0.9.0 adds `sustained-copy-lab`. A managed policy may spend a bounded
budget of up to 128 actions on unchanged repeats inside a 4 MiB owned buffer
workload. With the default budget, each optimized run removes 128 redundant
`CopyResource` calls, or 512 MiB of logical GPU copy traffic. Separate target
processes, exact readback hashes, event/snapshot agreement, adapter identity,
GPU timestamp validity, and rollback are mandatory before the report can pass.

Every positive performance claim is explicitly scoped in its JSON. The v0.10
readback gate requires CPU and GPU p50/p95 improvement plus at least 80% paired
wins in both directions. It is not a game-wide FPS, PCIe, residency, or power
claim.

Version 0.8.0 added the first managed control plane. In `manager-lab`, the .NET
runtime opens the target's shared mapping and publishes one short-lived policy
epoch with a one-action budget. The native hook validates and acknowledges the
policy, then may consume it only for the same proven redundant `CopyResource`
used by the owned experiment. The hook uses cached atomics on the API path; it
does not call managed code per copy. The report exposes generic copy and
readback control as active only in owned labs, while CPU scheduling and physical
RAM/VRAM residency stay blocked and presentation stays observe-only.

Detach is a reversible dispatch boundary, not an unsafe unload shortcut. The
module, event mapping, original function pointers, and Release-slot metadata
remain valid until process exit. A delayed hook pointer only forwards while
detached, without changing observation state. To prevent a delayed pointer or
surviving reader from crossing attachment generations, a successful attach is
one-shot per process; the owned stress proves that a later attach is rejected.

The owned target also exercises cooperative resource retirement. Retiring a
resource removes its active state and copy provenance, while any later reuse of
the same pointer receives a new monotonic resource ID. The managed reader
reconstructs active and retired IDs and rejects writes or copies involving a
retired resource. This is not automatic interception of COM destruction.

Version 0.7.1 added an explicit owned-lab lifetime mode that patches the
`IUnknown::Release` slot of the returned `ID3D11Buffer` and `ID3D11Texture2D`
interfaces. The target executes 64 automatic destruction cycles, restores every
dynamic slot at detach, and separately verifies that normal
`FluidHookAttach` keeps zero Release hooks and uses cooperative retirement only.

Version 0.7.2 tracks generations per Buffer/Texture2D subresource. The owned
workload proves that a write to mip 0 does not invalidate unchanged mip 1,
while a write to mip 1 does. Eight `CopySubresourceRegion` calls are observed;
three repeated regions are candidates, but all eight are forwarded.

Version 0.7.3 resolves owned `ID3D11RenderTargetView` and
`ID3D11UnorderedAccessView` objects back to their Texture2D subresources. It
observes `ClearRenderTargetView` and `ClearUnorderedAccessViewFloat` as GPU
writes, preserves unrelated-mip provenance, and invalidates the addressed mip.
Views for pre-attach resources such as the swap-chain backbuffer are outside
the owned-resource scope and do not become fabricated tracked resources.

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
buffer and texture creation, read/write `Map/Unmap`, `UpdateSubresource`,
`ClearRenderTargetView`, `ClearUnorderedAccessViewFloat`,
`CopySubresourceRegion`, and `CopyResource`. Detach restores every current
original vtable entry. Version 0.8 pins the DLL inside the owned target until
process exit so a delayed call cannot enter unloaded hook code; detach disables
actuation and restores dispatch, but does not remove the module image early.
Retained entrypoints are observation-neutral while detached. A new session
requires a new owned target process.

```powershell
native/build/Release/fluidruntime-hook-target.exe `
  --hook native/build/Release/fluidruntime-present-hook.dll `
  --frames 120 `
  --hardware `
  --out artifacts/present-hook-lab.json
```

The deterministic workload performs six resource copies. Three repeat an
unchanged source/destination generation and are reported as candidates, never
skipped by the default mode. The expected result is 49,152 bytes observed and
24,576 bytes classified as potentially avoidable.

The same workload performs eight full-mip copies, two partial copies, and one
empty-box no-op. Five are repeated regional candidates (5,120 bytes), but all
11 regional calls remain diagnostic-only and are forwarded. A clear through
the mip-0 RTV preserves mip-1 provenance; a clear through the mip-1 UAV
invalidates the next mip-1 repeat.

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
subresource indices, generations, lifecycle transitions, active-resource
references, precise GPU-view write flags, copy/write counts, and byte estimates
against the target snapshot before accepting a report.

Run the first controlled before/after intervention:

```powershell
dotnet run --project src/FluidRuntime -c Release -- copy-elision-lab `
  --target native/build/Release/fluidruntime-hook-target.exe `
  --hook native/build/Release/fluidruntime-present-hook.dll `
  --frames 300 `
  --hold-ms 500 `
  --gpu-timeout-ms 1000 `
  --trial-pairs 10 `
  --warmup-pairs 1 `
  --hardware true `
  --out artifacts/copy-elision-comparison.json
```

Run the same intervention through the managed control plane:

```powershell
dotnet run --project src/FluidRuntime -c Release -- manager-lab `
  --target native/build/Release/fluidruntime-hook-target.exe `
  --hook native/build/Release/fluidruntime-present-hook.dll `
  --frames 300 `
  --hold-ms 500 `
  --gpu-timeout-ms 1000 `
  --trial-pairs 10 `
  --warmup-pairs 1 `
  --hardware true `
  --out artifacts/manager-control-comparison.json
```

Run the bounded sustained intervention and paired GPU measurement:

```powershell
dotnet run --project src/FluidRuntime -c Release -- sustained-copy-lab `
  --target native/build/Release/fluidruntime-hook-target.exe `
  --hook native/build/Release/fluidruntime-present-hook.dll `
  --copy-count 128 `
  --trial-pairs 10 `
  --warmup-pairs 1 `
  --hold-ms 50 `
  --gpu-timeout-ms 5000 `
  --hardware true `
  --out artifacts/sustained-copy-hardware.json
```

Run the bounded `DEFAULT -> STAGING + CPU_READ` intervention:

```powershell
dotnet run --project src/FluidRuntime -c Release -- readback-elision-lab `
  --target native/build/Release/fluidruntime-hook-target.exe `
  --hook native/build/Release/fluidruntime-present-hook.dll `
  --trial-pairs 10 `
  --warmup-pairs 1 `
  --hold-ms 50 `
  --gpu-timeout-ms 5000 `
  --hardware true `
  --out artifacts/readback-elision-hardware.json
```

Each pair contains a baseline and optimized process, and pair order alternates
to reduce first/second-run bias. Warmups remain in the trace but are excluded
from statistics. Baselines forward all six copies; optimized runs observe the
same six, forward five, and skip the first redundant 4,096-byte buffer copy.
`copy-elision-lab` requests that bound through immutable attach options;
`manager-lab` publishes it after the target is alive and waiting for policy.
After hook detach, the target reads buffer and texture contents back through
staging resources, compares logical bytes exactly, and computes stable FNV-1a
hashes for the report, including the addressed mip.

`sustained-copy-lab` uses the managed policy path with a bounded action budget.
Its baseline forwards all 135 whole-resource copies; the default optimized run
forwards seven and skips 128 sustained repeats. This is evidence for the owned
GPU workload only. It is not evidence of lower end-to-end frame time, lower CPU
cost, higher FPS, or a benefit in an external game.

`readback-elision-lab` uses a separate policy bit and a fixed budget of 64.
Baseline runs forward 65 readback copies; optimized runs forward one and skip
64, avoiding 268,435,456 logical copy bytes while retaining and verifying all
65 maps. This does not prove physical VRAM placement or PCIe byte reduction.

Version 0.6 measures the workload with CPU QPC and D3D11 GPU timestamp queries
guarded by `TIMESTAMP_DISJOINT`. It reports paired p50/p95 distributions,
execution order, every raw run, and explicit performance-claim blockers.
Missing, timed-out, disjoint, zero-frequency, or insufficient GPU evidence is
never reported as a zero-cost success.

This is not remote injection and is not intended for protected or anti-cheat
processes. It exists to verify resource observation, runtime thunk transitions,
concurrent-call draining, and rollback in software we own before any
external-process work is considered. Candidate detection and copy elision
currently assume the controlled workload. Automatic destruction is only proven
for the same returned Buffer/Texture2D interface in the owned lab; interface
aliases, shader draw/dispatch writes, other clear operations, fences, deferred
contexts, and general resource-view aliasing are not covered yet. Regional-copy
candidates remain diagnostic-only. The manager currently supports one epoch,
one selected action from two exact action bits, and a bounded budget of at most
128 actions per owned target process. It is the control-plane foundation for
broader scheduling and memory work, not yet a general game manager.
