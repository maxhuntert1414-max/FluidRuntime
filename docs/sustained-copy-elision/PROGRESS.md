# Sustained Copy Elision Progress

## Status: Complete

## Quick Reference

- Research: [RESEARCH.md](RESEARCH.md)
- Implementation plan: [IMPLEMENTATION.md](IMPLEMENTATION.md)
- Evidence: [../evidence/v0.9.0-sustained-copy-elision.md](../evidence/v0.9.0-sustained-copy-elision.md)

## Phase Progress

### Phase 1: Negative Policy Matrix

**Status:** Complete

- Wired `control-policy-matrix` into the CLI and help.
- Covered eight policy cases in WARP Release and Debug.
- Ran 20 repetitions per case/configuration: 320/320 processes passed.
- Proved deterministic normalized evidence without sleep-based expiry gates.

### Phase 2: Sustained Native Workload

**Status:** Complete

- Added owned 4 MiB source/destination buffers.
- Added one required copy followed by up to 128 unchanged repeats.
- Expanded the existing control budget semantics to a bounded 1..128 range.
- Preserved budget-one compatibility and the ABI-v1 control-block layout.
- Added exact snapshot, event, readback hash, content, and rollback validation.

### Phase 3: Paired Measurement

**Status:** Complete

- Added `sustained-copy-lab` with alternating baseline/optimized order.
- Preserved warmups and raw runs while excluding warmups from statistics.
- Required adapter identity and valid disjoint-guarded D3D11 GPU timestamps.
- Added p50/p95, paired deltas, win rate, and explicit claim blockers.
- Captured 10 measured pairs plus one warmup pair on WARP and RX 580.

### Phase 4: Release

**Status:** Complete

- Managed tests: 63/63 passed.
- Native tests: 6/6 Release and 6/6 Debug passed.
- WARP and RX 580 correctness gates passed with no event loss or overrun.
- Full policy matrix passed 320/320.
- Runtime and Gateway documentation updated.
- Release branch CI passed in
  [run 30214300519](https://github.com/maxhuntert1414-max/FluidRuntime/actions/runs/30214300519).
- `main` and tag `v0.9.0` published from the verified release branch.

## Result

Each optimized default run skips exactly 128 redundant 4 MiB copies, avoids
536,870,912 logical GPU copy bytes, forwards the remaining seven legacy/required
copies, and preserves exact data after detach.

On the RX 580, the scoped GPU-workload gate passed with 10/10 optimized wins and
GPU p95 falling from 27,472.856 us to 356.784 us. CPU p50/p95 regressed slightly,
so the evidence does not establish an end-to-end, frame-time, FPS, power, or game
performance improvement.

## Architectural Decisions

- Keep the small existing ABI instead of extracting a speculative library.
- Bound every policy action and exhaust the sustained budget before the legacy
  deterministic workload.
- Separate correctness, GPU-workload performance, and CPU caveats in the report.
- Keep external injection, scheduler, RAM/VRAM, and presentation actuation
  disabled until their own evidence and rollback contracts exist.
