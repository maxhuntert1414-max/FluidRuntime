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

## Next Milestones

### v0.5: First Measured Intervention

- Add an explicit opt-in mode only to the owned D3D11 hook target.
- Skip one proven redundant `CopyResource` operation.
- Verify destination equivalence and fail closed when state is uncertain.
- Measure copy count, bytes, CPU cost, GPU timing, pacing, and rollback against
  an untouched baseline.

### v0.6: Trustworthy Resource State

- Track resource destruction and pointer reuse.
- Cover subresources, `CopySubresourceRegion`, shader/UAV writes, and fences.
- Replace the lab-specific repeated-generation heuristic with conservative
  provenance and synchronization rules.
- Add longer stress, race, and fault-injection tests.

### v0.7: Controlled External Observation

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
