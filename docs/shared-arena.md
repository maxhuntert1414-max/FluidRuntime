# Cooperative D3D12 Shared Arena

`FluidMemoryArena.dll` is an opt-in Windows x64 C++20 library for owned buffers.
It implements the first arena on top of the [coherence foundation](shared-memory-foundation.md).
It is not loaded by the daily monitor and does not attach to games or change Windows.

## Implemented Boundary

- One live arena per DLL instance, one owned D3D12 device/queue and at most 64 buffers.
  Load only one instance per process; this is not a system-wide memory-budget enforcer.
- At most 64 MiB of **charged backing**: page-rounded OS sections plus the
  driver-reported resource allocation size for each UPLOAD/DEFAULT/READBACK buffer.
  This includes alignment, not just requested payload bytes. Driver/device/context
  overhead, virtual address space and working-set residency are not this budget.
- A versioned C ABI with opaque non-recycled tokens, fixed-width fields, caller-owned
  outputs and contained C++ exceptions. C compilation/layout and real DLL calls are tested.
- Local host read/write spans with explicit acquire/publish/abort. All aliases share
  allocation-level coherence. Partial writes cannot repair unknown content.
- Explicit synchronous upload/readback. A stale readback cannot overwrite newer CPU
  bytes. No GPU copy is omitted, even when both replicas are already current.
- Whole-buffer read-only exports to an explicitly supplied, verified process handle.
  PID, creation time and image SHA256 must match; the retained handle prevents PID reuse.
- Fixed ownership bookkeeping; a global try-lock returns `BUSY` to competing callers.
  No locks, pointers, authority metadata or writable payload handles are shared with peers.

The library never enables the host's process-wide D3D12 debug layer. The owned lab
enables it before creating devices. Existing Gateway server/DLL and FluidLink
contracts remain unchanged: coherence tickets do not authorize graphics optimization.

## Run the Owned Lab

```powershell
cmake -S native -B native/build -A x64
cmake --build native/build --config Release
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Test-SharedArena.ps1 `
  -NativeDirectory native/build/Release -Hardware
```

Omit `-Hardware` for explicit WARP. A hardware request never silently uses software.
The helper validates the real GPU-timeout cleanup regression, runs correctness tests
and 30 paired allocation-reuse trials, then writes `artifacts/shared-arena.json`.
Use `-OutputPath` for separate runs. It only launches its own processes.

## Minimal Native Client

Include `native/include/fluid_memory_arena.h`, link `FluidMemoryArena.lib` and keep
the matching DLL beside the executable. The complete checked client is
[memory_arena_lab.cpp](../native/src/memory_arena_lab.cpp). Its normal lifecycle is:

```text
create -> allocate -> acquire(HOST_WRITE) -> write span -> end(publish=1)
       -> transfer(UPLOAD) -> transfer(READBACK)
       -> acquire(HOST_READ) -> consume span -> end(publish=1)
       -> release -> destroy
```

See the [ABI and local reader protocol](shared-arena-abi.md) for exact rules. C#
marshalling is a future integration, not something the existing manager now invokes.
Valid local pointers and correct caller buffer sizes are required. This ABI is not
a sandbox against code already executing inside the host process.

## Lifetime and Failure Rules

- A borrowed span expires at `fma_end`. Never retain, unmap, resize or access it
  outside its range; do not race pointer use with ending its lease.
- `fma_end(..., 0)` abandons content. A complete host overwrite can recover it while
  the arena remains open. Bounds violations, expired leases and internal failures
  close admissions. Deadlines are observed at API entry, not by a background service.
- A read export stays pinned until the verified process exits. An acknowledgment
  cannot prove a process unmapped every view. Cooperative readers must not forward
  or duplicate the grant into another process. This v1 is deliberately restrictive
  and does not support rotating leases in a long-lived external application.
- Abnormal reader exit or lease expiry closes the session. Normal exit permits
  `fma_end_read`; external views must be gone before new writers can acquire access.
  The library never terminates a peer or closes a remote numeric handle that might
  have been recycled. The test harness alone terminates its own fault-test child.
- GPU timeout/failed submission closes the arena and retains its backing. Destruction
  returns `BUSY` until the private fence proves completion or the device reports loss.
  A later fence does not reopen the session. Do not unload the DLL while any arena or
  span remains live. Unknown progress may require the owning process to exit.
- `fma_close` revokes admissions but does not force-free. End outstanding local/peer
  leases, then release/destroy. Closed leases return `CLOSED` after cleanup without
  publishing content. A new arena cannot reuse old buffer or lease tokens.

Image hashing is bounded to files up to 256 MiB. Identity checks authenticate the
expected process object and image file, not its entire in-memory code or a hostile
same-user security boundary. Writable external
producers, borrowed D3D12 resources, GPU shader access, multi-queue workloads, textures,
Vulkan arenas, automatic migration and third-party optimization are not v1 features.

## Measurement and Next Step

The A/B workload compares recreating four backing objects every operation with
retaining one buffer for eight operations. Both arms execute identical writes,
uploads, readbacks and full byte comparisons. Many real engines already pool their
resources; this is not evidence of outperforming such an engine.

Latency percentiles cover complete client operations. Reused-buffer setup/teardown
is outside its timed batch, but included in backing-allocation counts. Device setup
is outside both arms. One warmup batch per arm precedes 30 alternating-order pairs.
Reports include p50/p95/p99, median batch time, throughput, logical copy bytes and
backing allocations (not every CRT/driver allocation). CPU time uses coarse process
accounting; a zero observed delta is reported as `null`, not zero cost.
[Process CPU cycles](https://learn.microsoft.com/en-us/windows/win32/api/realtimeapiset/nf-realtimeapiset-queryprocesscycletime)
are also recorded; they are not converted to wall time or compared across machines.
The lab runs with debug validation when available; it is not a production-speed claim.

Next: integrate a trusted managed/native client and resource binding, then negotiate
Gateway policy for reuse/transfer elision with original-copy fallback. Keep the
isolated Gateway backend available. Do not infer physical unified memory, PCIe,
FPS, input-latency or power savings from this allocation benchmark.
See [frozen validation evidence](evidence/shared-arena.md).
