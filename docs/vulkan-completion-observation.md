# Vulkan Completion Observation

The opt-in layer separates **recorded**, **accepted and attributed**, and
**driver-reported completed** buffer-copy totals. This is observation, not a new
permission to remove work. Use [application sessions](application-sessions.md).

## Model

Each tracked queue holds cumulative submitted/completed totals. A fence stores
a queue-prefix marker and a monotonic payload generation. A successful submit
updates its prefix only after the driver returns and command generations are
validated. Even an unresolved or empty submit may fence earlier known work;
unknown work never contributes invented copy bytes.

`vkGetFenceStatus == VK_SUCCESS`, a successful single-fence wait, or successful
`vkWaitForFences(waitAll=true)` can confirm captured prefixes. A later fence on
the same queue includes earlier submissions; reporting an older fence afterwards
does not count them twice. `vkQueueWaitIdle` and `vkDeviceWaitIdle` confirm only
queue markers captured before the application's existing wait.

The layer inserts **no waits, polls, fences, barriers or driver calls**. Driver
calls run outside the state lock. Tokens are captured before and validated after
driver calls. Reset and submit invalidate old fence associations before forwarding,
even if the call fails. Destroy/recreate cannot revive a token. A host wait that
races the tail of a submit wrapper may miss new evidence; a later observed wait
can recover it. It never adopts a newer association after the wait returns.

## Bounds and Conservative Exclusions

- 256 queue records and 2,048 fence records, shared across devices. Queue slots
  retire at device destruction; fence slots retire at destruction. No history
  grows with submissions. Existing resource/command plus completion tables have
  an 8 MiB compile-time ceiling. No heap allocation on these tracking paths.
- Wait snapshots hold at most 64 fences; larger calls contribute no completion
  evidence. Device-idle snapshots hold at most 256 queue markers / 10,240 bytes
  on Windows x64.
- Successful wait-any with multiple fences is ambiguous. It forwards unchanged,
  records the gap, and does not query individual fences or infer completion.
  Timeout, not-ready and error returns do not confirm anything.
- Unknown fence creation chains/flags are untracked. Enabling
  `VK_KHR_external_fence_win32` or `_fd` excludes **all fences on that device**:
  later import/export can change a payload outside this process. Idle observations
  remain available. An initially signaled fence has no submission association.
- Enabling `VK_KHR_internally_synchronized_queues` excludes completion attribution
  for that device. Queue-prefix order here relies on the application's ordinary
  Vulkan external synchronization, not driver return order under concurrent calls.
- Arithmetic overflow and table exhaustion report coverage failures without
  altering the original call. Generations never wrap into trusted identities.
- Destruction retires outstanding observations as abandoned, not completed.
  Missing confirmation does not mean unfinished GPU work or a stalled application.

## ABI and Counters

Shared observation ABI **4**, **672 bytes**, **80 counters**. Existing indices
0..65 retain their meanings. The managed collector must match the DLL exactly.
The JSON session is `fluidruntime-application-session-v4`; Gateway `main` also
imports historical v1/v2/v3 captures without inventing completion fields.
FluidLink, Gateway native EXE/DLL ABI, release tags and binary pin are unchanged.

| Counter | Meaning |
| --- | --- |
| `fences_created`, `fences_destroyed` | Successful creations and non-null destroy calls, including untracked fences |
| `live_fences`, `untracked_fences` | Tracked-live gauge and successful creations excluded by limits/extensions |
| `fence_resets`, `fence_status_queries`, `device_waits` | Original API call counts, including failures |
| `ambiguous_fence_waits` | Successful multi-fence wait-any calls without attribution |
| `completion_tracking_failures` | Queue tracking/arithmetic exclusions or successful wait-all above the snapshot bound |
| `completed_submit_calls` | Fully attributed accepted calls in newly confirmed queue prefixes |
| `completed_buffer_copies`, `completed_buffer_copy_bytes` | Attributed copy calls/bytes in those prefixes, including replay |
| `pending_tracked_submits` | Gauge of attributed queue calls without observed completion |
| `abandoned_tracked_submits` | Pending observations retired when their device is destroyed |

Counters are independently atomic and saturating, not a coherent snapshot.
Do not validate cross-counter equalities at arbitrary sample times. Terminal
owned-target tests can check exact equalities after all calls have finished.

## Not Proven

Driver-reported completion does not validate resource contents, visibility to
host memory, shader/host writes, aliases, cross-queue dependencies or physical
transfer traffic. Vulkan can return successful fence status even after device
loss; negative-result counters must be considered and completion is never proof
of successful computation. No timestamp duration, FPS gain, saved bytes or
third-party elision authority is derived from these counters.

The next correctness layer is content/write generations and dependency evidence,
not removing synchronization solely because a fence eventually signaled.

## Validation

Native unit/mock dispatch tests cover prefix deduplication, queue/device isolation,
stale/reset/reused fences, failure and timeout paths, wait-any, oversized wait-all,
extension opt-outs, integer extremes and 10,000 bounded lifecycle iterations.
See [completion evidence](evidence/vulkan-completion-hook.md) for actual build and
GPU runs, rather than treating test presence as execution evidence.

Primary contracts: [fence synchronization](https://docs.vulkan.org/spec/latest/chapters/synchronization.html),
[wait-all/any](https://docs.vulkan.org/refpages/latest/refpages/source/vkWaitForFences.html),
[queue idle](https://docs.vulkan.org/refpages/latest/refpages/source/vkQueueWaitIdle.html),
[device idle](https://docs.vulkan.org/refpages/latest/refpages/source/vkDeviceWaitIdle.html),
[status and device loss](https://docs.vulkan.org/refpages/latest/refpages/source/vkGetFenceStatus.html),
[internally synchronized queues](https://docs.vulkan.org/refpages/latest/refpages/source/VK_KHR_internally_synchronized_queues.html).
