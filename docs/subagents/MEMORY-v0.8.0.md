# STARK-PRODUCTIONS Memory Trace: v0.8.0

## Assignment

Preserve the release objective, architecture decisions, ABI contracts,
evidence, limitations, artifacts, and next step without changing global memory.

## Handoff

v0.8.0 is the first managed-to-native actuation slice. Baseline is observe-only;
the optimized owned target accepts one short-lived policy and may omit exactly
one already-proven redundant whole-resource copy. The hot path stays native.

ABI versions are snapshot 9, attach 2, ring 6, and control 1. The mapping is
82,048 bytes. Copy actuation is owned-lab only; CPU scheduling and RAM/VRAM
residency have no backend; presentation is observe-only.

WARP and RX 580 prove control, equivalence, event accounting, lifecycle, and
rollback. They do not prove a speedup. The next slice is the deterministic
negative policy matrix.

The memory agent was read-only and did not edit files or global Codex memory.
