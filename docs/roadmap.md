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
- **v0.11.0:** API-visible `STAGING + CPU_WRITE -> DEFAULT` classification,
  dedicated upload action bit 4, ring ABI 8 and snapshot ABI 11, a 65-copy
  owned upload workload, exact post-detach hashes, lock-free bounded action
  reservation, WARP/RX 580 traces, and a positive GPU-interval claim with a
  bounded CPU submission-overhead envelope.
- **v0.12.0:** exact-content full-buffer `UpdateSubresource` classification,
  dedicated action bit 8, attach-options ABI 3, ring ABI 9, snapshot ABI 12,
  one-resource/4 MiB cache bounds, one-bit content mutation and external-write
  generation guards, 67-update paired workload, WARP/RX 580 traces, and a
  positive scoped CPU/GPU interval claim.
- **v0.13.0:** FluidLink 0.1.0 cross-repository transport with a fixed 56-byte
  binary header, one-byte message/event/decision opcodes, exact contract and
  capability negotiation, bounded strict JSON payloads, serialized correlated
  .NET round trips, legacy JSONL isolation, Python/.NET interoperability CI,
  and a reproducible envelope-size report.
- **v0.14.0:** FluidLink 0.2.0 and wire v2 with opcode-specific positional
  binary payloads, capability and presence bitmasks, integer microsecond/byte
  units, strict bounded UTF-8, 17 shared Python/.NET full-frame vectors,
  fail-closed typed clients with recoverable/fatal peer-error separation, and a
  measured same-flow reduction from 3,189 to 1,880 frame bytes while retaining
  v1 compatibility.
- **v0.15.0:** first live FluidGateway-to-native closed loop for one owned
  D3D11 path. Sixty-four exact FluidLink v2 duplicate-upload decisions authorize
  a short-lived action-8 budget, while destination generation and full-content
  comparison remain the native final gate. The exact loopback tuple is bound to
  the expected Gateway PID/executable through Windows, target/hook binaries are
  frozen and revalidated, and a context SHA-256 binds authorization to that
  evidence. Malformed, stalled, and cumulatively slow peers fail under one total
  deadline. WARP and RX 580 evidence are recorded. Performance remains blocked
  because Gateway authorization is outside the native timing window; process
  binding is not cryptographic peer authentication.

## Next Milestones

### v0.16: Low-Latency Decisions and Upload Generalization

- Replace 74 serial pre-authorization round trips with a bounded batch decision
  packet or another measured transport shape before considering per-frame use.
- Include authorization and fallback in an end-to-end latency benchmark; retain
  the current performance blocker until the complete loop passes.
- Add backpressure, cancellation, peer-restart, stale-session, and partial-batch
  tests before any shared-memory FluidLink proposal can become active.

- Extend upload evidence to textures, pitch-aware data, partial boxes,
  `UpdateSubresource1`, dynamic-buffer, reuse, and batching patterns without an
  unbounded hot-path content cache.
- Measure map/unmap, copy, fence/query, and synchronization costs before
  authorizing any new action.
- Cover destruction through interface aliases and non-primary resource views.
- Cover draw/dispatch shader writes, remaining UAV/render-target/depth clears,
  fences, queries, and command-list synchronization.
- Replace the lab-specific repeated-generation heuristic with conservative
  provenance and synchronization rules.
- Add longer stress, race, and fault-injection tests.
- Generalize live FluidGateway authorization only after each new native pattern
  has equivalence, provenance, budget, expiration, and rollback evidence.

### v0.17: Owned D3D12 Backend

- Add an opt-in owned-app observation layer for devices, command queues,
  resources/heaps, map/unmap, copy commands, resource barriers, queue submits,
  fences, and residency signals.
- Build a D3D12-specific provenance and synchronization model. Do not reuse
  D3D11 generation assumptions where explicit states, queues, and fences differ.
- Keep actuation disabled until an owned deterministic workload proves final
  content, resource-state correctness, queue ordering, budget, and rollback.

### v0.18: Owned Vulkan Backend

- Add an explicit opt-in Vulkan layer for the owned lab, observing allocations,
  memory binding, buffers/images, copy commands, barriers, queue submit/present,
  semaphores, fences, and available memory-budget telemetry.
- Model layouts, queue-family ownership, suballocation lifetime, and explicit
  synchronization independently from D3D11/D3D12.
- Promote one bounded action only after deterministic equivalence, validation-
  layer cleanliness, fault controls, timing, and complete layer removal pass.

### v0.19+: Controlled External Observation

- Define an explicit allowlist and operator consent model.
- Add an external attach prototype for unprotected software we are authorized
  to inspect.
- Refuse anti-cheat, protected, elevated, and identity-mismatched targets.
- Keep actuation disabled until observation and rollback gates pass.

### Later: Broader Runtime Management

- CPU scheduling and frame-critical thread classification.
- RAM/VRAM residency, upload staging reuse, and memory-pressure policies.
- Closed-loop decisions fed by measured frame pacing, latency, power, and
  regression guards.
- Delta-encoded state only after repeated snapshot payloads exist and exact
  baseline/resynchronization tests demonstrate useful compression.
- A generic shared-memory FluidLink transport only after a sustained TCP
  benchmark and an explicit record, atomic, backpressure, ACL, crash-recovery,
  fallback, and stress-test contract.

## Evidence Standard

FluidRuntime will not claim automatic FPS gains or support for old machines
from synthetic tests. A public performance claim requires reproducible traces,
before/after workload identity, visual or data equivalence, latency and pacing
percentiles, resource-use measurements, hardware details, and negative results.
