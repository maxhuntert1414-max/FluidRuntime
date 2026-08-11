# FluidRuntime

**A Windows research runtime for finding and safely removing redundant work
between CPU, GPU, RAM, VRAM, and the graphics pipeline.**

[![Version](https://img.shields.io/badge/version-0.18.0-ef6c35)](src/FluidRuntime/FluidRuntime.csproj)
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

## v0.18 Result

The owned update-upload lab now uses the existing FluidLink batch profile for
one seed and 128 exact duplicate candidates. The 64-candidate profile remains
available as an explicit regression case. The new default:

- returns 129 explicit operation decisions in one request/vector pair;
- spends the existing 128-action native ceiling without widening it;
- skips exactly 128 verified 4 MiB repeats, or 512 MiB of logical API work;
- keeps one owned resource, one 4 MiB cache, exact `memcmp`, expiration, and rollback;
- malformed, partial, rejected, stalled, or cumulatively slow responses fail
  closed to 134 forwarded calls and zero skips.

On the RX 580 gate, all 20 measured CPU and GPU pairs favored the optimized
native workload. Gateway authorization remains outside that timing window, so
the complete closed loop still blocks a performance claim.

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
- [v0.18 resilience and 128-action evidence](docs/evidence/v0.18.0-resilience-update-upload-128.md)
- [FluidLink v0.17 batch evidence](docs/evidence/v0.17.0-fluidlink-operation-batch.md)
- [D3D12 observation evidence](docs/evidence/v0.16.0-d3d12-observation.md)
- [Project handoff briefing](docs/BRIEFING-CLAUDE-CODE.md)

FluidRuntime is open source under the [MIT License](LICENSE).
