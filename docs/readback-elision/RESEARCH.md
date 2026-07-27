# VRAM-to-RAM Readback Elision Research

## Overview

Build the first bounded FluidRuntime experiment for reducing redundant data
movement from a GPU-oriented D3D11 resource into CPU-readable staging memory.
The public shorthand is VRAM-to-RAM, but the implementation and claim use the
API-observable resource classes because WDDM and the driver choose physical
placement.

## Problem Statement

Applications commonly copy a `D3D11_USAGE_DEFAULT` resource into a
`D3D11_USAGE_STAGING` resource with `D3D11_CPU_ACCESS_READ`, then call
`Map(D3D11_MAP_READ)` so the CPU can inspect the result. Repeating this sequence
while the source is unchanged can enqueue redundant GPU copy work and force
avoidable CPU/GPU synchronization.

FluidRuntime v0.9.0 can prove and elide repeated whole-resource copies, but it
does not classify transfer direction, observe CPU read maps, or restrict a
policy specifically to the readback path.

## User Stories / Use Cases

- As a contributor, I can distinguish a GPU-default-to-CPU-staging transfer
  from a GPU-internal copy.
- As a reviewer, I can verify that every skipped readback had an unchanged
  source and a still-valid staging destination.
- As a benchmark operator, I can compare the full copy-plus-map path in separate
  baseline and optimized processes.
- As the future manager, I can authorize only readback elision without granting
  authority over every redundant copy candidate.

## Technical Research

### D3D11 Semantics

Microsoft documents:

- `D3D11_USAGE_DEFAULT` as the common GPU-oriented resource usage.
- `D3D11_USAGE_STAGING` as supporting data transfer from GPU to CPU.
- `D3D11_CPU_ACCESS_READ` as requiring staging usage and making the resource
  CPU-readable rather than pipeline-bindable.
- `CopyResource` as a whole-resource copy performed by the GPU.
- `Map` as giving the CPU access while denying GPU access to the mapped
  subresource.
- Mapping a staging target before its queued copy completes can force a pipeline
  stall and CPU/GPU synchronization.

The driver and WDDM choose physical placement. Therefore the experiment cannot
claim literal PCIe bytes or guaranteed physical VRAM-to-RAM movement.

### Approach Options

1. **RAM-to-GPU upload deduplication first**
   - Requires hashing or otherwise identifying caller-owned payloads in
     `UpdateSubresource` and write-map paths.
   - Must handle row/depth pitch, partial boxes, aliases, and caller mutation.
   - Valuable, but broader than the currently proven provenance model.
2. **GPU-default-to-staging readback elision first**
   - Reuses whole-resource source/destination generation proof.
   - Adds explicit staging and CPU-read classification.
   - Can verify every CPU map against known deterministic bytes.
   - Cleanly separates a new policy action from generic copy elision.

### Recommended Approach

Start with option 2:

- one 4 MiB `D3D11_USAGE_DEFAULT` source with deterministic contents;
- one matching `D3D11_USAGE_STAGING | D3D11_CPU_ACCESS_READ` destination;
- one required copy and read map;
- 64 unchanged copy-plus-map repetitions;
- a dedicated `skip_redundant_readback_copy` policy action with budget 64;
- exact comparison on every map plus stable first/final hashes;
- baseline and optimized processes in alternating order;
- performance authorization only when CPU and GPU p50/p95 improve and at least
  80 percent of pairs favor optimized on both metrics.

### Data Requirements

Each run must expose and validate:

- source usage, destination usage, and CPU access classification;
- readback copy calls/bytes, forwarded calls, and skipped calls;
- successful CPU read-map calls and bytes;
- policy action mask, budget, acknowledgment, applied count, and final status;
- exact per-map content agreement and first/final staging hashes;
- CPU QPC duration and valid disjoint-guarded GPU timestamp duration;
- event sequence continuity, native overrun count, adapter identity, and detach
  rollback.

## Integration Points

- `native/include/fluidruntime_hook_api.h`: snapshot/ring version and action/event
  contracts.
- `native/src/present_hook.cpp`: resource access class, map-read observation,
  readback direction, counters, and action-specific reservation.
- `native/src/hook_target.cpp`: owned readback workload and native validation.
- `src/FluidRuntime/Native`: ABI reader and readback-policy publisher.
- New managed readback lab CLI/report/runner.
- CI and evidence docs.

## Risks and Challenges

- **Physical-memory overclaim:** use API-visible usage language and explicitly
  reject one-to-one PCIe/VRAM claims.
- **Stale staging data after a source write:** source/destination generations and
  trusted provenance must match before a skip.
- **Implicit synchronization semantics:** optimized runs still execute the same
  CPU read maps; only proven redundant copies are removed.
- **CPU verification dominates timing:** keep exact comparisons inside both
  baseline and optimized paths and report CPU/GPU separately.
- **Broad policy authority:** use a distinct action bit accepted only for
  default-to-readable-staging candidates.
- **Event overflow:** 64 repetitions keep the trace below the 1,024-slot ring;
  any loss or overrun fails closed.

## Open Questions

- Whether 64 x 4 MiB produces a stable CPU and GPU improvement on the RX 580.
- Whether future readback scheduling should cache for a bounded frame epoch
  rather than a process-local action count.
- Which upload path should follow: immutable payload hash for
  `UpdateSubresource`, dynamic-buffer versioning, or staging upload reuse.

## References

- https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ne-d3d11-d3d11_usage
- https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ne-d3d11-d3d11_cpu_access_flag
- https://learn.microsoft.com/pt-br/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-copyresource
- https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-map
- https://learn.microsoft.com/en-us/windows/uwp/graphics-concepts/copying-and-accessing-resource-data
- https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ne-d3d11-d3d11_query
