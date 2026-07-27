# Upload Elision Implementation

## Objective

Add a third bounded manager action for trusted D3D11 whole-buffer uploads from
CPU-writable staging resources to default resources, then prove correctness and
performance in the owned target.

## Phase 1: Versioned Native Contract

- [x] Bump ring ABI to 8 and snapshot ABI to 11.
- [x] Add an upload-transfer event flag.
- [x] Add the third action, bit 4: `skip_redundant_upload_copy`.
- [x] Add upload and skipped-upload counters to the snapshot.
- [x] Keep action masks mutually exclusive and reject unknown action bit 8.

Verification:

- Existing generic and readback actions retain their own authorization scope.
- Upload classification requires trusted `STAGING + CPU_WRITE -> DEFAULT`.
- A write `Unmap` advances source generation before any later decision.

## Phase 2: Owned Native Workload

- [x] Add `--upload-copy-count` with a maximum of 128.
- [x] Create a 4 MiB CPU-writable staging source and default destination.
- [x] Map/write/unmap deterministic data once.
- [x] Forward one required copy, then issue 64 unchanged copies.
- [x] Verify source and destination bytes after detach.
- [x] Emit exact upload scope, hashes, counters, and timing in JSON.
- [x] Add a native CTest baseline.

Expected baseline:

- 65 upload copy calls, all forwarded;
- 1 write map and 1 write unmap;
- 0 skipped upload copies.

Expected optimized run:

- 65 upload copy calls;
- 1 forwarded upload and 64 skipped uploads;
- 268,435,456 logical bytes avoided;
- same final source and destination hash as baseline.

## Phase 3: Managed Lab And Gates

- [x] Publish action bit 4 with an exact budget of 64.
- [x] Add `upload-elision-lab` options, command, report, and runner.
- [x] Validate ring events, native snapshot, control acknowledgment, adapter,
  hashes, rollback, and paired execution order.
- [x] Require hardware GPU p50/p95 improvement plus 80 percent wins and keep
  every CPU submission pair inside a 1 ms / 10 percent overhead envelope.
- [x] Expose an active `ram-gpu-upload` manager lane while leaving physical
  residency blocked.

## Phase 4: Negative Matrix And CI

- [x] Move the unknown-action policy case to bit 8 after bit 4 ships.
- [x] Compile-time check action isolation and validate each action in its owned lab.
- [x] Run Release and Debug native suites.
- [x] Run all managed tests.
- [x] Run a short WARP paired lab locally and in CI with claim blockers asserted.

## Phase 5: Hardware Evidence And Release

- [x] Capture WARP functional evidence.
- [x] Capture RX 580 paired hardware evidence.
- [x] Publish a positive claim only after every scoped gate passes.
- [ ] Update architecture, status, roadmap, briefing, README, and FluidGateway.
- [ ] Merge, tag `v0.11.0`, create the GitHub release, and verify remote CI.

## Rollback

The policy is one epoch, one action mask, bounded to at most 128 skips, and
expires within four seconds. Detach waits for active hook calls, restores every
patched vtable slot, removes the shared mapping, and clears provenance.
