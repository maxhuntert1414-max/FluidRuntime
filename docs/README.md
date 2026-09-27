# FluidRuntime Documentation

Start with the workflow you need. Monitoring, observation and cooperative GPU
actuation are separate capabilities; enabling one does not enable the others.

## Use and Evaluate

| Goal | Guide |
| --- | --- |
| Monitor a process without changing it | [Daily terminal and portable package](daily-use.md) |
| Observe an explicitly launched Vulkan app | [Application sessions](application-sessions.md) |
| Understand verified scope and release status | [Project status](STATUS.md) |
| Understand the design and next steps | [Architecture](architecture.md), [roadmap](roadmap.md) |

## Gateway and Graphics

| Area | Contract and reproduction |
| --- | --- |
| Isolated native Gateway server | [Setup, identity and validation](native-gateway.md) |
| In-process Gateway DLL | [Shared core, C ABI and backend comparison](inprocess-gateway.md) |
| FluidLink and owned D3D11 readback | [v0.5 contract and evidence](fluidlink-v0.5-readback.md) |
| Owned D3D11 upload elision | [Implementation](update-upload-elision/IMPLEMENTATION.md) |
| Owned D3D12 transfers | [Generalized transfer core and evidence](evidence/v0.21.0-d3d12-transfer-core.md) |
| Cooperative Vulkan execution | [Native backend](vulkan-native.md) |
| External Vulkan observation | [Command tracking](vulkan-command-observation.md), [completion tracking](vulkan-completion-observation.md) |

## Cooperative Memory

1. [Shared arena](shared-arena.md): opt-in D3D12 DLL, build and lifecycle.
2. [C ABI and read-descriptor protocol](shared-arena-abi.md): handles, layouts and errors.
3. [Coherence foundation](shared-memory-foundation.md): versions, aliases and GPU tickets.
4. [Design decision](architecture/shared-memory-v1.md): why cooperative ownership comes first.
5. [Arena evidence](evidence/shared-arena.md): allocation counts, timings, faults and limitations.

The arena is an experimental native library, not a daily-monitor feature or a
transparent RAM/VRAM pool for arbitrary applications. All GPU transfers remain.

## Contribute and Reproduce

[Contributing](../CONTRIBUTING.md) covers prerequisites, formatting, the code map
and test gates. Evidence pages link to frozen JSON traces and describe their
measurement windows. Older milestone results remain available under
[Project status](STATUS.md); they are not current compatibility guarantees.

Bring a reproducible failure or workload, the relevant version and hardware,
and both positive and negative measurements. Please keep diagnostic traces free
of secrets and unrelated personal data before posting them publicly.
