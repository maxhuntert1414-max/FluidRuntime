# Native Vulkan Transfer Library

FluidRuntime v0.22 implements Vulkan on Windows x64 as a cooperative DLL with
an opaque, versioned C ABI. It is not an implicit/explicit loader interception
layer, game injector, graphics scheduler, or engine integration. That wider
surface needs its own observation and provenance work before actuation.

## Build And Run

Requirements: Windows x64, an MSVC C++ toolchain, CMake 3.25+, .NET 10,
the adjacent FluidGateway checkout, and a working vendor Vulkan driver.
Python/.NET dependencies are unchanged. CMake fetches Khronos Vulkan-Headers
1.3.290 from a SHA-256-pinned archive; no SDK installation is required.
For offline builds, set `FETCHCONTENT_SOURCE_DIR_VULKANHEADERS` to a previously
verified copy. `FLUIDRUNTIME_ENABLE_VULKAN=OFF` preserves a Vulkan-free build.

```powershell
cmake -S native -B native/build -A x64 -DFLUIDRUNTIME_TEST_VULKAN_GPU=ON
cmake --build native/build --config Release
dotnet build FluidRuntime.slnx -c Release -warnaserror
ctest --test-dir native/build -C Release --output-on-failure

./tools/Test-GatewayManagedVulkanCopy.ps1 `
  -GatewayPath ../FluidGateway -TrialPairs 10 -WarmupPairs 1
```

The script starts its own loopback Gateway, verifies AB/BA pairs and malformed,
stalled and cumulatively slow peers, writes JSON in `artifacts/`, and stops its
own server. It does not target an existing game or leave a background optimizer.
`-Configuration Debug` selects native Debug binaries. `-Software` explicitly
requires a CPU Vulkan ICD; it does not silently fall back from hardware.

With an independently started Gateway, use the lower-level command:

```powershell
dotnet run --project src/FluidRuntime -c Release --no-build -- `
  gateway-vulkan-copy-lab `
  --target native/build/Release/fluidruntime-vulkan-transfer-target.exe `
  --library native/build/Release/fluidruntime-vulkan-transfer.dll `
  --gateway-pid 1234 --gateway-executable-sha256 <sha256-of-server-executable> `
  --trial-pairs 10 --warmup-pairs 1 --out artifacts/vulkan.json
```

Exit 0 means the owned execution/evidence gate passed, not a performance claim.
Exit 3 means authorization failed and a fresh, verified all-forwarded baseline
completed. Native failures after policy publication are errors, not a claim
that no action happened. Partial or invalid runs never become successful pairs.

## Library Contract

The ABI is [fluidruntime_vulkan_api.h](../native/include/fluidruntime_vulkan_api.h).
Load `fluidruntime-vulkan-transfer.dll` by absolute path, resolve
`FluidVulkanGetApi`, and supply the exact table size and ABI version. The
[owned target](../native/src/vulkan_transfer_target.cpp) is an executable example.

1. `create` owns a Vulkan instance/device, one graphics-capable queue, two
   primary command buffers, one fence, and dedicated non-aliased allocations.
2. `set_source` freezes two full-buffer source patterns per lane in host-visible
   staging memory plus CPU shadows. The input pointer is not retained.
3. Optional `wait_control` accepts one IPC epoch, action bit 16, 1..128 actions
   and at most four seconds of recording authority. Without it, every copy is
   forwarded. The C ABI has no self-authorize operation.
4. `begin`, `copy`, `fill` and `invalidate` record the private workload. Copies
   are omitted only after exact comparison with an immutable source already
   recorded for that same destination and command buffer.
5. `submit` appends readback and host-visibility barriers, closes both scopes,
   submits once and waits for the fence with a bounded timeout. `readback`
   requires completed work and an exact-size output buffer.
6. `revoke` permanently prevents future elision in that context. A new `begin`
   resets lane knowledge. `destroy` releases the private objects and can return
   final validation counts, including teardown diagnostics.

Calls are single-owner-thread only, including destruction. The ABI does not
expose borrowed Vulkan handles, GPU-writable sources, aliased memory, images,
partial regions, secondary buffers, timelines or queue-family transfers. There
is no per-frame Python/.NET traffic: candidate classification stays in C++.

Buffers are 4 bytes..4 MiB, multiples of four; the shipped workload uses 4 MiB.
Two 8 MiB staging allocations and 16 MiB of immutable CPU shadows are explicit
costs, not savings. Retained lane knowledge references frozen source slots;
there is no unbounded content cache. Sources cannot change while recording,
pending, or after an authorization attempt. Fill and explicit invalidation
clear lane knowledge; reset and close never reuse previous recording evidence.

If a fence wait times out, no reset/readback/destruction is allowed while work
is pending. `submit` may be called again only to wait for that existing fence.
The CLI additionally bounds and reaps its owned child process. It cannot bound
or recover a GPU driver stuck inside an OS call; no driver-reset mechanism is
installed. A caller embedding the DLL must preserve these lifetime rules.

## Memory And Synchronization

Host uploads are unmapped before submission. Full dedicated-allocation
flush/invalidate ranges handle non-coherent atom alignment. Copy/fill hazards
retain transfer dependencies; readback uses transfer-write -> host-read,
fence completion, then mapped-memory invalidation. The backend never assumes
that host map/unmap or command order alone supplies those dependencies.

References: [Vulkan synchronization](https://docs.vulkan.org/spec/latest/chapters/synchronization.html),
[copy requirements](https://docs.vulkan.org/refpages/latest/refpages/source/vkCmdCopyBuffer.html),
[readback visibility](https://docs.vulkan.org/refpages/latest/refpages/source/vkInvalidateMappedMemoryRanges.html).

## Validation And Evidence

With Khronos validation available, pass `-Validation` to the script or
`--validation true` to the CLI. This explicitly enables core and synchronization
validation and rejects warnings/errors. Missing validation is an error when
requested, never a silently weakened test. Without it, reports explicitly set
`validation_enabled=false`. Use a process-local `VK_LAYER_PATH` for a portable
validation build; nothing needs to be registered in Windows.
General loader notices (including intentionally disabled implicit overlays) are
counted separately and preserved as native diagnostics. They are not API or
synchronization validation warnings; all error-severity messages still fail.

The manager uses backend ID 3, operation ID 2, generalized events 17..22 and
the existing native ring ABI 9 at `Local\FluidRuntimeTransfer-3-<pid>`.
FluidLink's wire schema and opcodes are unchanged. Authorization binds the
Vulkan domain, exact topology, pair/phase, server PID and executable hash, plus
frozen target/library hashes. The loopback/shared-memory path is not a security
boundary against malicious software running as the same user.

Every run verifies the full ordered event sequence, lane IDs, source slots,
generation changes, fences, policy acknowledgement, exact readback, zero ring
loss and all-forwarded rollback. At 128 candidates, the measured phase forwards
136 calls in baseline or eight in controlled mode; four additional rollback
copies must always be forwarded. Omitted 512 MiB means logical API bytes only.

AB/BA order alternates independently in warmup and measured phases. Raw runs,
authorization cost, managed end-to-end, native workload, submit-to-fence and GPU
timestamp intervals are reported separately. GPU intervals sum the two private
command-buffer durations, not frame time. Statistics are descriptive and
`performance_claim_allowed` stays false for general game/product claims.

Hosted CI compiles Vulkan and runs its pure policy/parser tests without an ICD.
Actual GPU CTests require `FLUIDRUNTIME_TEST_VULKAN_GPU=ON`; the local hardware
and validation results are recorded separately in the evidence document.
