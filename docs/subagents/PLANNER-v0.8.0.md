# STARK-PRODUCTIONS Planner Trace: v0.8.0

## Assignment

Preserve the CPU/GPU/RAM/VRAM ambition while decomposing the next work into
verifiable, fail-closed slices.

## Decisions

- Keep v0.8.0 scoped to one owned D3D11 action and a correctness claim.
- Choose v0.8.1 as a native control-policy negative matrix.
- Follow with concurrency/fault injection, canonical resource identity and
  aliases, conservative write/synchronization coverage, FluidGateway shadow
  policy, and a sustained owned workload.
- Keep external observation read-only until allowlist, consent, executable
  identity, privilege, and protected-process refusal gates exist.
- Advance CPU, RAM, VRAM, D3D12, and Vulkan independently through telemetry,
  advisory decisions, reversible cooperative action, then closed loop.

## Next Acceptance Gate

The v0.8.1 matrix must prove valid, rejected, expired, and no-opt-in policies in
Release and Debug WARP runs with no sleeps used as the expiry mechanism.

The planner was read-only and did not edit files.
