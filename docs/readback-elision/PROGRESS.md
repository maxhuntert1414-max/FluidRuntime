# VRAM-to-RAM Readback Elision Progress

## Status: Complete

## Quick Reference

- Research: [RESEARCH.md](RESEARCH.md)
- Implementation: [IMPLEMENTATION.md](IMPLEMENTATION.md)

## Phase Progress

### Phase 1: ABI and Resource Classification

**Status:** Complete

#### Tasks Completed

- Audited the v0.9.0 whole-resource provenance and managed policy path.
- Confirmed official D3D11 staging/read-map semantics.
- Selected an action-specific default-to-readable-staging contract.
- Added control action 2 for redundant readback copies.
- Added API-visible usage/access classification and fail-closed provenance checks.
- Added ring ABI 7 `MapRead` events and snapshot ABI 10 readback counters.
- Expanded the ABI 7 ring from 1,024 to 2,048 events after the 65-map baseline
  correctly exposed 18 overruns; the zero-loss gate remains mandatory.
- Kept action 1 unable to authorize the readback lane.

#### Decisions Made

- Start with readback before upload deduplication.
- Use API-visible usage classes rather than claiming physical memory placement.
- Require both CPU and GPU evidence for a positive readback-path claim.

#### Blockers

- None.

### Phase 2: Owned Native Readback Workload

**Status:** Complete

#### Tasks Completed

- Added the 4 MiB `DEFAULT` source and readable `STAGING` destination.
- Verified all 65 maps byte-for-byte with stable FNV-1a hashes.
- Passed the WARP baseline with 65 forwarded copies, 65 maps, and zero loss.
- Passed the managed optimized path with 64 exact skips and unchanged hashes.
- Added the seventh native CTest in both Release and Debug.

### Phase 3: Managed Paired Measurement

**Status:** Complete

#### Tasks Completed

- Added `readback-elision-lab` with a fixed action-2 budget of 64.
- Added alternating baseline/optimized processes and excluded warmups.
- Added exact native/event/hash/adapter/rollback validation for every raw run.
- Added CPU and GPU p50/p95 plus 80-percent-win claim gates.

### Phase 4: Evidence and Release

**Status:** Complete

#### Tasks Completed

- Passed 68/68 managed tests.
- Passed 7/7 native CTests in Release and Debug.
- Passed the 320/320 Release/Debug negative policy matrix.
- Captured WARP and RX 580 traces; all 26 raw readback runs passed.
- Passed the scoped RX 580 performance gate with 10/10 CPU and GPU wins.
- Updated Runtime and Gateway public documentation with the exact non-claims.
- Passed branch and `main` GitHub Actions, then promoted the verified commit to
  the v0.10.0 release.

## Session Log

### 2026-07-26

- Created `feature/v0.10-readback-elision` from published v0.9.0.
- Chose 4 MiB x 64 copy-plus-map repetitions.
- Defined fail-closed action, resource, timing, equivalence, and claim gates.
- Passed 64/64 managed tests after the ABI extension.
- Built the native Release targets and passed all 6 pre-existing CTests.
- Expanded the ring after a real zero-loss gate caught the original capacity.
- Proved 64 bounded readback-copy skips with 65 unchanged successful maps.
- Captured 22/22 valid RX 580 runs and 4/4 valid WARP runs.
- GitHub Actions `30230066738` passed the complete Windows evidence contract.

## Architectural Decisions

- Keep control ABI v1 if the existing action-mask field can express the new bit.
- Version snapshot/ring semantics for new counters and `MapRead` events.
- Preserve the v0.9.0 generic-copy and budget-one paths unchanged.
