# FluidLink 0.5 and Gateway Readback

This step reduces managed transport overhead and expands **Gateway authorization**
to the existing owned D3D11 device-to-staging readback actuator. It does not add
global hooks, arbitrary-process intervention, residency management or a scheduler.
The Gateway v0.69.0 server and DLL are unchanged; no contract hash or C ABI changed.

## Transport Changes

- Caller-owned frame encoding with overlap/capacity checks and initialized headers.
- Request buffer pooling, cleared on success, rejection and exception.
- Owned single-buffer decoding; no payload clone after stream assembly.
- Direct strict UTF-8 writes and non-boxing hot opcode/flag checks.
- Cached immutable Runtime authorization profiles shared by both backends.

The client retains negotiation, correlation, session identity, deadlines, vector
cardinality and decision checks. Owned responses survive input reuse. Request
memory is borrowed only until completion. Cancellation cannot preempt a synchronous
DLL call; late results grant no authority. Native copies and managed decision
objects remain: this is not zero-copy or an allocation-free authorization path.

## Authorized Readback

```powershell
$dll = (Resolve-Path ../FluidGateway/native/build/Release/FluidGatewayNative.dll).Path
$sha = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
dotnet run --project src/FluidRuntime -c Release -- gateway-readback-lab `
  --target native/build/Release/fluidruntime-hook-target.exe `
  --hook native/build/Release/fluidruntime-present-hook.dll `
  --gateway-backend inprocess --gateway-library $dll --gateway-library-sha256 $sha `
  --trial-pairs 2 --warmup-pairs 0 --hardware false --out artifacts/gateway-readback.json
```

For server mode omit DLL options and supply `--gateway-pid`,
`--gateway-executable-sha256` and optionally `--port`; exact IPv4 loopback is required.
The isolated server remains the default. No silent fallback switches backends.

Each run describes a VRAM/device source and RAM/staging destination using the
existing numeric `Copy` opcode. One seed executes; 64 repeat candidates of 4 MiB
are authorized through the existing batch profile. The readback-specific context
binds the action, binary hashes, pair, phase and topology. Native provenance and
generation checks remain the final gate, with a four-second, 64-action policy.
All 65 read maps and required synchronization remain forwarded. Full readback
hashes and post-detach rollback must match. Omitted logical bytes are not measured
physical PCIe traffic, and necessary readback is not removed.

The runner locks owned binaries and verifies actual target/hook modules and ring
PID before publishing a policy. It rejects identity drift and reused contexts.
A failed authorization runs a verified original readback (or retains the already
completed baseline), writes a failure report and returns exit code 3. Earlier
completed pairs are counted separately. Invalid CLI inputs or DLL load errors
can stop before launch; they never grant local policy authority. Cancellation
stops owned work and cleans it up. Legacy `readback-elision-lab` remains a separate
local-policy experiment, never a silent authority fallback for this command.

Legacy authorization type names and `seed_upload_executed` remain compatible.
`seed_transfer_executed`, `operation_type`, `source_memory_layer` and
`destination_memory_layer` make the actual direction explicit.
`GatewayUploadBackend` adds value 3 without changing values 0-2.

## Verification

```powershell
$env:FLUIDGATEWAY_DLL = $dll
$env:FLUIDRUNTIME_NATIVE = (Resolve-Path native/build/Release).Path
dotnet test FluidRuntime.slnx -c Release -warnaserror
./tools/Test-GatewayBackends.ps1 -GatewayLibrary $dll `
  -GatewayExecutable ../FluidGateway/native/build/Release/fluidgateway-native.exe
```

The script checks D3D11 upload/readback, D3D12 and Vulkan on both backends, plus
original readback after a bad server hash. Use `-SkipVulkan` without Vulkan hardware.
Managed tests check wrong native actions and a disposed authorizer with real owned
fallback, direction/context tampering, buffer ownership and all golden vectors.
Gateway differential tests cover GPU/CPU writes and source recreation on readback.
Graphics checks use two pairs: correctness evidence, not performance qualification.
The root readback report does not allow a performance claim.

Local validation: 274 managed tests passed in Release and Debug without skips;
Gateway's full suite passed 331 tests. The new readback case and server/DLL corpus
passed in Release, Debug and ASAN. Both graphics modes and negative authority
checks passed; formatting and the PowerShell parser were verified.

## Measured Comparison

Same unchanged Gateway v0.69.0 DLL/server, same Windows machine; old Runtime/FluidLink
and new builds measured sequentially. Each run has five warmup and 30 alternating
server/in-process AB/BA pairs. No GPU actuation occurs in this transport benchmark.
Version order itself is not randomized, so this is not a causal latency study.

| Mode / workload | Allocated managed bytes/op before -> after | p95 us before -> after | p99 us before -> after |
| --- | ---: | ---: | ---: |
| Server / batch 128 | 199.18 -> 87.03 | 547.73 -> 523.23 | 819.03 -> 849.83 |
| DLL / batch 128 | 162.10 -> 80.51 | 357.45 -> 340.23 | 687.29 -> 633.97 |
| Server / authorization 128 | 1041.11 -> 841.93 | 6651.03 -> 6568.47 | 7790.04 -> 7375.73 |
| DLL / authorization 128 | 529.27 -> 387.12 | 663.14 -> 656.91 | 799.11 -> 783.43 |

The clearest result is lower managed allocation. DLL authorization CPU time rose
from about 4959 to 5341 ns/candidate, and CPU cycles rose from about 14020 to 14697.
Server authorization CPU time rose from about 10935 to 16658 ns/candidate, while
its cycles fell slightly; server batch p99 also rose. Do not claim universal CPU or latency improvement;
Windows CPU-time counters are coarse, and one-machine scheduling/JIT noise remains.
Host private memory did not consistently fall. No FPS, input-delay or physical-transfer claim.

Raw samples, p50/p95/p99, CPU time/cycles, throughput, memory and binary hashes:
[before](evidence/fluidlink-v0.5/before.json) and [after](evidence/fluidlink-v0.5/after.json).
The [earlier optimized run](evidence/fluidlink-v0.5/after-initial.json) is retained
to expose variation; the table uses the final formatted build, not the best run.
The earlier v0.69.0 release evidence remains unchanged. The next deeper transport
step is a separately versioned typed native API, still sharing the same core;
this change does not implement it.
