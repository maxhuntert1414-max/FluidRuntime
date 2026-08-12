# FluidRuntime

**A Windows research runtime for finding and safely removing redundant work
between CPU, GPU, RAM, VRAM, and the graphics pipeline.**

[![Version](https://img.shields.io/badge/version-0.20.0-ef6c35)](src/FluidRuntime/FluidRuntime.csproj)
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
| D3D12 | Gateway-authorized, bounded `CopyBufferRegion` elision in an owned COPY queue |
| Vulkan | Planned, not implemented |
| External games | Unsupported; owned opt-in workloads only |

## v0.20 Result

FluidRuntime now has a separate native D3D12 hook and owned COPY-queue target.
Across 10 measured RX 580 pairs plus one warmup:

- each optimized run omitted 128 exact redundant 4 MiB
  `CopyBufferRegion` calls, or 536,870,912 logical API bytes;
- managed end-to-end delta p50/p95/p99 was
  `-23.552 / -10.989 / -7.843 ms`, with 10/10 optimized wins;
- submit-to-fence delta p50/p95/p99 was
  `-48.442 / -46.139 / -44.853 ms`;
- GPU timestamp delta p50/p95/p99 was
  `-49.350 / -47.064 / -45.809 ms`;
- malformed, stalled, and slow peers published no policy and completed an
  all-forwarded baseline with 132 tracked calls and zero skips.

The native final gate uses a bounded CPU shadow of an immutable upload range,
automatic and explicit destination invalidation, a four-second action budget,
fence completion, full readback equivalence, Debug Layer validation, and atomic
vtable rollback.

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
  -File tools/Test-GatewayManagedD3D12Copy.ps1 `
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
- [v0.20 D3D12 actuation evidence](docs/evidence/v0.20.0-d3d12-copy-elision.md)
- [v0.19 end-to-end authorization evidence](docs/evidence/v0.19.0-end-to-end-authorization.md)
- [v0.18 resilience and 128-action evidence](docs/evidence/v0.18.0-resilience-update-upload-128.md)
- [FluidLink v0.17 batch evidence](docs/evidence/v0.17.0-fluidlink-operation-batch.md)
- [D3D12 observation evidence](docs/evidence/v0.16.0-d3d12-observation.md)
- [Project handoff briefing](docs/BRIEFING-CLAUDE-CODE.md)

FluidRuntime is open source under the [MIT License](LICENSE).
