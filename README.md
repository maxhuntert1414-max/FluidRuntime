# FluidRuntime

**A Windows research runtime for finding and safely removing redundant work
between CPU, GPU, RAM, VRAM, and the graphics pipeline.**

[![Version](https://img.shields.io/badge/version-0.17.0-ef6c35)](src/FluidRuntime/FluidRuntime.csproj)
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
| FluidLink batch | 65 logical operations in one ordered request/vector pair |
| D3D11 | Reversible copy, readback, staging upload, and direct upload labs |
| D3D12 | Owned observation with queues, barriers, fences, content, and budgets |
| Vulkan | Planned, not implemented |
| External games | Unsupported; owned opt-in workloads only |

## v0.17 Result

The optional FluidLink batch profile preserves the original v2 contract and
adds an exact second profile for homogeneous operation groups. In the controlled
Gateway authorization path:

- 65 operation decisions use one request and one explicit decision vector;
- complete authorization falls from 74 to 10 loopback round trips;
- malformed, partial, rejected, stalled, or cumulatively slow responses fail
  closed before native policy publication;
- the owned D3D11 workload still validates exact final content, event accounting,
  process identity, policy bounds, and rollback.

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
- [FluidLink v0.17 batch evidence](docs/evidence/v0.17.0-fluidlink-operation-batch.md)
- [D3D12 observation evidence](docs/evidence/v0.16.0-d3d12-observation.md)
- [Project handoff briefing](docs/BRIEFING-CLAUDE-CODE.md)

FluidRuntime is open source under the [MIT License](LICENSE).
