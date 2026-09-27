# Shared Arena ABI v1

Normative declarations: [fluid_memory_arena.h](../native/include/fluid_memory_arena.h).
Windows x64, little endian, C calling convention; no C++ classes or STL types cross
the boundary. All exports are exception-contained. Status codes 0-12 are explicit
in the header. Unknown ABI versions, adapter selectors and operation values fail.
Keep v1 layouts fixed; incompatible changes need a different negotiated version.

| Type | Size | Meaning |
| --- | --- | --- |
| `fma_options` | 24 | Four u32 fields (size/version/adapter/timeout), u64 budget |
| `fma_span` | 24 | u64 lease, local pointer, u64 byte length; never sent over IPC |
| `fma_peer` | 56 | u64 process handle/creation FILETIME, u32 PID/reserved=0, 32-byte SHA256 |
| `fma_reader_hello` | 16 | Four u32 fields: magic/version/opcode/capabilities |
| `fma_shared_read` | 64 | Four u32 fields followed by six u64 fields |
| `fma_metrics` | 128 | Eight u32 fields followed by twelve u64 counters |

`fma_create` requires exact options size, version 1, explicit WARP=0 or hardware=1,
100-10000 ms timeout and a budget in 1..67108864 bytes. Only one live arena per
loaded DLL instance is permitted; applications must not load duplicate instances
to claim a process-wide budget. Allocation and lease tokens are private registry
lookups, never pointers.
`BUSY` is retryable with caller backoff/deadline; `CLOSED` never grants a new action.
Metrics remain available for closed arenas. Destruction requires all leases ended
and known GPU progress. Counter exhaustion permanently rejects further tokens in
that DLL instance; wraparound and token recycling are forbidden.

## Owned Local Reader Protocol

This is **not FluidLink** and does not authorize GPU work. The laboratory transports
fixed descriptors over inherited anonymous pipes with an explicit handle allowlist.
No listener, socket, globally named mapping or JSON payload is involved. A production
external control plane must separately define authentication and capability routing.

1. Child sends exactly 16 bytes: magic `0x31414d46` (FMA1), version=1, opcode=0,
   capabilities=1 (read-only). Unknown versions/bits/opcodes are rejected.
2. Trusted parent supplies that hello plus a retained process handle, expected PID,
   creation FILETIME and image SHA256 to `fma_export_read`. The DLL independently
   validates the process identity and file. It acquires a whole-buffer host-read
   lease and duplicates only `FILE_MAP_READ`, non-inheritable, into that process.
3. Parent sends exactly the returned 64-byte descriptor. Fields at offsets 0/4/8/12
   are magic/version/opcode=1/size=64. The six u64 fields at offsets 16/24/32/40/48/56
   are arena/buffer/lease/remote mapping handle/bytes/content version.
4. Child maps read-only, consumes and exits. Parent calls `fma_end_read` only after
   the process exits. There is no peer-supplied completion fence or publication ACK.

Never treat a numeric handle as valid in a different process. Never forward grants.
Handles are not automatically revoked by closing a parent copy: ownership follows
the [Windows DuplicateHandle contract](https://learn.microsoft.com/en-us/windows/win32/api/handleapi/nf-handleapi-duplicatehandle).
The expected image hash must come from the trusted coordinator, not from the peer.

Golden hello (hex):

```text
46 4d 41 31 01 00 00 00 00 00 00 00 01 00 00 00
```

Golden descriptor with illustrative tokens 1/2/3, handle=4, bytes=16, version=5
(these values are not a live grant):

```text
46 4d 41 31 01 00 00 00 01 00 00 00 40 00 00 00
01 00 00 00 00 00 00 00 02 00 00 00 00 00 00 00
03 00 00 00 00 00 00 00 04 00 00 00 00 00 00 00
10 00 00 00 00 00 00 00 05 00 00 00 00 00 00 00
```

`memory_arena_lab.cpp` checks both byte vectors and rejected negotiation/identity;
`memory_arena_abi_c.c` independently compiles sizes/offsets as C and invokes the
real DLL. Fault tests compile the same production source into a private executable
to stall an owned WARP queue and exhaust tokens; no fault-switch exports ship.
