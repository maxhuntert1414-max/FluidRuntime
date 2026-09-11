# Application Sessions

Available in v0.23 source on `main`. This is an experimental Windows x64 launch
integration, not a general game optimizer. Start with an unprotected diagnostic
application. Do not use it in anti-cheat/protected games or work you cannot lose.
The acknowledgement below is operator consent, **not automatic anti-cheat detection**.

## Build And Run

Use a normal, unelevated terminal. Requirements: .NET 10 SDK, MSVC x64, CMake,
Vulkan-capable driver. CMake fetches hash-pinned Vulkan-Headers 1.3.290; no
system-wide layer registration or Vulkan SDK installation is performed.

```powershell
dotnet build FluidRuntime.slnx -c Release -warnaserror
cmake -S native -B native/build -A x64
cmake --build native/build --config Release

dotnet src/FluidRuntime/bin/Release/net10.0/fluidruntime.dll app-session `
  --exe C:\VulkanTools\vkcube.exe --layer-dir native/build/Release `
  --out artifacts/session.json --seconds 10 `
  --acknowledge-no-anticheat true -- --c 300 --use_staging

python -m fluidgateway analyze-app --session artifacts/session.json `
  --out artifacts/session-diagnosis.html
```

Run the last command where current Gateway `main` is installed/importable, or
from its checkout using the absolute session path. `analyze-app` writes HTML
and a sibling JSON diagnosis. Application arguments after `--` are forwarded
without shell parsing. Replace the example path with your explicitly selected
executable. Only x64 PE executables are accepted; this does not verify that an
application uses Vulkan. A session without a verified Vulkan device fails closed.

`--observe-vulkan false` provides a CPU/RAM baseline without enabling our layer.
Keep identical application arguments and alternate baseline/observed order.
Existing layer environment is preserved, not silently disabled. A baseline
refuses an inherited FluidRuntime layer. Loader environment changes apply only
to the launched child; existing processes, registry and machine settings stay
unchanged. Normal loader limitations still apply to launchers and child processes:
only the exact selected executable/PID can own this session's counter mapping.

## Optional Windows Intervention

Add `--priority-seconds 2` to explicitly request a temporary priority change
after module and Vulkan-device verification. Default is zero. Allowed duration
is 1..30 seconds and must fit in the 1..120-second requested capture window.
The lease timer starts after verification, not at process launch.

The independent helper verifies PID, start time, executable SHA-256, same user
and session, noncritical process, and current **Normal** priority. It then sets
**AboveNormal**, confirms it, and restores on expiration or collector stop.
Existing non-Normal policy is refused. If another actor changes priority during
the lease, that change is preserved and reported rather than overwritten.
Windows read/set operations are not a compare-and-swap; this is not an exclusive
scheduler policy against concurrent tools. No High/Realtime, affinity, process
suspension, RAM trimming, power-plan changes or driver actions exist here.

Stopping the collector normally requests restoration. Forced collector exit
still leaves the independent timer alive. **Killing the watchdog itself can
prevent restoration**; inspect the target priority before repeating after such
a failure. Per-lease JSON records requested/applied/restored state. Missing or
failed restoration makes the session fail; it is never reported as a speedup.

Capture stop or Ctrl+C does not forcibly close the selected application.
Counter writes stop, but in-flight updates can finish and the DLL remains
loaded/forwarding with bookkeeping overhead until application exit. Restart
the application without the session for an uninstrumented run.

## Observation Contract

The explicit layer `VK_LAYER_FLUIDRUNTIME_observe` negotiates loader interface 2.
It captures downstream dispatch at object creation and preserves unsupported
extension lookups and downstream return values. The layer never removes or
adds GPU work, barriers, waits, allocations or copies. DLL/target files are
held read-only and the loaded module is checked against the selected hash.
Path/denylist checks and the same-user named mapping are not a sandbox against
a malicious application or administrator.

The 560-byte little-endian shared ABI has four uint32 fields (magic `0x4f564746`,
version 3, size 560, count 66), then aligned int64 owner PID and enable flag,
then 66 atomic int64 counters. The canonical order is
[`vulkan_observation.h`](../native/include/vulkan_observation.h), mirrored and
validated by the managed reader and Gateway importer. There are no names or
JSON payloads in this hot path. JSON is for bounded offline report export.

Fixed capacities: 16 live instances, 64 devices, 8,192 tracked allocations and
8,192 tracked buffers. The allocation/buffer tables together have a compile-time
2 MiB ceiling and reuse fixed slots without allocating on their lookup/churn path.
Command recording adds 1,024 pools, 4,096 command buffers and 64 secondary
references per recording; combined resource/command tables stay below 8 MiB.
One submit call can attribute at most 512 primary/secondary occurrences. These
new limits report incomplete coverage while forwarding the original call.
Instance/device exhaustion returns an explicit out-of-host-memory creation
error; this is an instrumentation compatibility limit, not the driver's actual
capacity. Allocation overflow does not block the application; it increments
`untracked_allocations` and makes live/peak-byte coverage partial. Counters use
independent atomic operations, not a coherent multi-field snapshot.

The resource hook observes `vkCreateBuffer`/`vkDestroyBuffer`, allocation lifetime,
legacy/core/KHR buffer binds and legacy/core/KHR buffer copies. Bindings retain an
allocation generation; freeing and recycling a numeric handle cannot revive an
old binding. Resources are retired before downstream destruction. Tracking is
locked, while driver calls always execute outside that lock.

Recorded buffer-copy bytes are classified by the bound memory type: host-visible
only to device-local only, reverse, device-to-device, host-to-host, either endpoint
with both flags, or unknown. These names do **not** measure physical RAM/VRAM
placement. Same-allocation bytes are an additional, overlapping counter, not a
redundancy finding. No buffer contents are read or retained by the observer.

Unknown allocation/create/bind/copy extension chains, sparse/protected buffers,
missing records and unmodeled binds remain unclassified. Failed bind2 batches
invalidate attribution for all supplied buffers, including possible partial
success. At capacity, the application proceeds and coverage counters increase.
Counters saturate rather than wrap; `counter_overflows` marks partial totals.

Runtime now exports `fluidruntime-application-session-v3`. Use the matching DLL
and managed collector; mixed shared ABI versions cannot produce a verified
session. Gateway `main` imports v1 (32 counters), v2 (46 counters) and v3 (66 counters)
without inventing new evidence for old captures. FluidLink v2 and the Gateway
DLL C ABI are unchanged.

V3 distinguishes recorded copies from copies attributed to successful queue
submissions. It tracks command allocation/free, begin/end, buffer/pool reset,
generation-bound secondary references and replay. A failed or unresolved submit
never contributes a partial copy total. See [command observation](vulkan-command-observation.md)
for the exact bounds, counter semantics and unmodeled cases.

- Recorded buffer copy bytes are not executed bytes: command buffers can be
  replayed, discarded or never submitted. Images have counts, not byte estimates.
- Submitted bytes are accepted, attributed work, not proof of GPU completion,
  valid resource contents, physical traffic or redundant work. No pending-state,
  fence-completion or cross-queue dependency model is inferred from these totals.
- Allocation bytes are requested sizes, not physical VRAM residency, saved RAM
  or measured PCIe traffic. Host-visible and device-local flags can overlap.
- Present counts are API calls, not displayed FPS or input latency. Negative
  VkResult counts include recoverable conditions such as out-of-date swapchains.
- CPU/RAM sampling is approximately every 250 ms, not continuous. Final CPU/RAM
  values reuse the last live sample; short spikes and
  shutdown CPU time may be absent. Vulkan final counters are read separately.
- Process exit wakes the collector between sampling ticks. Capture duration stops
  before watchdog cleanup and is not a frame-latency or exact application benchmark.
  A pre-cancelled session does not launch the selected application.
- Coverage includes legacy buffer/image copies, buffer-copy2, bind2, barrier2
  and submit2 core/KHR aliases. Newer image-copy2/map2, sparse/external memory,
  complete image layouts, buffer content/write generations, shader writes and
  queue-family provenance are not modeled. Allocation lifetime generations are
  not content provenance. `telemetry_failures` is reserved, not a promise
  that all missing coverage can be detected.

## Validation

```powershell
dotnet test FluidRuntime.slnx -c Release -warnaserror
ctest --test-dir native/build -C Release --output-on-failure
./tools/Test-ApplicationSessions.ps1 -VkcubePath C:\VulkanTools\vkcube.exe `
  -BuildPath native/build -Pairs 4 -Frames 120
./tools/Test-VulkanBufferHooks.ps1 -BuildPath native/build -Pairs 2
```

The script is only for a disposable, explicitly supplied Khronos cube binary.
It tests AB/BA input identity, counts, natural teardown, normal lease restoration
and forced collector termination. It never runs that destructive fault test
against arbitrary application names. Native mock tests require no Vulkan ICD.
Hardware validation and ASAN coverage are recorded separately in
[the evidence report](evidence/v0.23.0-application-integration.md).
The new resource-hook checks and raw v2 sessions are in
[the buffer-hook evidence](evidence/vulkan-buffer-hook.md).
Command recording/submission validation is recorded in
[the command-hook evidence](evidence/vulkan-command-hook.md).

Primary contracts: [Khronos loader/layer interface](https://github.com/KhronosGroup/Vulkan-Loader/blob/main/docs/LoaderLayerInterface.md)
and [Windows SetPriorityClass](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setpriorityclass).
