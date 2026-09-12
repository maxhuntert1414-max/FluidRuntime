# Vulkan Completion Hook Evidence

Local validation on Windows x64, 2026-09-12. This milestone adds observation,
not third-party copy elision, a driver, or a performance improvement claim.
The [completion contract](../vulkan-completion-observation.md) defines scope.

## Verified Results

| Check | Result |
| --- | --- |
| Native full CTest, hardware Vulkan enabled | 35/35 each in Release, Debug and ASAN |
| Completion tracker | 30,032 checks, including 10,000 fixed-slot lifecycle iterations |
| MSVC static analysis | Layer, dispatch, buffer, command and completion trackers pass with warnings as errors |
| Managed Runtime | 283/283 each in Release and Debug, no skips; trusted Gateway DLL/native targets enabled |
| Gateway | 343/343; 23 focused application-session cases |
| Owned buffer workload | Two Release AB/BA pairs; one Debug and one ASAN pair; original copies and exact readback preserved |
| Unmodified Khronos cube | Two Release AB/BA pairs, 120 presents/run; 121 accepted and completed queue calls, 117 recording replays |
| Existing short priority lease | Separate owned cube run restored Normal after a one-second AboveNormal lease |
| Old ABI 3 DLL | New collector rejects observation; original cube still exits 0 |
| Historical import | Gateway accepts v1/v2/v3 without invented completion; managed reader rejects ABI 1/2/3 layouts |
| Final capture import | All 14 v4 sessions produce HTML/JSON with GPU-actuation/performance authority false |

All four observed buffer runs reported **32 buffer-copy calls / 134,217,728 bytes**
recorded, attributed to **two accepted queue calls**, and confirmed by **two
existing fence waits**. Each had one fence, no device-idle calls, and zero pending,
abandoned, unresolved, overflow or error counters after teardown. The target's
own exact GPU readback and original-copy checks passed in the unobserved runs too.

Each observed cube run reported three fences, 123 fence waits and two device-idle
calls. All 121 accepted submits had driver-reported completion, with zero pending,
abandoned or tracking errors after teardown. Cube had **zero buffer-buffer copy
bytes**; its image copy does not become an invented byte estimate.

Mock dispatch separately covers legacy/core/KHR submits, secondary replay,
concurrent externally synchronized queues, failed submit/idle, not-ready/timeout,
wait-any ambiguity, wait-all above 64 fences, reset during a downstream wait,
handle reuse and device extension opt-outs. Original status/wait/idle/reset call
counts are checked to detect injected polling or waits. Tracker tests cover
queue/device isolation, older-prefix deduplication, snapshot cutoffs, capacity
exhaustion, stale generations and arithmetic extremes. These are synthetic
control-path tests, not hardware coverage of every Vulkan extension.

## Bounds and Measurement Limits

`sizeof` on this build: resource + command + completion tables **5,822,536 bytes**;
device-idle marker snapshot **10,240 bytes**. These are fixed native storage
measurements, not process RSS, VRAM residency or peak stack measurements.

The two short cube pairs recorded observed-minus-baseline capture-window deltas
of **+4.53 ms** and **+5.11 ms**, across roughly 2.4 seconds/run. This tiny sample
includes loader, collector and presentation noise; it neither isolates hook CPU
cost nor establishes a performance regression/improvement. No FPS, input-latency,
power or physical transfer savings are claimed. A dedicated overhead benchmark
and broader application/driver matrix remain future work.

ASAN ran with the compiler's runtime available only in the test process PATH and
`halt_on_error=1:abort_on_error=1:detect_leaks=0`. This is not a leak-sanitizer claim;
ASAN binaries are not the normal Release package. No global configuration, registry,
driver, services, keyboard settings or user game was changed. The collector-crash
test was skipped in this milestone; the existing separate fault-test evidence is
not relabeled as a new run.

Completion remains a driver signal, not proof of correct content after device
loss, host-memory visibility, valid writes/aliases or redundant GPU work.
Timeline semaphore/cross-queue dependency and content-generation models are not
implemented. Hardware secondary/submit2 paths are not established by these captures.

## Reproduce and Inspect

Use the build/test procedure in [application sessions](../application-sessions.md).
Final workload commands:

```powershell
./tools/Test-VulkanBufferHooks.ps1 -BuildPath native/build-vulkan -Pairs 2
./tools/Test-VulkanBufferHooks.ps1 -BuildPath native/build-vulkan -Configuration Debug -Pairs 1
./tools/Test-VulkanBufferHooks.ps1 -BuildPath native/build-vulkan-asan -Pairs 1
./tools/Test-ApplicationSessions.ps1 -BuildPath native/build-vulkan `
  -VkcubePath native/build-vulkan-tools/cube/Release/vkcube.exe `
  -Pairs 2 -Frames 120 -SkipCollectorCrash
```

Set the compiler's ASAN runtime PATH before the ASAN command. Existing scoped
logs are `artifacts/completion-hook-*.log`; Gateway's full-suite log is
`tmp/completion-hook-unittest.log`. Local final HTML/JSON diagnoses are in the
Gateway checkout's `tmp/completion-hook-final` directory.

Published [raw archive](traces/vulkan-completion-hook.zip): 14 final sessions,
the matching cube priority-helper result and the final cube AB/BA summary. It
excludes preliminary captures and their superseded helper. SHA-256:
`d485332021cae31dbf1e197af0b7fb25b3b10235f1a157a99e7b145f6cf73182`.

| Observed binary | SHA-256 |
| --- | --- |
| Layer Release | `6da35a0726037e2fc717491c2b826ef087049e81baf560fcce497bf1b2c6e697` |
| Layer Debug | `c1b03018617e9642b199f3fa792924be5b8bb1a3ade5910f5585fff56d268cbf` |
| Layer ASAN | `93dbdef60f05aa33ff2b23f1579822d237fa93713ad5bb53e8d7c2209eb5171e` |
| Khronos cube | `af8ac60765bd0d489e2de3f40b852332aea02ba92c07acbd7c7d368a05c8be1c` |

Gateway's unchanged trusted v0.69.0 DLL used in managed integration tests:
`51b877627bfd2ea0f9384d7869706f4bf5e416b0190688b3a479826973d0e220`.
Release tags, FluidLink contracts and Gateway binary pin remain unchanged.
