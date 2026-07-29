# UpdateSubresource Elision Research Contract

## Question

Can software avoid a repeated CPU-memory-to-D3D11 upload without trusting a
source pointer, hash collision, physical memory assumption, or stale resource
generation?

## API Facts

`ID3D11DeviceContext::UpdateSubresource` copies application data into a
non-mappable resource. The application may modify or free the source memory
after the call returns because the runtime has already snapped the data. Under
destination contention, Microsoft documents a CPU copy into command-buffer
storage followed by an asynchronous GPU copy. Without contention, the chosen
path depends on the architecture.

Source:
[Microsoft UpdateSubresource documentation](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-updatesubresource).

Consequences:

- pointer reuse is not content identity;
- API-visible bytes are not proof of PCIe or physical VRAM traffic;
- exact byte inspection adds CPU work and must be measured;
- destination generation must participate in the decision;
- partial textures require pitch-aware canonicalization and remain out of scope.

## Chosen Slice

- Owned immediate-context D3D11 target only.
- Full 4 MiB default buffer, subresource zero, null box, zero pitches.
- One exact byte cache entry, capped at 4 MiB through attach ABI 3.
- Baseline and optimized processes perform the same comparisons.
- FNV-1a labels evidence; `memcmp` proves equality.
- Action 8 authorizes at most 64 skips and expires within four seconds.
- A one-bit mutation and an intervening external copy are mandatory guards.

## Rejected Shortcuts

- Same source pointer: memory may change in place.
- Hash-only equality: collision cannot prove exact content.
- Destination-only generation: cannot distinguish A from B.
- Unbounded cache: memory overhead would defeat the project objective.
- General external actuation: current write/synchronization coverage is not
  sufficient.

## Acceptance Gate

- Baseline: 67/67 direct uploads forwarded.
- Optimized: 3 forwarded, 64 skipped.
- One-bit A-to-B transition forwarded.
- B after external C write forwarded.
- Final destination bytes equal B exactly.
- Exact policy accounting, no ring loss, no provenance failures, rollback true.
- WARP functional evidence and RX 580 paired evidence.
- Positive claim requires 10 measured pairs, GPU p50/p95 improvement, at least
  80 percent GPU wins, and every CPU pair inside +1,000 us / +10 percent.
