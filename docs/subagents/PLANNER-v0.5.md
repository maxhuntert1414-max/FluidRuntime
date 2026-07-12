# STARK-PRODUCTIONS Planner Trace: v0.5

## Assignment

Design the smallest safe first intervention for the owned D3D11 hook lab. The
scope required explicit opt-in, redundant-copy elision, baseline comparison,
content equivalence, rollback, and no external injection.

## Decisions

- Limit actuation to one skipped redundant `CopyResource`, not all candidates.
- Preserve observe-only behavior in `FluidHookAttach`; expose actuation only
  through versioned `FluidHookAttachEx` options.
- Run baseline and optimized workloads in separate processes.
- Count observed, forwarded, and skipped copies independently.
- Emit both redundant-candidate and skipped flags through IPC.
- Perform staging readback only after detach so validation copies do not pollute
  hook telemetry.
- Compare logical bytes exactly, hash them with stable FNV-1a for reporting, and
  ignore texture row padding.
- Treat timing as transparent experimental output, not benchmark evidence.

## Risks Preserved For Later

- Resource destruction, aliasing, shader/UAV writes, subresources, and fences
  are not modeled yet.
- The optimization remains invalid for external processes.
- Repeated-run statistics and GPU timestamps are still required before a
  performance claim.

The planner was read-only and did not edit files directly.
