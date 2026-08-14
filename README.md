# FluidRuntime

**A Windows research runtime for finding and safely removing redundant work
between CPU, GPU, RAM, VRAM, and the graphics pipeline.**

[![Version](https://img.shields.io/badge/version-0.21.1-ef6c35)](src/FluidRuntime/FluidRuntime.csproj)
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
| D3D12 | Gateway-authorized multi-lane buffer elision with queue/fence provenance |
| Vulkan | Planned, not implemented |
| External games | Unsupported; owned opt-in workloads only |

## v0.21.1 Safety Release

This patch does not widen native authority. It makes the current owned-lab path
safer to run locally: every launched target is terminated and reaped on timeout
or cancellation, native-probe duration is bounded, evidence files are replaced
atomically, and the native toolchain treats warnings as errors with SDL and
Control Flow Guard enabled. MSVC code analysis is clean for the two targets that
previously reported null-dereference and excessive-stack warnings.

## v0.21 Result

The D3D12 path now consumes a backend-neutral transfer contract covering queues,
execution scopes, resources, lanes, operations, and fences. Across 30 measured
RX 580 pairs plus one warmup:

- two command lists and two independent destination lanes preserved exact final
  content while each optimized run omitted 128 redundant 4 MiB calls;
- baseline forwarded 136 tracked calls; optimized forwarded the eight required
  guards and skipped 128 candidates;
- submit-to-fence delta p50/p95/p99 was
  `-45.817 / -42.616 / -37.080 ms`, with 30/30 optimized wins;
- GPU timestamp delta p50/p95/p99 was
  `-46.870 / -42.814 / -38.004 ms`, also with 30/30 wins;
- the native execution gate passed, but the complete managed path did not:
  end-to-end p95/p99 was `+18.262 / +53.557 ms`, so the product-level
  performance claim remains blocked;
- malformed, stalled, and slow peers published no policy and completed an
  all-forwarded 136-call baseline with zero skips.

The reusable contract, transfer event/action opcodes, unique destination
ownership rule, and numeric backend/operation IDs are ready for a Vulkan
implementation. Vulkan itself is not implemented in this release.

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
dotnet build FluidRuntime.slnx -c Release -warnaserror

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

FluidRuntime launches and terminates only executables supplied to explicit lab
commands. It does not discover arbitrary games or inject into an external PID.

## Documentation

- [Current status and release gate](docs/STATUS.md)
- [Architecture and trust boundaries](docs/architecture.md)
- [Roadmap](docs/roadmap.md)
- [v0.21 generalized D3D12 transfer evidence](docs/evidence/v0.21.0-d3d12-transfer-core.md)
- [v0.21.1 local-use hardening evidence](docs/evidence/v0.21.1-local-use-hardening.md)
- [v0.20 single-lane D3D12 evidence](docs/evidence/v0.20.0-d3d12-copy-elision.md)
- [v0.19 end-to-end authorization evidence](docs/evidence/v0.19.0-end-to-end-authorization.md)
- [v0.18 resilience and 128-action evidence](docs/evidence/v0.18.0-resilience-update-upload-128.md)
- [FluidLink v0.17 batch evidence](docs/evidence/v0.17.0-fluidlink-operation-batch.md)
- [D3D12 observation evidence](docs/evidence/v0.16.0-d3d12-observation.md)
- [Project handoff briefing](docs/BRIEFING-CLAUDE-CODE.md)

FluidRuntime is open source under the [MIT License](LICENSE).
