# ADR: Versioned Cooperative Memory Before Transparent Memory

Status: accepted for the first cooperative prototype; 2026-09-19.

## Context

FluidRuntime already observes graphics transfers and completion. Shared OS pages
alone do not establish the latest resource content, safe CPU/GPU access or the
ability to skip a transfer. The project needs an explicit coherence contract
before adding a shared payload arena.

## Decision

Keep Gateway policy separate from trusted Runtime ownership/content bookkeeping.
Use one device/queue, linear registered buffers, whole-replica transfers and
exclusive leases first. Track aliases by allocation identity, not raw pointer or
driver-handle equality. Metadata never lives in writable peer mappings.
Only one GPU ticket is outstanding per session until submission ordering can be
proved by the owning backend; independent host operations can still overlap.

The internal C++20 core is consumed by real D3D12 validation now. The future
arena will wrap it in a C ABI; no new protocol or ABI is declared stable here.
CPU sections and GPU resources remain different objects with explicit transfers.

| Alternative | Trade-off | Decision |
| --- | --- | --- |
| Transparent global Windows allocator/hooks | Unknown writes, API states, anti-cheat and process lifetimes | Not a safe starting point |
| Shared GPU heaps everywhere | Restrictions and platform-dependent costs; not general CPU mapping | Only a later measured capability |
| Versioned cooperative buffers + staging | Extra transfers remain, but correctness is explicit | First implementation |
| Fine-grained concurrent access and range versions | More overlap, much larger dependency/coherence proof | Defer until serialized design is measured |

## Hardware Boundary

UMA/cache-coherent UMA are hardware/driver properties, not software modes we can
enable. [D3D12 architecture flags](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/ns-d3d12-d3d12_feature_data_architecture1)
are capability inputs, not proof of zero-copy benefit. Shared D3D12 heaps have
different constraints from OS sections, including restrictions on CPU-accessible
heap types. [Microsoft shared-heaps documentation](https://learn.microsoft.com/en-us/windows/win32/direct3d12/shared-heaps).

Mapping does not replace CPU/GPU synchronization. Persistent mappings still
require correct lifetime and ordering. [D3D12 Map documentation](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12resource-map).
An [OS file-mapping view](https://learn.microsoft.com/en-us/windows/win32/memory/creating-a-file-view)
provides process virtual-memory access, not automatic GPU residency.

## Consequences

The design can eliminate unnecessary CPU IPC payload serialization later and
reuse already-current replicas when separately authorized. It cannot manufacture
memory bandwidth, merge RAM/VRAM physically or promise lower game latency.
Exclusive leases and whole-buffer versioning may force more copies than a finer
range tracker, but avoid certifying untouched bytes incorrectly.

Revisit concurrency, dirty ranges, GPU-upload/custom heaps, Vulkan external memory
and cross-queue dependencies only after exact-content tests and measured workload
benefits. Keep original-copy behavior available; do not infer savings from an
architecture flag, successful hook or fence completion alone.
