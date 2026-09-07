# C++ Gateway Integration

FluidGateway v0.68 adds a standalone C++20 decision endpoint. FluidRuntime's C#
coordinator and D3D11/D3D12/Vulkan native libraries retain their responsibilities.
The Gateway wire contracts and FluidLink package version do not change.

## Select the Endpoint

The native backend is opt-in until the v0.68 release promotion gates are recorded.
The validation scripts currently retain Python as their default.

Start the native Gateway explicitly:

```powershell
..\FluidGateway\native\build\Release\fluidgateway-native.exe serve-events --host 127.0.0.1 --port 8765
```

Pass its actual PID and SHA256 to existing Runtime managed-lab commands. A Python
interpreter hash is not interchangeable with the native executable hash. The
test scripts calculate and verify the identity of whichever backend was selected:

```powershell
.\tools\Test-GatewayManagedUpdateUpload.ps1 -GatewayBackend Native
.\tools\Test-GatewayManagedD3D12Copy.ps1 -GatewayBackend Native
.\tools\Test-GatewayManagedVulkanCopy.ps1 -GatewayBackend Native -BuildPath native/build-vulkan
```

`-GatewayExecutable` overrides the selected executable's path, not its identity
checks. `-GatewayBackend Python` explicitly selects the reference implementation.
Neither mode changes system configuration or introduces an automatic fallback to
another language. Fault-injection fixtures use Python as test machinery only.

## Compatibility and Measurement

The C++ endpoint supports FluidLink v2 base and batch, not v1 or JSONL. The
comparison-oriented `link-probe` can use `--v1-baseline-port <port>` to run its
legacy flow against an explicitly selected second loopback server. Both ports
are recorded in the report. `Test-FluidLinkIntegration.ps1 -GatewayBackend Native`
starts and cleans up the separate reference peer for that comparison.

For the actual manager authorization A/B test, build:

```powershell
dotnet build tools/GatewayComparison/GatewayComparison.csproj -c Release -warnaserror
```

Then run Gateway's `tools/compare_runtime_gateway.py`. It compares five warmup
and thirty measured AB/BA pairs using the real authorizer with PID, executable
hash, nonce/context, capability, topology and decision checks. Negative PID/hash
controls must fail. No native policy is published by this benchmark.

CPU cycles are measured with QueryProcessCycleTime. Windows process-time samples
can round short intervals to zero; they do not establish zero CPU consumption.
Keep transport/core benchmarks separate from full authorization and GPU timing.

## Safety Is Unchanged

The new endpoint still emits advisory intent. Runtime independently verifies
native content equivalence and provenance before applying bounded controls.
Malformed/stalled/slow peers continue through verified all-forwarded baselines.
Connection retirement never extends a native lease or removes expiry/rollback.

The C++ Gateway bounds retained state and requires a fresh connection on capacity
exhaustion. Current manager runs already establish new short-lived connections.
There is no transparent infinite-session renewal for third-party clients.
No general game optimization, physical PCIe savings or system-wide scheduling
claim follows from a faster control endpoint.
