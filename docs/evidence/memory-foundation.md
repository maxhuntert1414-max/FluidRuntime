# Shared Memory Foundation Validation

Final local validation: 2026-09-21, Windows x64, MSVC 14.51.36231,
Windows SDK 10.0.26100.0. This validates the foundation, not a production arena.
Gateway v0.69.0 binaries, FluidLink 0.5.0 and the daily portable package are unchanged.

## Results

| Surface | Result |
| --- | --- |
| Coherence core | 50,099 checks, 10,000 slot reuse cycles, 8,000 commits from four host threads |
| Default metadata state | 6,784 fixed bytes in these x64 builds; no payload/history retained |
| Native Release / Debug / ASAN | 42 CTest tests each, zero failures; includes existing D3D11/D3D12/Vulkan tests |
| Managed Release / Debug | 312 tests each, zero skips; real Gateway DLL and native integrations enabled |
| Gateway Python regression | 343 passed; Gateway changes are documentation only |
| Both new native targets | MSVC `/analyze` with warnings as errors, passed |
| Validation wrapper | Executed under Windows PowerShell 5.1; strict report checks passed |
| Formatting | Scoped clang-format check passed for the three new native files |
| Standalone Release lab | WARP roundtrips passed with Windows-only PATH, without Python/.NET/ASAN resolution |

Regression coverage includes stale generations, alias invalidation, partial writes
against stale replicas, forged/replayed tickets, invalid ranges, bounds, slot and
serial exhaustion, pending/wrong/future fences, aborted writes and device removal.
The cross-allocation GPU test refuses a second GPU ticket until the first finishes;
independent host access remains possible. GPU abort closes admissions for the whole
session. A later completion cannot silently revive it.

## Exact-Content Lab

Every capture verified a 16-KiB read-only mapping in a separate owned process and
denied a writable view there. Four D3D12 roundtrips then checked every byte after
initial upload, alias mutation, GPU-side replacement and full recovery after an
abandoned CPU write. All barriers/fences/copies remain.

| Capture | Adapter | UMA / cache-coherent UMA | Roundtrips | Logical GPU copy bytes | Omitted bytes | Debug errors |
| --- | --- | --- | --- | --- | --- | --- |
| [Release hardware](traces/memory-foundation/release-hardware.json) | RX 580 2048SP | false / false | 4 | 131,072 | 0 | 0 |
| [Debug hardware](traces/memory-foundation/debug-hardware.json) | RX 580 2048SP | false / false | 4 | 131,072 | 0 | 0 |
| [ASAN hardware](traces/memory-foundation/asan-hardware.json) | RX 580 2048SP | false / false | 4 | 131,072 | 0 | 0 |
| [Release WARP](traces/memory-foundation/release-warp.json) | Microsoft Basic Render Driver | true / true | 4 | 131,072 | 0 | 0 |

The D3D12 debug layer was available and enabled in all four captures. GPU-upload
heap support was unavailable (`null`), not asserted false. WARP's architecture
flags describe the software adapter, not this PC's physical GPU. Memory budgets
are driver snapshots, not fixed installed capacity or measured traffic.

Captures use the repository's LF line endings. SHA256: hardware reports are
byte-identical across configurations,
`7bdb19ebe87e794ed10e125b4d8b162934ae1a56063912272d318d77157eb595`.
WARP: `13c77ff4610b7e31ca35b3bab7374e3805a5839807b086a34d716fa568d8c178`.
Executables used for the captures have these SHA256 values:

| Configuration | `fluidruntime-memory-readiness.exe` SHA256 |
| --- | --- |
| Release | `45a6eb26cff6bac96980ab655cd36c8f179287bbff54158e95d2fb627b37a189` |
| Debug | `4fd4e77c9ff969b094cc96ba5371e60e114893c93eddb74f3750d5f33e4c01e3` |
| ASAN | `9697141b560c502ad2390b9010502ae15dcab89de4bb9fc946f25318b65e0ebb` |

Reproduce with [Test-MemoryFoundation.ps1 and the build commands](../shared-memory-foundation.md).
Write new runs to `artifacts/`, not over these frozen captures. Local full-suite
logs are `artifacts/memory-foundation-*.log`; the Gateway log is
`FluidGateway/tmp/memory-foundation-python.log`. Exact publication SHAs and CI
results are recorded in the canonical workspace checkpoint.

## What Is Not Established

- Performance improvement, latency percentiles, physical PCIe traffic or game FPS.
- Leak-freedom or a sustained workload soak; ASAN used `detect_leaks=0`.
- An allocated 64-MiB arena: the core bounds metadata and logical buffer sizes only.
- Cross-process GPU heaps, untrusted writable producers or arbitrary game resources.
- Real GPU device-removal injection: the core tests use deterministic fence evidence.
- A stable C ABI, new FluidLink capability or automatic copy-elision authority.

The foundation is ready for the next **cooperative arena prototype**. That backend
still must implement the physical cap, peer identity, lease lifetimes, staging,
exception-contained ABI and measured original-copy fallback before optimization
can be enabled. See the [v1 decision](../architecture/shared-memory-v1.md).
