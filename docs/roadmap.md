# FluidRuntime Roadmap

The destination is an intelligent, evidence-driven runtime that reduces wasted
work and data movement across CPU, GPU, RAM, VRAM, graphics resources, and frame
presentation. Software cannot turn discrete PC hardware into physically unified
memory, but it can make earlier decisions, reuse data, avoid redundant work, and
coordinate the interfaces the operating system and graphics APIs expose.

## Delivered

- **v0.1:** consume FluidGateway ledgers and generate advisory runtime actions.
- **v0.2:** read-only native PID, process-memory, VRAM, and GPU-engine probe.
- **v0.3:** cooperative D3D11 Present and resource-copy observation with clean
  detach and a deterministic owned workload.
- **v0.4:** low-overhead shared-memory event streaming into the managed runtime,
  cross-language ABI validation, overrun detection, and exact native/managed
  agreement checks.
- **v0.5:** explicit owned-target copy elision limited to one redundant D3D11
  copy, two-process baseline comparison, post-detach buffer/texture readback,
  exact content comparison, stable content hashes, and fail-closed rollback
  checks.
- **v0.6:** paired baseline/optimized traces with alternating order, excluded
  warmups, CPU QPC and disjoint-guarded GPU timestamps, p50/p95 distributions,
  raw per-run evidence, bounded query timeouts, and explicit claim blockers.
- **v0.7.0:** cooperative owned-target resource retirement, bounded pointer-reuse
  history, monotonic resource IDs, provenance invalidation, IPC lifecycle events,
  and managed active/retired-state reconstruction.
- **v0.7.1:** opt-in automatic destruction observation through returned D3D11
  resource Release slots, dynamic-slot rollback before DLL unload, 64-cycle
  churn validation, concurrent detach stress, cooperative fallback, and a
  direction-aware GPU claim gate.
- **v0.7.2:** ABI-v4 subresource indices, per-mip generations,
  `CopySubresourceRegion` observation, unrelated-mip provenance preservation,
  exact regional repeat detection, mip readback, and WARP/AMD evidence.
- **v0.7.3:** ABI-v5 GPU-view write events, exact Texture2D mip resolution for
  `ClearRenderTargetView` and `ClearUnorderedAccessViewFloat`, conservative
  fallback for wider views, pre-attach resource exclusion, and WARP/AMD
  evidence with a blocked performance claim when p95 regressed.
- **v0.8.0:** ABI-v1 shared-memory control block, explicit owned-target opt-in,
  one short-lived managed policy epoch, native acknowledgment, atomic
  one-action budget, `ControlPolicyAccepted` evidence, baseline/optimized
  `manager-lab`, pinned-module observation-neutral stale-entry forwarding,
  fail-closed reattach rejection, and WARP/AMD traces with an honestly blocked
  performance claim.
- **v0.9.0:** deterministic 320-process negative policy matrix across WARP
  Release/Debug, bounded managed action budgets from 1 through 128, a 4 MiB
  sustained copy workload, exact content hashes and rollback gates, paired WARP
  and RX 580 traces, and a positive claim limited to the owned GPU copy workload.
- **v0.10.0:** API-visible `DEFAULT -> STAGING + CPU_READ` classification,
  dedicated readback policy action, ABI 7 `MapRead` evidence, snapshot ABI 10,
  2,048-event zero-loss ring, a 65-copy/65-map owned workload, exact per-map
  hashes, WARP/RX 580 traces, and a positive claim limited to the owned readback
  workload.

## Next Milestones

### v0.11: Upload Path and Synchronization Hardening

- Prototype the opposite API-visible direction: CPU-written staging/dynamic
  data into GPU-default resources, with exact upload provenance and no physical
  residency claims.
- Measure update, map/unmap, copy, fence/query, and synchronization costs before
  deciding whether reuse, batching, or elision is safe.
- Cover destruction through interface aliases and non-primary resource views.
- Cover draw/dispatch shader writes, remaining UAV/render-target/depth clears,
  fences, queries, and command-list synchronization.
- Replace the lab-specific repeated-generation heuristic with conservative
  provenance and synchronization rules.
- Add longer stress, race, and fault-injection tests.
- Derive bounded policies from live FluidGateway evidence instead of a fixed
  lab action, with explicit regression rollback.

### v0.12+: Controlled External Observation

- Define an explicit allowlist and operator consent model.
- Add an external attach prototype for unprotected software we are authorized
  to inspect.
- Refuse anti-cheat, protected, elevated, and identity-mismatched targets.
- Keep actuation disabled until observation and rollback gates pass.

### Later: Broader Runtime Management

- D3D12 command queues, heaps, barriers, copies, and residency telemetry.
- Vulkan queues, memory allocations, barriers, and presentation telemetry.
- CPU scheduling and frame-critical thread classification.
- RAM/VRAM residency, upload staging reuse, and memory-pressure policies.
- Closed-loop decisions fed by measured frame pacing, latency, power, and
  regression guards.

## Evidence Standard

FluidRuntime will not claim automatic FPS gains or support for old machines
from synthetic tests. A public performance claim requires reproducible traces,
before/after workload identity, visual or data equivalence, latency and pacing
percentiles, resource-use measurements, hardware details, and negative results.
