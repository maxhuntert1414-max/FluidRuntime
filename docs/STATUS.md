# Project Status

Verified locally and remotely on 2026-07-26.

## Public Release

- FluidRuntime `main` / tag `v0.9.0`
- Release branch: `wip/v0.8.1-policy-matrix-checkpoint`
- Remote release validation:
  [GitHub Actions run 30214300519](https://github.com/maxhuntert1414-max/FluidRuntime/actions/runs/30214300519)
- Managed tests: 63/63 passed
- Native tests: 6/6 Release and 6/6 Debug passed
- Negative control-policy matrix: 320/320 owned WARP processes passed
- Sustained WARP trace: 22/22 processes passed; performance claim blocked as
  expected because WARP is a software adapter
- Sustained RX 580 trace: 22/22 processes passed; scoped GPU-workload evidence
  gate passed
- The v0.9.0 evidence contracts passed locally and on GitHub Actions

## New in v0.9.0

- Managed policy budgets are bounded from 1 through 128 without changing the
  ABI-v1 control-block layout.
- `sustained-copy-lab` creates an owned 4 MiB buffer workload and removes 128
  proven unchanged `CopyResource` repeats per optimized run.
- Each optimized run avoids 536,870,912 logical copy bytes while preserving
  exact FNV-1a readback hashes, IPC accounting, adapter identity, and rollback.
- The full rejected/expired/no-opt-in policy matrix is deterministic across
  Release and Debug.

## Claim Boundary

The positive evidence scope is exactly:

`owned-d3d11-sustained-copy-elision-gpu-workload-only`

On the RX 580, GPU workload p95 changed from 27,472.856 us to 356.784 us and all
10 measured pairs favored the optimized run. CPU p50 and p95 regressed slightly,
so v0.9.0 does not claim better end-to-end frame time, FPS, power, or game-wide
efficiency.

FluidRuntime still does not inject into external games, schedule OS threads,
control RAM/VRAM residency, actuate presentation, or support D3D12/Vulkan.

## Read This First

- Full handoff: [BRIEFING-CLAUDE-CODE.md](BRIEFING-CLAUDE-CODE.md)
- Architecture: [architecture.md](architecture.md)
- Roadmap: [roadmap.md](roadmap.md)
- v0.9.0 evidence:
  [evidence/v0.9.0-sustained-copy-elision.md](evidence/v0.9.0-sustained-copy-elision.md)
- Historical v0.8.1 checkpoint:
  [subagents/CHECKPOINT-v0.8.1.md](subagents/CHECKPOINT-v0.8.1.md)

## Operating Level Today

FluidRuntime can inspect process/GPU/memory telemetry, observe an owned D3D11
resource pipeline, publish a bounded managed policy, and interfere reversibly by
eliding copies whose provenance is proven inside the deterministic lab.

The next safe expansion is to harden provenance for aliases, shader writes,
fences, deferred contexts, and synchronization before any external attach or
RAM/VRAM/scheduler backend is promoted.
