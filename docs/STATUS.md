# Project Status

FluidRuntime v0.11.0 was verified locally and remotely on 2026-07-26.

## Public Release

- Branch/tag: `main` / `v0.11.0`
- Runtime main validation:
  [GitHub Actions run 30232835526](https://github.com/maxhuntert1414-max/FluidRuntime/actions/runs/30232835526)
- Feature validation:
  [GitHub Actions run 30232683504](https://github.com/maxhuntert1414-max/FluidRuntime/actions/runs/30232683504)
- FluidGateway documentation: commit `5725f4f`,
  [GitHub Actions run 30233019441](https://github.com/maxhuntert1414-max/FluidGateway/actions/runs/30233019441)
- Managed tests: 73/73 passed.
- Native tests: 8/8 Release and 8/8 Debug passed.
- Negative control-policy matrix: 320/320 WARP processes passed.
- WARP upload trace: 4/4 raw runs passed with the performance claim blocked.
- RX 580 upload trace: 22/22 raw runs passed; scoped performance gate passed.
- Legacy generic, sustained, and readback optimized smokes passed after the
  shared hot-path change.
- Release evidence and raw traces are committed with the tagged source.

## New In v0.11.0

- Snapshot ABI 11 and ring ABI 8 add upload classification, directional
  map/unmap flags, and upload-specific counters.
- Control action bit 4 is dedicated to trusted D3D11
  `STAGING + CPU_WRITE -> DEFAULT` whole-resource repeats.
- `upload-elision-lab` writes a 4 MiB staging source once, forwards one required
  upload, and issues 64 unchanged repeats in separate baseline and optimized
  processes.
- Baselines forward all 65 uploads. Optimized runs forward one and skip exactly
  64, avoiding 268,435,456 logical copy bytes.
- Expected, staging-source, and default-destination hashes agree after detach.
- Skip reservation is lock-free and budgeted; provenance proof and reservation
  are linearized under the resource lock. Skips do not advance content
  generation because no bytes changed.
- The manager exposes `ram-gpu-upload` as active only in the owned lab while
  physical `ram-vram-residency` remains blocked.

## Evidence Claim

The positive evidence scope is exactly:

`owned-d3d11-writable-staging-to-default-upload-copy-workload-only`

Claim basis:

`gpu-interval-improvement-with-bounded-cpu-submission-overhead`

AMD Radeon RX 580 2048SP:

| Metric | Baseline p50 | Optimized p50 | Baseline p95 | Optimized p95 |
| --- | ---: | ---: | ---: | ---: |
| CPU submission QPC | 11,989.250 us | 11,987.300 us | 13,397.080 us | 12,391.395 us |
| GPU timestamp interval | 31,883.320 us | 1,669.600 us | 32,520.304 us | 1,734.448 us |

GPU won 10/10 pairs; paired p50/p95 deltas were -94.814% and -94.445%.
CPU won 6/10 pairs; all 10 stayed inside the predeclared +1,000 us / +10%
submission-overhead envelope. CPU paired delta p95 was +377.070 us (+3.198%),
so this release does not claim CPU acceleration.

The GPU value is a guarded timestamp interval around the owned workload, not a
GPU-busy hardware counter. This evidence does not prove physical RAM/VRAM
placement, PCIe bytes, FPS, power, external-game support, or general upload
caching.

## Operating Level

FluidRuntime can inspect process/GPU/memory telemetry, observe an owned D3D11
resource pipeline, publish a bounded managed policy, and interfere reversibly
with three proven owned-lab copy patterns: generic unchanged `CopyResource`,
default-to-readable-staging readback, and writable-staging-to-default upload.

It still does not inject into external games, schedule OS threads, control
physical RAM/VRAM residency, actuate presentation, or support D3D12/Vulkan.

## Read Next

- [v0.11.0 evidence](evidence/v0.11.0-upload-elision.md)
- [Architecture](architecture.md)
- [Roadmap](roadmap.md)
- [Full handoff](BRIEFING-CLAUDE-CODE.md)
- [v0.10.0 evidence](evidence/v0.10.0-readback-elision.md)
