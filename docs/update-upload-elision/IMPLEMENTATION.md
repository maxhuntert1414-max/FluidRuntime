# UpdateSubresource Elision Implementation

## Native Contract

- [x] Ring ABI 9 and snapshot ABI 12.
- [x] Attach-options ABI 3 with one-resource / 4 MiB cache bounds.
- [x] Content-compared event flag.
- [x] Dedicated action 8; unknown action moved to bit 16.
- [x] Direction-specific observed, candidate, forwarded, skipped, and cache
  counters.

## Hook Path

- [x] Restrict eligibility to one owned full default buffer.
- [x] Copy source bytes only on first/new content; compare repeats exactly.
- [x] Require destination generation equality.
- [x] Reserve policy under the provenance lock.
- [x] Preserve generation on skips and advance it on real updates.
- [x] Erase cache on resource retirement and detach.

## Adversarial Workload

- [x] Required A upload plus 32 A repeats.
- [x] One-bit B mutation plus 16 B repeats.
- [x] External C `CopyResource` write.
- [x] Required B re-upload plus 16 B repeats.
- [x] Exact final readback and distinct A/B/C hashes.

## Managed Control And Evidence

- [x] `update-upload-elision-lab` command, options, runner, and report.
- [x] Alternating paired order and excluded warmups.
- [x] Exact event-generation pattern validation.
- [x] Dedicated policy publication and unit tests.
- [x] Manager lane `ram-gpu-direct-update`.
- [x] CI evidence contract and negative matrix.
- [x] WARP and RX 580 traces.

## Rollback

The action remains one epoch, one exact action bit, at most 128 reservations,
and at most four seconds. This release uses 64. Detach restores all patched
slots after draining active callbacks and clears retained content.
