# FluidRuntime

**A Windows research runtime for finding and safely removing redundant work
between CPU, GPU, RAM, VRAM, and the graphics pipeline.**

[![Version](https://img.shields.io/badge/version-0.19.0-ef6c35)](src/FluidRuntime/FluidRuntime.csproj)
[![CI](https://github.com/maxhuntert1414-max/FluidRuntime/actions/workflows/ci.yml/badge.svg)](https://github.com/maxhuntert1414-max/FluidRuntime/actions/workflows/ci.yml)
[![FluidLink](https://github.com/maxhuntert1414-max/FluidRuntime/actions/workflows/fluidlink.yml/badge.svg)](https://github.com/maxhuntert1414-max/FluidRuntime/actions/workflows/fluidlink.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-2f855a)](LICENSE)

FluidRuntime is the actuation companion to
[FluidGateway](https://github.com/maxhuntert1414-max/FluidGateway). Gateway
diagnoses probable waste and makes bounded decisions; Runtime proves whether an
action can be applied without changing the result.

## Current Status

| Area | State |
| --- | --- |
| FluidLink v2 | Strict binary IPC with numeric opcodes and no JSON payloads |
| FluidLink batch | 129 logical operations in one ordered request/vector pair |
| D3D11 | Reversible copy, readback, staging upload, and direct upload labs |
| D3D12 | Owned observation with queues, barriers, fences, content, and budgets |
| Vulkan | Planned, not implemented |
| External games | Unsupported; owned opt-in workloads only |

## v0.19 Result

The owned RX 580 gate now times the complete managed path: live Gateway
authorization, process startup, native policy, D3D11 work, evidence validation,
and fallback. Across 10 measured pairs plus one warmup:

- optimized end-to-end elapsed was lower in 10/10 pairs;
- paired delta p50/p95/p99 was `-376.363 / -356.112 / -352.855 ms`;
- concurrency 1/2/4/8 completed 128 measured authorizations with zero failures;
- x8 authorization p99 was `215.338 ms`, inside the declared 250 ms
  session-level budget;
- malformed, stalled, and slow peers published no policy and completed a clean
  baseline with 134 forwarded calls and zero skips.

The report therefore retains TCP loopback for current session-level control.
It does not establish TCP as a per-frame hot path.

This is measured protocol and functional evidence. It is not yet a claim of
lower game latency, higher FPS, reduced PCIe traffic, lower power, or physical
RAM/VRAM savings.

## How It Fits

```text
PresentMon + Windows telemetry
             |
        FluidGateway
      diagnosis + policy
             |
       FluidLink binary IPC
             |
        FluidRuntime
 proof + bounded owned action
             |
   native D3D11/D3D12 labs
```

## Verify Locally

Requirements: Windows, Python 3.11+, .NET 10 SDK, CMake, and an x64 C++
toolchain for native labs.

```powershell
dotnet test FluidRuntime.slnx -c Debug
dotnet build FluidRuntime.slnx -c Release

powershell -NoProfile -ExecutionPolicy Bypass `
  -File tools/Test-FluidLinkIntegration.ps1 `
  -GatewayPath ..\FluidGateway

powershell -NoProfile -ExecutionPolicy Bypass `
  -File tools/Test-GatewayManagedUpdateUpload.ps1 `
  -GatewayPath ..\FluidGateway `
  -CandidateActionCount 128 `
  -TrialPairs 2 -WarmupPairs 0 -Hardware $false
```

## Safety Boundary

FluidRuntime does not currently inject into third-party games, alter drivers,
schedule Windows threads, control physical residency, or promise unified-memory
behavior in software. Native intervention is limited to owned deterministic
targets, short-lived policies, exact equivalence checks, bounded action budgets,
and verified rollback.

## Documentation

- [Current status and release gate](docs/STATUS.md)
- [Architecture and trust boundaries](docs/architecture.md)
- [Roadmap](docs/roadmap.md)
- [v0.19 end-to-end authorization evidence](docs/evidence/v0.19.0-end-to-end-authorization.md)
- [v0.18 resilience and 128-action evidence](docs/evidence/v0.18.0-resilience-update-upload-128.md)
- [FluidLink v0.17 batch evidence](docs/evidence/v0.17.0-fluidlink-operation-batch.md)
- [D3D12 observation evidence](docs/evidence/v0.16.0-d3d12-observation.md)
- [Project handoff briefing](docs/BRIEFING-CLAUDE-CODE.md)

FluidRuntime is open source under the [MIT License](LICENSE).
