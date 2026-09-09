# Gateway Backends

The owned D3D11, D3D12 and Vulkan authorization paths accept two explicit modes:
`--gateway-backend server` (default) or `--gateway-backend inprocess`.
The existing `-GatewayBackend Python|Native` PowerShell server selector is
unchanged; it selects the implementation of the isolated server, not a DLL.

## In-Process

Build FluidGateway v0.69 or use its Windows x64 native package. Build Runtime
with .NET 10. Supply the trusted DLL's absolute path and SHA-256:

```powershell
$dll = (Resolve-Path ../FluidGateway/native/build/Release/FluidGatewayNative.dll).Path
$sha = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
dotnet run --project src/FluidRuntime -c Release -- gateway-d3d12-copy-lab `
  --target native/build/Release/fluidruntime-d3d12-transfer-target.exe `
  --hook native/build/Release/fluidruntime-d3d12-transfer-hook.dll `
  --gateway-backend inprocess --gateway-library $dll --gateway-library-sha256 $sha `
  --trial-pairs 2 --warmup-pairs 0 --hardware false --out artifacts/inprocess-d3d12.json
```

The same three Gateway switches work with `gateway-update-upload-lab` and
`gateway-vulkan-copy-lab`. The Vulkan `--library` is still the GPU actuator;
`--gateway-library` is the decision DLL. TCP host/port/PID/hash options cannot
be mixed with in-process mode. Nothing is installed or enabled globally.

For isolated operation retain `--gateway-pid`, `--gateway-executable-sha256`
and the optional loopback port. No silent server fallback occurs: discard the
failed authorization and start a fresh, explicitly selected server session.

The DLL is loaded once per authorizer, with its file locked against modification.
Every authorization creates a fresh bounded session. The C# client retains all
FluidLink negotiation, identity, correlation, capability and decision checks.
Native content checks, short-lived policy epochs, budgets and rollback still
decide whether an operation can actually be omitted.

The C ABI returns status codes; the managed adapter converts errors into its
existing fail-closed exception path. No C++ exception crosses the ABI. Invalid
pointers or memory corruption cannot be recovered safely in-process. Cancellation
cannot preempt a synchronous native call; a late result is rejected. The server
remains the fault-isolated default.

Authorization evidence adds `gateway_backend`, `gateway_library`,
`transport_round_trip_count` and `wire_exchange_count`. The legacy
`round_trip_count` field retains its ten protocol exchanges for compatibility;
in-process transport round trips are zero. Peer process fields refer to the actual
host image, not a nonexistent TCP server, and the context includes DLL hash/ABI.

Paired runs reject changes to the backend or DLL identity. Attaching a concurrency
benchmark also requires a matching backend and, when its peer was verified,
matching DLL identity. D3D11/D3D12 aggregate reports expose `gateway_backend`,
`gateway_library` and `gateway_transport_round_trip_count`; their legacy
`gateway_round_trip_count` still counts protocol exchanges, not TCP operations.

## Tests and A/B

```powershell
$env:FLUIDGATEWAY_DLL = $dll
dotnet test FluidRuntime.slnx -c Release
./tools/Test-GatewayBackends.ps1 -GatewayLibrary $dll `
  -GatewayExecutable ../FluidGateway/native/build/Release/fluidgateway-native.exe
dotnet run --project tools/FluidGateway.BackendBenchmark -c Release -- `
  ../FluidGateway/native/build/Release/fluidgateway-native.exe $dll artifacts/backend-ab.json
```

`Test-GatewayBackends.ps1` accepts `-GatewayMode server|inprocess|both`,
`-NativeDirectory` and `-SkipVulkan` for machines without a Vulkan adapter.
It starts/stops only its owned server and cooperative targets. The managed tests
explicitly skip DLL integration if `FLUIDGATEWAY_DLL` is not set, rather than
pretending that a mock proved native integration.

The benchmark uses five warmup pairs and 30 alternating AB/BA pairs, separately
measuring batches and complete authorization. Output contains individual samples,
p50/p95/p99, CPU time/cycles, managed allocation bytes, throughput, private memory
and scoped DLL PMR/payload-copy counters. Module load/hash verification is excluded
from steady-state timing. Native total allocation counts and OS/kernel copies are
not measured; this is not a zero-copy or universal performance claim.

The optional transport interface belongs to FluidLink 0.4.0; wire contracts remain
the same. The server and DLL share the Gateway C++ core. Typed calls are a later,
separately versioned extension, not a parallel policy implementation.

[Measured A/B results and binary hashes](https://github.com/maxhuntert1414-max/FluidGateway/blob/v0.69.0/docs/release-v0.69.0.md)
are published with Gateway. These are overhead measurements and owned correctness
tests, not a claim about game FPS or physical RAM/VRAM transfers.
