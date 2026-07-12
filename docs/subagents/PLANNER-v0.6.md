# STARK-PRODUCTIONS Planner Trace: v0.6

## Assignment

Plan a reliable evidence layer for cooperative copy elision using D3D11 GPU
timestamps and repeated paired trials without making unsupported performance
claims.

## Decisions

- Guard GPU timestamps with `TIMESTAMP_DISJOINT` and a bounded polling timeout.
- Preserve invalid GPU states explicitly instead of substituting zero.
- Record warmup pairs but exclude them from distributions.
- Alternate baseline/optimized execution order within measured pairs.
- Use paired deltas and publish p50, p95, mean, extrema, and direction counts.
- Preserve every native and managed run in the output trace.
- Block performance claims for fewer than ten measured pairs or any invalid GPU
  timing pair.
- Keep correctness, content equality, counters, IPC continuity, and rollback as
  hard gates independent of timing availability.

The planner was read-only and did not edit files directly.
