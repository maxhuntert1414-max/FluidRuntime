# Upload Elision Research

## Problem

FluidRuntime v0.10.0 proves a bounded, direction-specific readback intervention.
The opposite API-visible path is still folded into the generic `CopyResource`
lane. That prevents the manager from authorizing CPU-written upload traffic
without also authorizing unrelated copies.

The v0.11.0 question is deliberately narrower than RAM/VRAM residency:

> Can an owned D3D11 workload safely skip repeated whole-resource copies from
> an unchanged CPU-writable staging buffer into the same unchanged default
> buffer?

## D3D11 Facts

Microsoft documents these constraints:

- `D3D11_USAGE_STAGING` permits CPU writes and GPU access only for copy
  operations.
- `D3D11_CPU_ACCESS_WRITE` makes a dynamic or staging resource CPU-mappable.
- `D3D11_USAGE_DEFAULT` leaves physical memory selection to the runtime,
  driver, and memory manager. It is not a promise of dedicated VRAM.
- `CopyResource` copies compatible whole resources using the GPU, cannot copy
  a currently mapped resource, and is asynchronous with respect to command
  submission.
- `Map` denies GPU access to the mapped subresource; `Unmap` invalidates the
  CPU pointer and restores GPU access.

Primary references:

- <https://learn.microsoft.com/windows/win32/api/d3d11/ne-d3d11-d3d11_usage>
- <https://learn.microsoft.com/windows/win32/api/d3d11/ne-d3d11-d3d11_cpu_access_flag>
- <https://learn.microsoft.com/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-copyresource>
- <https://learn.microsoft.com/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-map>
- <https://learn.microsoft.com/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-unmap>

## Existing Provenance

The native hook records resource usage and CPU-access flags at creation. A
successful write map enters a pending set, but the resource generation advances
only after the matching `Unmap`. Every later redundant-copy decision compares:

- trusted source and destination identities;
- unchanged source generation;
- unchanged destination generation;
- the same source identity previously copied to that destination.

This is enough to prove unchanged API-visible resource history. It does not
prove byte identity across two different CPU writes and must never skip the
first copy after an `Unmap` write.

## Options Considered

1. Hash CPU-mapped bytes inside the hook.
   Rejected for v0.11.0 because it adds an intrusive full-buffer CPU read to a
   write-only path and changes the workload being measured.
2. Intercept `UpdateSubresource` and deduplicate arbitrary CPU pointers.
   Rejected for the first upload milestone because pointer lifetime, boxes,
   pitches, deferred contexts, and driver copy behavior need a separate proof.
3. Classify unchanged `STAGING + CPU_WRITE -> DEFAULT` `CopyResource` calls.
   Selected because existing generation provenance can establish redundancy
   without reading application memory or claiming physical placement.

## Threat Model And Boundaries

The v0.11.0 action is valid only when:

- both resources were observed at creation and remain provenance-trusted;
- source usage is staging and includes CPU write access;
- destination usage is default;
- the whole-resource copy is already a proven redundant candidate;
- the owned target opted into the shared-memory control plane;
- action 3 is accepted, unexpired, and still has budget;
- source and destination were not destroyed, reused, or written since the
  required copy.

Action 1 cannot authorize uploads. Action 2 cannot authorize uploads. A new
write map invalidates the source generation at `Unmap`, so the next copy must be
forwarded.

## Evidence Contract

Each baseline and optimized process must prove:

- one successful staging write map and unmap;
- one required upload plus 64 unchanged repeated uploads of 4 MiB;
- baseline forwards all 65 upload copies;
- optimized forwards one and skips exactly 64 under action 3;
- the default destination hashes to the expected CPU payload after detach;
- source staging and destination default hashes match after detach;
- event, snapshot, control block, adapter, sequence, and byte totals agree;
- hook detach restores original pointers before verification.

WARP proves function only. A positive performance claim requires at least ten
paired hardware trials, alternating order, valid GPU timestamps, matching
adapter identity, improved GPU p50/p95, and optimized GPU wins in at least 80
percent of pairs.

`CopyResource` is asynchronous command submission, so lower GPU work does not
imply lower CPU submission time. CPU p50/p95 and every measured pair must stay
inside a predeclared overhead envelope of at most 1,000 microseconds and 10
percent. The allowed statement is therefore GPU-interval improvement with
bounded CPU submission overhead, not CPU acceleration.

## Claim Language

Allowed scope:

`owned-d3d11-writable-staging-to-default-upload-copy-workload-only`

Not established:

- physical RAM-to-VRAM bytes or PCIe traffic;
- residency, migration, eviction, or allocation placement;
- identical payloads across distinct CPU writes;
- dynamic-resource or `UpdateSubresource` optimization;
- external-game compatibility, FPS, power, or system-wide scheduling.
