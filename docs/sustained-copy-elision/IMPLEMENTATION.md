# Sustained Copy Elision Implementation Plan

## Overview

Add a bounded multi-action managed policy and a large owned copy workload that
demonstrates removed GPU work while preserving exact equivalence.

## Phase Summary

1. Finish and verify the negative control-policy matrix.
2. Add the bounded sustained native workload and policy budget.
3. Add paired managed measurement and claim gates.
4. Capture evidence, review, document, and publish.

## Phase 1: Negative Policy Matrix

- [x] Wire `control-policy-matrix` into the CLI.
- [x] Align report schema versions and tests.
- [x] Add option and raw-policy unit coverage.
- [x] Run 8 cases x 20 repetitions x Release/Debug WARP.

Success result: all 320 processes passed with deterministic normalized evidence,
zero action in negative cases, and exact content/rollback.

## Phase 2: Sustained Native Workload

- [x] Add 4 MiB owned source/destination buffers.
- [x] Run one required copy and 128 unchanged repeats.
- [x] Accept managed action budgets from 1 through 128.
- [x] Preserve budget-one compatibility and ABI layout.
- [x] Validate exact native counters and sustained-buffer readback hashes.

Success result: baseline forwards all sustained copies. The default optimized
run skips exactly 128 repeats, avoids exactly 512 MiB logically, and preserves
all bytes after detach.

## Phase 3: Paired Measurement

- [x] Add `sustained-copy-lab` CLI and structured report.
- [x] Alternate baseline/optimized order.
- [x] Exclude warmups and retain raw runs.
- [x] Require valid GPU timing and direction-aware p50/p95/win-rate gates.
- [x] Record CPU regression independently from the GPU-only claim.

Success result: 10 measured hardware pairs passed every correctness gate, all
GPU timings were valid, GPU p50/p95 improved, and 10/10 pairs favored optimized.

## Phase 4: Release

- [x] Run managed and native Release/Debug suites.
- [x] Capture WARP and RX 580 traces.
- [x] Review code and evidence.
- [x] Update Runtime and Gateway docs.
- [x] Push the branch and verify remote CI.
- [x] Fast-forward `main`, tag `v0.9.0`, and verify the published release.

## Boundary

The public claim remains limited to the owned sustained D3D11 GPU copy
workload. This phase does not authorize external injection, general game
optimization, CPU scheduling, RAM/VRAM residency control, or presentation
actuation.
