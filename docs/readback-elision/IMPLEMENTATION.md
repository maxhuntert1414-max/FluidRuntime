# VRAM-to-RAM Readback Elision Implementation Plan

## Overview

Add an action-specific, bounded D3D11 readback-elision path and prove it with an
owned default-resource-to-readable-staging workload.

## Prerequisites

- FluidRuntime v0.9.0 control plane and sustained-copy evidence.
- Snapshot ABI v9, ring ABI v6, and control ABI v1.
- Existing whole-resource provenance, paired statistics, GPU timestamp queries,
  content hashes, and rollback validation.

## Phase Summary

1. Version resource-access and readback observation contracts.
2. Add the owned repeated readback workload and native self-validation.
3. Add the managed paired readback command and claim gates.
4. Run correctness/performance evidence and publish only the proven scope.

## Phase 1: ABI and Resource Classification

### Tasks

- [x] Add a dedicated readback policy action bit.
- [x] Change the negative matrix unknown action to a truly unknown bit.
- [x] Track D3D11 usage and CPU-access flags for owned resources.
- [x] Classify `DEFAULT -> STAGING + CPU_READ` whole-resource copies.
- [x] Add `MapRead` events and readback counters.
- [x] Bump and synchronize snapshot/ring ABI contracts.

### Success Criteria

Legacy workloads remain behavior-compatible. A readback policy cannot authorize
a GPU-internal copy, and the generic policy cannot silently authorize the new
readback lane.

## Phase 2: Owned Native Readback Workload

### Tasks

- [x] Add 4 MiB default source and CPU-readable staging destination buffers.
- [x] Run one required copy/map and 64 unchanged copy/map repetitions.
- [x] Compare all mapped bytes and retain first/final hashes.
- [x] Validate exact resource, event, snapshot, policy, and rollback counters.
- [x] Add a native baseline CTest.

### Success Criteria

Baseline forwards 65 readback copies. Optimized forwards one and skips 64. Both
perform 65 successful read maps and return identical bytes after detach.

## Phase 3: Managed Paired Measurement

### Tasks

- [x] Add `readback-elision-lab` options, command, report, and runner.
- [x] Publish only the dedicated readback action with an exact bounded budget.
- [x] Alternate baseline/optimized order and exclude warmups from statistics.
- [x] Require exact hashes, event/snapshot agreement, adapter identity, and
  rollback across every raw run.
- [x] Require CPU and GPU p50/p95 improvement plus 80 percent wins for any
  positive performance claim.

### Success Criteria

Ten measured hardware pairs pass all correctness gates. A positive claim is
allowed only for the owned default-to-readable-staging readback workload.

## Phase 4: Evidence and Release

### Tasks

- [x] Run managed and native Release/Debug suites.
- [x] Run the full negative policy matrix after the action-bit extension.
- [x] Capture WARP and RX 580 traces.
- [x] Review raw evidence and claim blockers.
- [x] Update Runtime and Gateway documentation.
- [ ] Push branch, verify CI, promote `main`, tag, release, and verify remote refs.

## Boundary

This feature does not prove physical VRAM placement, PCIe byte reduction,
external-game support, general readback caching, RAM/VRAM residency control, or
RAM-to-GPU upload optimization.
