# Vulkan Command Observation

The opt-in application layer now distinguishes **recorded** buffer-copy commands
from **attributed successful submissions**. It still forwards original calls;
this feature cannot authorize GPU copy elision or application interference.
Use the launch procedure in [application sessions](application-sessions.md).

## Lifetime Model

The hooks cover command-pool creation/destruction/reset, command-buffer
allocation/free, begin/end/reset, `vkCmdExecuteCommands`, and queue submit
legacy/core/KHR entry points. The existing buffer-copy hooks feed recording
summaries. Primary and secondary recordings have monotonically increasing
generations; reset, rerecording and handle reuse cannot revive an older link.
Pool destruction retires its records before entering the downstream driver.

Each recording stores direct copy-call/byte totals and generation-bound secondary
references. Submission resolves those references, including repeated occurrences.
Nested secondary execution is deliberately unresolved. This is a limited
recording provenance model, not a Vulkan validation layer or a complete model of
resource dependencies, barriers, shader writes and GPU execution.

Before the downstream submit, a fixed snapshot captures totals and generation
tokens under the state lock. No driver call runs under that lock. On `VK_SUCCESS`,
all tokens are revalidated before updating any submitted totals or replay state.
A failure, changed generation, unknown recording or exceeded bound contributes
no partial attributed vector. The original call and return value are preserved.

## Counters

| Counter | Meaning |
| --- | --- |
| `buffer_copies`, `buffer_copy_bytes` | Copy API calls and region bytes at recording time; unchanged v2 semantics |
| `successful_submit_calls`, `failed_submit_calls` | Queue calls returning success or another result; one count per API call, not per submit-info entry |
| `submitted_primary_command_buffers` | Primary occurrences in fully attributed successful calls |
| `submitted_secondary_command_buffers` | Secondary occurrences expanded from those primaries, including repeated references |
| `resubmitted_command_buffers` | Occurrences of a generation already used in attributed successful calls, including within the same call |
| `submitted_buffer_copies`, `submitted_buffer_copy_bytes` | Direct and secondary copy calls/bytes multiplied by attributed submission occurrences |
| `unresolved_submit_calls` | Successful calls excluded from attributed totals |
| `command_tracking_failures` | Recording/lifecycle operations the model could not retain or understand |
| `command_tracking_overflows` | Submit snapshot capacity or aggregate arithmetic overflow |
| `untracked_command_buffers`, `untracked_command_pools` | Successful allocations/creations absent from fixed tracking tables |
| `live_command_buffers` | Tracked live handles, a decreasing gauge rather than a cumulative counter |

Unknown calls do not update replay state. Consequently replay and submitted-copy
totals are partial whenever coverage is incomplete. A recording never submitted
may contribute recorded bytes but no submitted bytes. Replay may produce more
submitted bytes than recorded bytes; neither difference proves waste.

## Bounds and Exclusions

- Fixed tables: 1,024 command pools, 4,096 command buffers, 64 secondary references
  per recording. Together with the existing buffer/allocation tables: <= 8 MiB,
  checked at compile time. No per-event history or allocation in these paths.
- Per submit: at most 512 expanded primary/secondary occurrences and 512 submit
  info entries. The snapshot is <= 12 KiB on the stack, with no per-submit heap
  allocation. Counters saturate instead of wrapping.
- Unknown command allocate/begin/inheritance chains, protected/unmodeled pool
  flags, submit chains/flags, nontrivial submit2 device masks, nested secondaries,
  stale generations and exceeded limits make coverage incomplete. Legacy timeline
  semaphore submit chains are currently unresolved rather than silently ignored.
- A one-time recording cannot be attributed twice. For other recordings, this
  observer does not validate pending/completed state or simultaneous-use legality.
  Successful submission is **not** GPU completion, resource validity or content
  equivalence. Per-queue ordering, fence/semaphore completion and buffer-content
  generations remain future work.
- Submitted byte totals do not recompute memory categories: the v2 category
  counters still describe binding flags at recording time. Images have no byte
  attribution. No claim of physical RAM/VRAM residency, PCIe traffic or FPS gain.
- ABI 3 is 560 bytes / 66 counters. JSON sessions use v3; Gateway accepts v1/v2/v3.
  FluidLink wire contracts and the Gateway DLL C ABI are unchanged.

Reference: [Khronos command-buffer lifecycle](https://docs.vulkan.org/spec/latest/chapters/cmdbuffers.html).
Validation and artifacts: [command-hook evidence](evidence/vulkan-command-hook.md).
