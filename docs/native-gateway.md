# C++ Gateway Integration

FluidGateway v0.68 adds a standalone C++20 decision endpoint. FluidRuntime's C#
coordinator and D3D11/D3D12/Vulkan native libraries retain their responsibilities.
The Gateway wire contracts and FluidLink package version do not change.

## Select the Endpoint

The validation scripts now default to **Native**, following the published
[v0.68.0 promotion evidence](https://github.com/maxhuntert1414-max/FluidGateway/blob/v0.68.0/docs/release-v0.68.0.md).
The CI pins that release and tests Native and the explicit Python reference in
separate jobs. A missing native executable is an error, never an implicit fallback.

Build the sibling Gateway checkout first, or extract its Windows x64 release ZIP
and pass `-GatewayExecutable <path-to-fluidgateway-native.exe>` to the scripts:

```powershell
cmake -S ../FluidGateway/native -B ../FluidGateway/native/build -A x64
cmake --build ../FluidGateway/native/build --config Release
```

For manual Runtime commands, start the native Gateway explicitly (the validation
scripts launch and clean up their own server):

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

The standalone smoke test verifies the entire positive path without Python on
PATH, including actual native D3D11 actuation, content checks and rollback:

```powershell
.\tools\Test-NativeGatewayStandalone.ps1 `
    -GatewayExecutable ..\FluidGateway\native\build\Release\fluidgateway-native.exe
```

It needs prebuilt Release Runtime/owned-target binaries and the .NET runtime,
not Python. Use `-BuildPath native/build-vulkan` for that local build layout.
Only the test process environment is restricted; machine/user settings are not
modified. Its JSON report is paired with an `.environment.json` verification.
`Test-GatewayServerCommand.ps1 -GatewayPath ../FluidGateway` separately verifies
selection, executable hashes, script defaults and missing-executable rejection.

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
It returns a nonzero exit code if either correctness or the p95/p99/CPU comparison
fails; the JSON is still written so negative measurements remain inspectable.

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
