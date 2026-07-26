# Sustained Copy Elision Research

## Overview

Turn the existing one-action owned D3D11 copy-elision proof into a sustained,
bounded workload that can show a measurable optimization without weakening the
project's equivalence and rollback gates.

## Problem Statement

FluidRuntime v0.8.0 can skip one proven redundant 4 KiB `CopyResource`, but
process startup, manager handshake, resource creation, and query overhead
dominate that single operation. The correctness proof is real; the published RX
580 trace does not show a performance gain.

## User Stories / Use Cases

- As a contributor, I can run a paired baseline/optimized lab and see exactly
  how many GPU copy calls and logical bytes were removed.
- As a reviewer, I can reject the result if readback, hashes, event accounting,
  policy budget, or rollback differ.
- As the future manager, I can issue a bounded multi-action policy rather than
  making a managed round trip for every API call.

## Technical Research

### Approach Options

1. Repeat the existing 4 KiB copy.
   - Small code change.
   - Too little GPU work to measure reliably.
2. Add a large owned buffer pair and repeat an unchanged whole-resource copy.
   - Keeps the existing provenance rule.
   - Makes removed work dominate setup overhead.
   - Requires explicit memory and action bounds.
3. Add a new native optimization library immediately.
   - Could isolate policy logic later.
   - Adds an ABI and packaging surface before the behavior is proven.

### Recommended Approach

Use option 2 inside the existing owned target:

- 4 MiB source and destination buffers;
- one required initial copy;
- 128 unchanged repeats;
- optimized policy budget of 128;
- maximum policy budget fixed at 128;
- 512 MiB of logical GPU copy traffic avoided when all gates pass.

The sustained segment runs before the existing deterministic workload so the
budget is consumed only by the large-buffer repeats. The original six-copy
workload remains present and forwarded after the policy is exhausted.

### Required Technologies

- Existing D3D11 hook and provenance model.
- Existing shared-memory policy block and native atomics.
- Existing D3D11 timestamp/disjoint query path.
- Existing staging readback and FNV-1a hash helpers.

Microsoft documents `ID3D11DeviceContext::CopyResource` as an asynchronous GPU
whole-resource copy. Timestamp differences are reliable only inside a
`D3D11_QUERY_TIMESTAMP_DISJOINT` interval whose disjoint flag is false and whose
frequency is non-zero.

References:

- https://learn.microsoft.com/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-copyresource
- https://learn.microsoft.com/windows/win32/api/d3d11/ne-d3d11-d3d11_query

### Data Requirements

Each run must preserve:

- policy epoch, budget, acknowledgment, applied count, and final status;
- total/candidate/forwarded/skipped copy calls and bytes;
- CPU QPC and valid GPU timestamp duration;
- source/destination sustained-buffer hashes;
- IPC sequence loss and native overrun counts;
- detach and dispatch rollback status.

## Integration Points

- `native/src/present_hook.cpp`: bounded multi-action policy validation.
- `native/src/hook_target.cpp`: sustained resources, workload, report, and
  native self-validation.
- `src/FluidRuntime/Native/HookRingReader.cs`: bounded budget publication.
- New managed paired lab command/report/runner.
- Existing copy-elision and manager labs must remain behavior-compatible at
  budget one.

## Risks and Challenges

- A broad budget could authorize unrelated candidates. Mitigation: run the
  sustained segment first and exhaust the exact budget before the legacy
  workload.
- Policy expiration could occur before the batch completes. Mitigation: keep
  the batch bounded and retain the four-second maximum lifetime.
- Event-ring overflow could hide evidence. Mitigation: 128 repeats keep the
  total below the 1,024-event ring; any loss or overrun fails the run.
- WARP gain is not a hardware claim. Mitigation: publish WARP correctness and
  RX 580 hardware performance separately.
- The benchmark is synthetic. Mitigation: scope every claim to the owned
  sustained D3D11 copy workload.

## Open Questions

- Whether 128 x 4 MiB is enough for a stable RX 580 p95 improvement. The runner
  must measure before the release claim is decided.
- Whether a later release should move reusable policy logic into a standalone
  native library. This is deferred until the sustained path is proven.
