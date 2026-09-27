# Shared Arena v1: Validation and Allocation Reuse

Captured 2026-09-27. Scope: owned Windows x64 D3D12 linear buffers through the
real `FluidMemoryArena.dll` C ABI. The daily monitor, external application
observation, Gateway release pin and FluidLink contracts are unchanged.

## Result

The cooperative arena passes local lifetime, exact-content and failure tests.
Reusing a buffer across eight operations creates **120 backing objects instead
of 960** over 30 trial pairs: **87.5% fewer creations**. This is an allocation
count, not 87.5% less RAM/VRAM or a claim about game performance.

Both measured arms copy **7,864,320 logical CPU bytes** and **7,864,320 logical
GPU bytes**. No GPU transfer is omitted. Peak charged backing is 212,992 bytes
in each capture, against a 67,108,864-byte limit. Driver/context overhead and
working-set residency are outside that charge.

## Environment and Method

- Host: Windows 11 Pro, build 22631; Xeon E5-2650 v2, 8 cores / 16 logical CPUs.
- Physical GPU inventory: AMD Radeon RX 580 2048SP, driver 31.0.21924.61.
  A Parsec virtual display adapter is also installed. Hardware mode selects a
  D3D12-capable non-software adapter by high-performance preference; these arena
  JSON reports record the hardware/UMA flags, not the adapter name or LUID.
- MSVC 19.51.36243.0, CMake 4.2.3-msvc3, Windows x64. AddressSanitizer uses Debug
  with `halt_on_error=1:abort_on_error=1:detect_leaks=0`.
- All four captures use the D3D12 debug layer and report zero debug errors.
  The library itself never enables the host's process-wide debug layer.
- One excluded warmup batch per arm, then 30 alternating-order paired batches,
  eight operations per batch, 240 measured operations per arm, 16 KiB payloads.
- Every operation writes the host span, uploads, reads back and compares all
  bytes. The baseline creates/releases one section plus three GPU buffers each
  time; the reused arm retains one such set across eight operations.

**Timing boundary:** device creation is outside both arms. Baseline buffer
allocation/release is timed per operation. Reused-buffer setup/release is outside
its timed batch, although included in allocation counts. These are therefore
steady-batch reuse measurements, not complete end-to-end setup amortization.
Final captures ran sequentially after all build/test workers had finished.

The report stores aggregate operation percentiles, not raw per-pair samples or
confidence intervals. Backing counts exclude general CRT/driver allocations.
CPU copy bytes count explicit payload copies, not bus transactions or cache
traffic. No full-process RSS/handle soak or GPU timestamp interval is claimed.

## Release Hardware Results

| Metric | Recreate each operation | Reuse across eight |
| --- | ---: | ---: |
| Operation p50, us | 2,522.150 | 573.850 |
| Operation p95, us | 3,792.750 | 727.115 |
| Operation p99, us | 5,026.330 | 878.804 |
| Batch p50, us | 20,672.100 | 4,595.050 |
| Operations/second | 365.915 | 1,742.950 |
| Process CPU cycles/operation | 2,839,770 | 870,748 |
| Process CPU us/operation | unavailable | unavailable |
| Backing creations | 960 | 120 |

The process CPU-time counter returned a zero delta at its coarse resolution;
JSON uses `null`, not zero cost. Process cycles are a separate measurement and
are not converted to wall time. Many engines already pool resources; this lab
does not show an advantage over such engines. Debug-layer instrumentation and
this host's load also limit timing generalization.

## Other Captures

| Configuration | p50 baseline/reused, us | p95 baseline/reused, us | p99 baseline/reused, us |
| --- | ---: | ---: | ---: |
| Release WARP | 994.400 / 412.200 | 1,065.710 / 481.280 | 1,094.230 / 530.676 |
| Debug hardware | 2,976.400 / 768.450 | 3,499.320 / 870.440 | 3,740.530 / 1,038.800 |
| ASAN Debug hardware | 5,086.350 / 1,334.250 | 5,498.940 / 1,524.220 | 5,758.010 / 1,589.750 |

Debug/ASAN and WARP are correctness controls, not product performance gates.

## Validation Gates

- Native CTest: **44/44** each in Release, Debug and ASAN Debug, including existing
  D3D11/D3D12/Vulkan regressions. ASAN is not a leak or race detector in this run.
- MSVC `/analyze`: arena DLL, C/C++ client and GPU-timeout fault target passed.
- Managed suite: **312/312**, no skips, each in Release and Debug with the actual
  pinned Gateway v0.69.0 DLL and native targets, warnings treated as errors.
- Windows PowerShell 5.1 evidence helper: four complete captures passed.
- Release DLL/lab loaded with a Windows-only `PATH`, without Python, .NET or ASAN
  discovery. Import inspection found only the arena DLL and Windows DLLs.
- Scoped native formatting, documentation links and README process/CPU-monitor
  command smoke tests passed. Monitoring only observed the owned test shell.

Arena cases cover C11 layout/ABI and binary reader golden vectors; wrong/null
arguments and stale handles; aliases, partial writes, abort and recovery; stale
readback; read-only peer mappings and PID/start-time/image-hash mismatches; normal
and abnormal peer exit; lease expiry; allocation alignment/budget and 64-slot
limits; 100 allocation/release cycles; four concurrent writers; and permanent
opaque-token exhaustion. The report's `checks` includes variable polling loops,
so its exact value is not a stable test-count contract.

The timeout test stalls an owned WARP queue behind a private fence. A timed-out
transfer closes admissions, retains backing and rejects destruction with `BUSY`.
Signaling the gate permits safe cleanup but does not revive the closed session.
No fault-injection entry point is exported by the production DLL.

## Reproduce

See [build and lab commands](../shared-arena.md#run-the-owned-lab) and
[the ABI](../shared-arena-abi.md). Select each built configuration explicitly:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Test-SharedArena.ps1 `
  -NativeDirectory native/build/Release -Hardware `
  -OutputPath artifacts/shared-arena-hardware.json
```

Omit `-Hardware` for WARP. Use the selected compiler's ASAN runtime on the child
`PATH` only for sanitizer builds; never distribute that build for daily use.
The helper validates evidence/correctness, not that one timing arm must win.

## Frozen Artifacts

SHA256 of the committed UTF-8 JSON bytes:

| Report | SHA256 |
| --- | --- |
| [Release hardware](traces/shared-arena/release-hardware.json) | `8931ec99da23fd7cab434a547e7651e02a0a1e0725b6a641bdda8e32e4724e79` |
| [Release WARP](traces/shared-arena/release-warp.json) | `a0d6b7787c318942c9cb9f38558c93bdb00c023f2e530e0f03340aa4720df2e4` |
| [Debug hardware](traces/shared-arena/debug-hardware.json) | `29082abf34fdb0114e17ec78f2be85141f3e9bdd1d1e59d80292c73e0039115d` |
| [ASAN Debug hardware](traces/shared-arena/asan-hardware.json) | `6abf2b923534eff617d7e962497d40100891259f198b20f08efd959366b05481` |

Local Release binary identities (not downloadable release assets or signatures):

| Binary | SHA256 |
| --- | --- |
| `FluidMemoryArena.dll` | `391b557428fdbf03a1a738415560c7c61f51590e58aea31e1825da93ce2a6481` |
| `fluidruntime-memory-arena-lab.exe` | `24acfccf81b8dddfafcd0900dab37589faf98a817df22e6d1fa23ee2e9f936ba` |
| `fluidruntime-memory-arena-fault-tests.exe` | `8d1616c0090aa3aac7d10d95e911cf2e5cf218ea839edd324f604f8ca9cdee82` |

## Next Gate

Integrate a trusted managed/native client with explicit resource binding and
deterministic cleanup. Then measure a full setup-inclusive batch against an
already-pooled baseline and sustain it under disconnects and faults. Policy-backed
replica reuse needs separate negotiation and original-copy fallback. This milestone
does not enable transparent unified memory, third-party copy removal, FPS/input
latency gains, residency management or a replacement Windows scheduler.
