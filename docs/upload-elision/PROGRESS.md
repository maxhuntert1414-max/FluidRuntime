# Upload Elision Progress

## Status

Implementation and local evidence complete on `feature/v0.11-upload-elision`.
Documentation, publication, and remote CI verification remain.

## Completed

- [x] Verified the v0.10.0 baseline: 68/68 managed tests and 7/7 native Release
  tests pass from the valid post-migration build directory.
- [x] Reviewed official D3D11 usage, CPU access, map/unmap, and copy semantics.
- [x] Audited current generation provenance and confirmed writes become visible
  to redundancy decisions only after `Unmap`.
- [x] Chose staging-write-to-default whole-resource copy elision as the first
  narrow upload intervention.
- [x] Defined the action-isolation, equivalence, rollback, WARP, and hardware
  performance gates.

## In Progress

- [ ] Publish the synchronized FluidRuntime and FluidGateway documentation.

## Pending

- [x] Owned native upload workload and self-validation.
- [x] Managed paired runner and report.
- [x] Unit, native, negative-matrix, and CI coverage.
- [x] WARP and RX 580 evidence.
- [ ] FluidRuntime and FluidGateway documentation publication.

## Final Local Verification

- Managed tests: 73/73.
- Native Release: 8/8.
- Native Debug: 8/8.
- Negative policy matrix: 320/320.
- WARP upload raw runs: 4/4, claim blocked.
- RX 580 upload raw runs: 22/22, scoped gate passed.

## Decisions

- Do not hash mapped write memory inside the hook in v0.11.0.
- Do not optimize a copy after a new CPU write, even if application bytes happen
  to be identical.
- Describe `STAGING -> DEFAULT` as an API-visible upload direction, not as proof
  of RAM/VRAM placement or physical bus traffic.
- Preserve physical residency as a blocked manager lane.
- Because D3D11 `CopyResource` submission is asynchronous, gate the positive
  upload claim on strict GPU improvement plus a bounded CPU submission overhead
  envelope instead of claiming CPU acceleration.
