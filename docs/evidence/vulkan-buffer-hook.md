# Vulkan Buffer Resource Hook

Validated locally on Windows x64, 2026-09-09. This extends the existing explicit
Vulkan layer; it does not add a kernel driver, existing-PID injection, global
registration, or third-party D3D11/D3D12 interception. Third-party calls remain
unchanged. It is a resource-observation milestone, not external copy elision.

## Verified Scope

- Create/destroy buffer hooks and allocation lifetime, including driver-handle reuse.
- Legacy, core bind2 and KHR bind2 attribution; failures discard classification.
- Legacy, core copy2 and KHR copy2 forwarding and bound-memory categories.
- Fixed allocation/buffer tables: 8,192 entries each, together at most 2 MiB.
- Destruction retires tracking before downstream callbacks can recycle identities.
- No buffer-content reads, driver calls inside the tracking lock, or allocations
  in the table lookup/churn path. This is not an allocation-free claim for the
  Vulkan driver, loader, collector or entire application.
- Shared observation ABI 2 (400 bytes, 46 counters), report schema v2; old/new
  collector/DLL mismatch is rejected. Gateway imports legacy v1 and new v2.
- Existing FluidLink, Gateway C ABI, server and in-process authorization remain
  unchanged; these observation counters never grant an action budget.

## Validation

| Check | Result |
| --- | --- |
| Gateway full Python suite | 335 passed, no skips |
| Runtime managed Release / Debug, with trusted Gateway DLL and native targets | 277 passed each, no skips |
| Native CTest Release / Debug / ASAN, hardware Vulkan enabled | 33 passed each |
| MSVC analysis, observer / dispatch tests / tracker tests | Passed, warnings treated as errors |
| Owned original-execution transfer target under layer | 2 Release A/B pairs; 1 Debug and 1 ASAN pair |
| Unmodified Khronos `vkcube.exe` | 2 Release A/B pairs, 120 presents per run |
| Existing priority-lease regression on cube only | Applied temporarily, restoration confirmed |
| Old observation DLL with new collector | Session rejected, original cube exited 0 |
| Gateway import of final session artifacts | All 14 v2 sessions accepted; actuation remains false |

The dispatch tests use a mock driver, so they run without a Vulkan ICD. They
cover exact forwarding of copy/bind pointers, returned errors, create failure,
core/KHR dispatch, 4 concurrent command-buffer streams, disabled counters,
reentrant buffer/device recreation, saturation, and generation-safe memory
reuse. Tracker tests also exercise full tables, repeated churn, separate devices,
unknown bindings, same-allocation buffers, and uint64 range boundaries. Fault
scenarios with invalid ranges/handles are mock-only, never sent to hardware.

In each observed owned Release run, the hook recorded 32 buffer copies totaling
128 MiB: 112 MiB HOST_VISIBLE-only -> DEVICE_LOCAL-only and 16 MiB in reverse.
All 6 buffers and 6 allocations were retired; binding failures, unknown copy
bytes, dropped records, saturation and API errors were zero. The unchanged
owned target checks exact GPU readback, original-copy counts and rollback
internally; an unsuccessful check exits nonzero and fails the capture script.

Cube exercised 4 buffer creations/destructions and one buffer-to-image copy.
It did **not** exercise buffer-to-buffer copies, so it is compatibility/lifetime
evidence, not proof of the transfer byte categories. Its executable SHA-256:
`af8ac60765bd0d489e2de3f40b852332aea02ba92c07acbd7c7d368a05c8be1c`.

The initial remote interop run also exposed a timing assumption in an existing
D3D11 readback negative test: its baseline could exit before ring discovery with
a 50 ms hold. That test now holds the owned baseline for the collector's bounded
five-second discovery window. Production deadlines and all identity, denial,
original-copy and content assertions are unchanged. This is a test-collection
correction, not a Vulkan or FluidLink contract change.

## Raw Evidence

[Session archive](traces/vulkan-buffer-hook.zip), SHA-256:
`9b9877496b57d77b4fb1623045d628150c5dddd60fa279d40095d2d2664086a2`.
It contains the complete A/B captures, the mixed-ABI negative and the cube
priority restoration record/summary. ASAN is a validation build, not a runtime
dependency of the normal Release layer.

Measured layer SHA-256 values:

| Build | Hash |
| --- | --- |
| Release | `36ca56ef1c9cec0ace825707839d0d486f025005cf535456bc53aff85c42eaba` |
| Debug | `753f6b0e9600bee71532771dad965977906e29677dc76390bf5ccf2b10823ee5` |
| ASAN | `6e35db401ed3466b715d8cbac23064242727f89d7f39b07006cea5d44eeae70c` |

These identify local tested binaries, not a promise of byte-identical independent CI builds.

## Reproduce

```powershell
dotnet build FluidRuntime.slnx -c Release -warnaserror
cmake -S native -B native/build-vulkan -A x64 -DFLUIDRUNTIME_TEST_VULKAN_GPU=ON
cmake --build native/build-vulkan --config Release
ctest --test-dir native/build-vulkan -C Release --output-on-failure
./tools/Test-VulkanBufferHooks.ps1 -Pairs 2
./tools/Test-ApplicationSessions.ps1 -VkcubePath C:\VulkanTools\vkcube.exe `
  -BuildPath native/build-vulkan -Pairs 2 -Frames 120 -SkipCollectorCrash
```

Use current Gateway `main` to import a resulting `app-session` JSON with
`python -m fluidgateway analyze-app --session <capture.json> --out <report.html>`.
Normal sessions need no system-wide layer installation and do not terminate
the selected application when capture stops. See [the usage guide](../application-sessions.md).

## Limits

These are short correctness/compatibility runs, not a performance study or soak.
The original and observed runs use the same target, arguments and normal GPU
work. CPU/RAM samples are coarse and final samples reuse the last live reading.
No FPS, input-latency, power or reduced physical-traffic claim follows from them.
Hook overhead and longer application workloads still need measurement.

Memory properties describe Vulkan flags at recording time. Combined
HOST_VISIBLE/DEVICE_LOCAL memory stays in its own category. Same-allocation
copies can be required; allocation identity is not content identity. No command
buffer submission/replay model, shader-write provenance, image layout model or
safe-elision authorization for third-party applications is implemented here.
Unknown extension chains and sparse/protected buffers stay unclassified.

Primary semantics: [memory properties](https://docs.vulkan.org/refpages/latest/refpages/source/VkPhysicalDeviceMemoryProperties.html),
[bind2 failure behavior](https://docs.vulkan.org/refpages/latest/refpages/source/vkBindBufferMemory2.html),
[copy commands](https://docs.vulkan.org/spec/latest/chapters/copies.html).
