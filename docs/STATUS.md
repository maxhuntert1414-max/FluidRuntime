# Project Status

FluidRuntime v0.10.0 was verified locally and remotely on 2026-07-26.

## Public Release

- Branch/tag: `main` / `v0.10.0`
- Remote validation:
  [GitHub Actions run 30230066738](https://github.com/maxhuntert1414-max/FluidRuntime/actions/runs/30230066738)
- Managed tests: 68/68 passed
- Native tests: 7/7 Release and 7/7 Debug passed
- Negative control-policy matrix: 320/320 WARP processes passed
- WARP readback trace: 4/4 raw runs passed with the performance claim blocked
- RX 580 readback trace: 22/22 raw runs passed; scoped performance gate passed
- Release evidence and raw traces are committed with the tagged source.

## New In v0.10.0

- Snapshot ABI 10 and ring ABI 7 add `MapRead`, readback classification, and
  readback-specific counters.
- The event ring now holds 2,048 entries. The original 1,024-entry ring produced
  18 overruns under the new workload, and the zero-loss gate caught it.
- Control action 2 is dedicated to trusted D3D11
  `DEFAULT -> STAGING + CPU_READ` whole-resource repeats.
- `readback-elision-lab` performs one required and 64 unchanged 4 MiB
  copy/map cycles in separate baseline and optimized processes.
- Baselines forward all 65 readback copies. Optimized runs forward one, skip 64,
  and still execute and verify all 65 maps.
- Every optimized run avoids 268,435,456 logical copy bytes while preserving
  exact hashes, event/snapshot agreement, adapter identity, and rollback.
- The manager now exposes an active owned `vram-ram-readback` lane while keeping
  physical RAM/VRAM residency control blocked.

## Evidence Claim

The positive evidence scope is exactly:

`owned-d3d11-default-to-staging-readback-workload-only`

On the AMD Radeon RX 580 2048SP, all 10 measured pairs favored the optimized
run for CPU QPC duration and the guarded GPU timestamp interval:

| Metric | Baseline p50 | Optimized p50 | Baseline p95 | Optimized p95 |
| --- | ---: | ---: | ---: | ---: |
| CPU workload | 544,800.350 us | 421,077.150 us | 575,244.930 us | 442,802.645 us |
| GPU timestamp interval | 528,879.480 us | 406,132.160 us | 558,646.270 us | 427,418.994 us |

The GPU value is a timestamp interval around the workload, not a GPU-busy
hardware counter. This evidence does not prove physical VRAM placement, PCIe
bytes, FPS, power, external-game support, or general readback caching.

## Operating Level

FluidRuntime can inspect process/GPU/memory telemetry, observe an owned D3D11
resource pipeline, publish a bounded managed policy, and interfere reversibly
with two proven owned-lab copy patterns: generic unchanged `CopyResource` and
the new default-to-readable-staging readback path.

It still does not inject into external games, schedule OS threads, control
physical RAM/VRAM residency, actuate presentation, or support D3D12/Vulkan.

## Read Next

- [v0.10.0 evidence](evidence/v0.10.0-readback-elision.md)
- [Architecture](architecture.md)
- [Roadmap](roadmap.md)
- [Full handoff](BRIEFING-CLAUDE-CODE.md)
- [v0.9.0 evidence](evidence/v0.9.0-sustained-copy-elision.md)
