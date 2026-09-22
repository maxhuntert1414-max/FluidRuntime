# Shared Memory Foundation

Status: foundation for a **cooperative D3D12 buffer arena**, not unified memory
enabled in games. The daily monitor, Gateway backends and FluidLink contracts
are unchanged. No new actuation authority or global hooks are introduced.

## Implemented

- `fluidruntime-memory-core`: a reusable C++20 interface target with bounded,
  mutex-protected allocation/version/access bookkeeping. It owns no GPU objects,
  payload copies, sockets or mapped memory. This is internal C++, not a C ABI.
- Generation-bound allocation IDs and subrange views. All aliases of the same
  allocation share one version and exclusive access state. No unbounded history.
- Explicit host/device read/write, full upload and full readback admissions.
  CPU writes invalidate the device replica; GPU writes invalidate the host.
- GPU completion is bound to the owning queue and reserved fence value. Incomplete,
  wrong-queue, future, stale and replayed receipts do not publish new content.
- Only one GPU ticket may be outstanding across the session; host access to
  independent allocations remains possible. Fence reservation alone cannot prove
  submission order across threads.
- Unknown GPU progress or device removal quarantines the allocation and closes the
  session. Counter exhaustion also closes admissions. Neither timeout nor
  reconnection frees GPU work.
- `fluidruntime-memory-readiness`: a real Windows/D3D12 laboratory consuming this
  core, with a read-only cross-process mapping and four exact-content roundtrips.

```powershell
cmake -S native -B native/build -A x64
cmake --build native/build --config Release
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Test-MemoryFoundation.ps1 `
  -NativeDirectory native/build/Release -Hardware
```

Without `-Hardware`, the helper explicitly uses WARP. A hardware request never
silently falls back to WARP. Results go to `artifacts/memory-foundation.json` by
default; use `-OutputPath` to preserve separate captures. The helper launches only
owned test executables. It does not attach to your applications or change Windows.

## What the Lab Proves

The broker creates a 16-KiB unnamed Windows section. Only a read-only duplicate
is inherited by an owned child through an explicit handle allowlist. The child
checks every byte and verifies that a writable view is denied. The parent holds
an exclusive read lease until the child exits. The section contains payload,
never authority metadata. Shared access in this step avoids a serialized payload
message; it is not a general cross-process arena implementation yet.

D3D12 then uses normal UPLOAD/DEFAULT/READBACK resources. All required copies,
barriers and fences remain. Cases include an initial upload, partial alias write,
GPU-side content replacement and an aborted CPU write followed by full recovery.
Each complete readback is compared byte-for-byte, under a read lease, to an
independent expected buffer. Logical copy bytes are reported, not physical PCIe
traffic. No copy is omitted in this milestone.

Architecture flags and budget snapshots are reported for the chosen adapter.
Unavailable GPU-upload support is `null`, not a guessed capability. Availability
is not a performance recommendation. D3D12 debug-layer errors fail the lab when
the layer is installed; its availability is itself reported.

## Core Contract

`Coherence<>` defaults to 64 records, up to 64 MiB per logical allocation; it
retains no payload. That is a metadata bound, **not** a physical arena budget.
`Coherence<capacity, serial_limit>` permits smaller deterministic test limits.
The first backend must separately cap its total physical allocation size.

| Operation | Required input state | Result after successful completion |
| --- | --- | --- |
| Host read | Current host replica; no pending access | No version change |
| Host full write | No pending access | New host version; device stale |
| Host partial write | Current host replica | Same, including every alias |
| Device read | Current device replica | No version change after fence |
| Device full/partial write | Partial requires current device replica | New device version; host stale |
| Upload | Current host, whole-allocation view | Device shares host content version after fence |
| Readback | Current device, whole-allocation view | Host current after fence **and backend CPU publication** |

An admission is an ownership ticket, **not a Gateway policy authorization**.
The backend must perform the requested work and supply trusted driver evidence.
Do not use completion values, versions or tickets taken from writable IPC as proof.
Inspection is advisory; it does not lock payload. All actual accesses need a lease.

Pointers, resource states, memory visibility and actual writes remain the owning
backend's responsibility. Fence completion alone does not publish host bytes:
the lab waits, maps readback, copies into the host replica, then calls `finish`.
Calling `finish` before completing that work violates the contract.

Full writes can recover unknown content; partial writes cannot. CPU abort clears
content validity. GPU abort closes the session and keeps the allocation quarantined
even if a later receipt arrives. `release` rejects active/quarantined records and
closed sessions. `close` revokes
admissions but frees no physical allocation; backend teardown must handle pending
work. This lab terminates only its own process if GPU completion becomes unknown,
avoiding normal stack cleanup that could recycle resources still in use.

Session identities must be fresh per broker lifetime. Fixed `1` is used only in
the isolated, one-shot laboratory. Allocation generations and operation serials
never wrap or reset within a session. The numeric `Access` values are private
implementation constants, not newly allocated FluidLink opcodes.

## Next Implementation Contract

The first shared arena will support **registered cooperative linear buffers on
one D3D12 device/queue**, with an explicit 64-MiB total arena cap and 64 live slots.
Vulkan, D3D11, textures, multiple adapters/queues, sparse residency, automatic
third-party capture and transparent page-fault migration are not v1 features.

1. Keep allocations, versions, bounds and ownership in trusted Runtime memory.
   Shared pages carry payload only; descriptors use allocation ID/generation,
   offset and length, never process-local pointers.
2. Establish the peer's process identity through the existing verified control
   path before duplicating unnamed handles with minimum rights. No PID-derived
   globally writable object names or broad handle inheritance.
3. Give cooperative writers explicit acquire/publish ownership. Retained writable
   peer mappings cannot be made immutable by a sequence number. Zero-copy reads
   require an enforceable lifetime/ownership contract; untrusted or untracked
   writers must not provide evidence for copy elision.
4. On discrete GPUs, copy a published host range into Runtime-owned staging and
   track the device replica. Keep that staging allocation immutable until its
   fence completes. RAM section pages are not automatically D3D12 GPU heaps.
5. Permit reuse only with current content, exclusive access and the existing
   Gateway policy/budget/expiry checks. A new negotiated capability and normative
   numeric-opcode schema/golden vectors must precede external control messages.
   Do not silently reinterpret existing FluidLink v2 operations.
6. Expose an exception-contained C ABI with opaque broker/lease handles and
   fixed-width descriptors for C#/C++ clients. Borrowed spans expire when their
   lease ends. No STL or `std::mutex` is shared across processes or the ABI.
7. Close admissions on peer disconnect, device loss, bounds violation or deadline.
   Never recycle an allocation with unknown GPU progress. Preserve isolated-server
   mode and an original-copy fallback when optimization authority is unavailable.
8. Validate actual producer/consumer processes, malicious/stale descriptors,
   interrupted writes, exact GPU contents and bounded cleanup. Benchmark staging
   bytes, GPU copy bytes, CPU time, allocations and p50/p95/p99 independently.

These prerequisites let implementation begin for the **cooperative prototype**.
They do not certify production shared-memory optimization or arbitrary-game support.
See [architecture decision](architecture/shared-memory-v1.md) and
[measured evidence](evidence/memory-foundation.md).
