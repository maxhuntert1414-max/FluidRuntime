# Vulkan Command Hook Evidence

Local validation completed on 2026-09-11, Windows x64. This milestone adds
recording/submission provenance to the opt-in Vulkan layer, not third-party GPU
optimization. [Contract and limits](../vulkan-command-observation.md).

## Verified Results

| Surface | Result |
| --- | --- |
| Gateway full suite | 340 tests passed |
| Runtime managed suite | 280 tests passed in Release and Debug, no skips |
| Native Runtime | All 34 CTests passed in Release, Debug and ASAN, hardware Vulkan enabled |
| MSVC analysis | Observer, dispatch, resource and command tracker targets passed with warnings as errors |
| Command tracker | 3,147 checks: generations, reset, replay, secondary references, failed/partial snapshots, capacity and arithmetic |
| Mock layer dispatch | Legacy/core/KHR submission, forwarding, failed submit, concurrent queues and reset during downstream submit |
| Owned buffer target | Two Release AB/BA pairs, one Debug pair, one ASAN pair; exact target content checks and original copies preserved |
| Unmodified Khronos cube | Two Release AB/BA pairs, 120 presents/run; original process exits normally |
| Mixed ABI | Legacy ABI 1 DLL/new collector rejects verification; original cube exits 0; managed tests also reject ABI 2 layouts |
| Gateway import | All 14 final v3 sessions import with actuation and performance claims prohibited |

Managed integration used the trusted unchanged Gateway v0.69.0 DLL and the local
native Release targets through `FLUIDGATEWAY_DLL` / `FLUIDRUNTIME_NATIVE`.
Formatting, PowerShell parsing, Ruff and diff checks passed. Static-analysis
findings were fixed with enum dispatch and explicit bounded-index checks; no
warnings or failing assertions were suppressed.

The owned target records 32 buffer-copy calls / 134,217,728 bytes. Each observed
run attributed exactly that volume to two successful submit calls containing
four primary occurrences, with zero replay, unresolved calls or API errors.
All command buffers retire. These are **submitted**, not saved, bytes.

The cube records four command-buffer generations and makes 121 successful submit
calls. The observer attributes 121 primary occurrences and 117 replays, with no
unresolved calls or command-tracking failures. It has one buffer-to-image copy
and **no buffer-to-buffer copies**; it does not validate submitted buffer bytes.
The existing cube-only priority regression also restored its one-second lease.
Forced collector termination was not rerun in this milestone.

Secondary execution, submit2 aliases, concurrent submissions and failure paths
were checked through the mock driver and standalone tracker, not a new hardware
secondary-command workload. No user-selected game was launched. These short
correctness runs are not a soak, broad compatibility certification or benchmark
of hook overhead, FPS, input latency, physical traffic or energy savings.

Fixed resource plus command tracking state is 5,636,144 bytes on this x64 build;
the per-call submission snapshot is 8,248 bytes. This is `sizeof` evidence, not a
measurement of the whole process's RAM consumption. No per-submit heap allocation
or unbounded history is introduced by the command tracker.

## Reproduction

```powershell
python -m unittest # from the sibling FluidGateway checkout
dotnet test FluidRuntime.slnx -c Release -warnaserror
dotnet test FluidRuntime.slnx -c Debug -warnaserror
ctest --test-dir native/build-vulkan -C Release --output-on-failure
ctest --test-dir native/build-vulkan -C Debug --output-on-failure
ctest --test-dir native/build-vulkan-asan -C Release --output-on-failure
./tools/Test-VulkanBufferHooks.ps1 -Pairs 2
./tools/Test-VulkanBufferHooks.ps1 -Configuration Debug -Pairs 1
./tools/Test-VulkanBufferHooks.ps1 -BuildPath native/build-vulkan-asan -Pairs 1
./tools/Test-ApplicationSessions.ps1 `
  -VkcubePath native/build-vulkan-tools/cube/Release/vkcube.exe `
  -BuildPath native/build-vulkan -Pairs 2 -Frames 120 -SkipCollectorCrash
```

Configure/build all three native configurations first. ASAN commands require the
matching compiler sanitizer runtime on the process-local PATH and
`ASAN_OPTIONS=halt_on_error=1:abort_on_error=1:detect_leaks=0`. Do not distribute
ASAN binaries as ordinary Release artifacts.

## Frozen Artifacts

[Raw sessions and cube helper evidence](traces/vulkan-command-hook.zip)
SHA-256: `c2dd8354a8c2689b2b9568ea556f9e34cf28c2ed27ca5da8ae376a72e8abc745`.

Local tested layer DLL SHA-256 values (not claims of reproducible CI binaries):

- Release: `e1b00000179caf7e3d91e6c0e912fa594142fef5e7e709e5242b3665e3e48582`
- Debug: `c382e594f847ad6faf557d6912e23065800678692eb1df0799adc25d03688570`
- ASAN: `f0b839ef7fdd2dfb5ec367a6c1b6db8dfbd54234d15e743972546647fad4c17b`
- Cube executable: `af8ac60765bd0d489e2de3f40b852332aea02ba92c07acbd7c7d368a05c8be1c`

The archive excludes earlier draft captures. Submitted-copy and replay counters
are partial when coverage is incomplete. Resource validity/content generations,
shader writes, GPU completion and cross-queue dependencies remain outside this
observer. FluidLink and the Gateway native DLL ABI/pin did not change.
