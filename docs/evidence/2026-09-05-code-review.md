# Runtime Review And Regression Evidence

Review baseline: main `5f25677`. See the
[paired review report](https://github.com/maxhuntert1414-max/FluidGateway/blob/main/docs/code-review-2026-09-05.md)
for findings, Gateway provenance/dependency corrections and unresolved risks.

## Runtime Corrections

- Pre-cancelled probes/application sessions stop before filesystem work and launch.
  Application preparation checks cancellation again immediately before launching.
- Native probe stdout/stderr reads share the process deadline, including when a
  descendant inherits the pipes. The regression creates a ten-second pipe holder,
  checks the probe's own two-second timeout, and cleans up the owned descendant.
- Application sampling waits for process exit or the remaining sampling window;
  it no longer deliberately rounds exit detection to the next 250 ms poll.
- Capture counters and the capture stopwatch stop before watchdog cleanup.
  Ending capture cancels/detaches the process-exit waiter without killing the app.
- CPU/RAM terminal-sample and collector-duration limitations are explicit in JSON.

Wire contracts, the pinned Gateway tag, native ABI and source versions are
unchanged. No native C++ refactor or new authority was needed for these fixes.
The new Gateway diagnosis/provenance fixes require current Gateway main.

## Local Validation

| Gate | Result |
| --- | --- |
| Managed tests, Release, warnings as errors | 250/250, previously 245 |
| Paired Gateway tests | 294/294, previously 278 |
| Native Release / Debug / ASAN | 32/32 each |
| Python/.NET FluidLink interop | Passed, matching base/batch contracts and vectors |
| D3D11 UpdateSubresource integration, WARP | Two pairs + invalid/stall/slow fallback passed |
| D3D12 transfer integration, WARP | Two pairs + invalid/stall/slow fallback passed |
| Vulkan cooperative transfer integration, hardware | Two pairs + invalid/stall/slow fallback passed |
| Real vkcube launch observation | 120-frame AB/BA, counters and teardown passed |
| Priority restoration | Normal after one-second lease and forced collector exit |
| Gateway application-session import | HTML/JSON generated from the new priority trace |

The controlled owned runs each omitted 128 exact redundant API operations
(536,870,912 logical bytes). Fallbacks omitted none. These correctness checks
do not prove reduced physical PCIe traffic, game FPS or lower input latency.
ASAN leak detection was disabled; passing ASAN is not proof of leak freedom.

Real application collector durations: AB 2,433.5681/2,347.8575 ms, BA
2,318.7639/2,353.0879 ms (baseline/observed). The direction changed between pairs;
no performance conclusion is justified. Normal lease restoration was
1,003.6358 ms; collector-crash watchdog restoration was 2,003.7267 ms.

Evidence prefixes: `artifacts/review-d3d11*`, `review-d3d12*`, `review-vulkan*`,
`review-app*`; interop report `artifacts/fluidlink-cross-process.json`.
Original v0.23 published traces remain unchanged. These new local artifacts
are regression evidence, not a release benchmark or independently published dataset.

## Remaining Work

Gateway sessions still retain unbounded history: sustained operation needs a
retention/rollup contract and long soak tests. The advisory planner requires
complete write information; it is not a byte-provenance oracle. Third-party
Vulkan remains observation-only, and broader real-engine compatibility and
overhead must be measured before widening native intervention.
