# FluidRuntime

**A Windows research runtime for finding and safely removing redundant work
between CPU, GPU, RAM, VRAM, and the graphics pipeline.**

[![Version](https://img.shields.io/badge/version-0.23.0-ef6c35)](src/FluidRuntime/FluidRuntime.csproj)
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
| Native telemetry | Persistent read-only process, RAM, VRAM, and GPU-engine series |
| Vulkan | Native cooperative buffer-copy library, FluidLink authorization, exact readback and rollback |
| Third-party Vulkan | Opt-in launch observation; verified with unmodified Khronos cube applications |
| Windows integration | Optional Normal -> AboveNormal priority lease with independent rollback watchdog |
| General game optimization | Not established; external GPU operations are never removed |

## Application Sessions

The v0.23 source on `main` can observe a selected x64 Vulkan application through
an explicit loader layer, collect CPU/RAM and Vulkan counters, and send the
result to Gateway's `analyze-app` report. No global installation is required.
Windows priority changes are **off by default** and limited to a short,
explicitly requested lease. Protected/anti-cheat applications are unsupported.

[Build and run a session](docs/application-sessions.md) |
[Test evidence and limitations](docs/evidence/v0.23.0-application-integration.md).

## Persistent Native Telemetry

`fluidruntime inspect` can collect up to 100 native GPU/VRAM snapshots without
launching a probe process per sample. The native probe keeps one bounded PDH
session alive, reuses its counters, and emits an ordered
`native_probe_samples` series. The default remains one snapshot.

```powershell
fluidruntime inspect --ledger ledger.json --out report.json --pid 1234 `
  --samples 30 --interval-ms 1000 `
  --native-probe fluidruntime-native-probe.exe --native-probe-samples 30
```

This path is observational and read-only. It does not inject, hook, schedule,
change residency, or optimize the target process.

## Native Vulkan

The v0.22 backend removes proven redundant `vkCmdCopyBuffer` calls from an
owned RAM -> device-local memory -> RAM workload. Two isolated lanes retain
exact content through source changes, fills, invalidation, reset and revocation.
FluidGateway authorizes a bounded policy through the existing binary FluidLink
contract; the native library makes the final decision.

This is a reusable **cooperative library**, not a Vulkan interception layer for
arbitrary games. It owns its Vulkan objects, installs nothing globally, and
does not remove required barriers or fences. Logical bytes omitted are not
physical PCIe traffic, VRAM savings, or proof of higher FPS.

[Build, run and understand the boundary](docs/vulkan-native.md).
[Measurements and validation](docs/evidence/v0.22.0-vulkan-native.md).

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
   native D3D11/D3D12/Vulkan labs
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

FluidRuntime does not remotely inject into existing processes, alter drivers,
replace the Windows scheduler, control physical residency, or promise unified-
memory behavior in software. GPU intervention is limited to owned deterministic
targets. Third-party Vulkan observation forwards calls unchanged; the separate
Windows priority lease never requests High/Realtime priority or global changes.

Lab commands use disposable owned workloads. `app-session` launches only the
selected application and leaves it running when capture stops. The observation
layer stays loaded until that application exits; stopping capture is not an unload.

## Documentation

- [Code review and regression evidence](docs/evidence/2026-09-05-code-review.md)
- [Current status and release gate](docs/STATUS.md)
- [Architecture and trust boundaries](docs/architecture.md)
- [Roadmap](docs/roadmap.md)
- [Native Vulkan library and lab](docs/vulkan-native.md)
- [v0.21 generalized D3D12 transfer evidence](docs/evidence/v0.21.0-d3d12-transfer-core.md)
- [v0.21.1 local-use hardening evidence](docs/evidence/v0.21.1-local-use-hardening.md)
- [v0.21.2 CI portability evidence](docs/evidence/v0.21.2-ci-portability.md)
- [v0.20 single-lane D3D12 evidence](docs/evidence/v0.20.0-d3d12-copy-elision.md)
- [v0.19 end-to-end authorization evidence](docs/evidence/v0.19.0-end-to-end-authorization.md)
- [v0.18 resilience and 128-action evidence](docs/evidence/v0.18.0-resilience-update-upload-128.md)
- [FluidLink v0.17 batch evidence](docs/evidence/v0.17.0-fluidlink-operation-batch.md)
- [D3D12 observation evidence](docs/evidence/v0.16.0-d3d12-observation.md)
- [Project handoff briefing](docs/BRIEFING-CLAUDE-CODE.md)

FluidRuntime is open source under the [MIT License](LICENSE).
