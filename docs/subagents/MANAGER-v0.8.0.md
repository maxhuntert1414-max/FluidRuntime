# STARK-PRODUCTIONS Manager Trace: v0.8.0

## Assignment

Audit release readiness across source freeze, versioning, tests, traces,
documentation, FluidGateway compatibility, GitHub publication, and remote CI.

## Initial Decision

NO-GO while v0.8.0 was an uncommitted moving worktree with stale traces and no
CI on the candidate source.

## Exit Criteria

- One frozen candidate diff and no scratch files in the commit.
- Managed Release, native Release/Debug, manager Release/Debug, and exact CI
  predicate passing.
- WARP and RX 580 traces regenerated from final binaries and hashed.
- Performance claim blocked where evidence does not support it.
- Runtime and Gateway documentation committed and remote CI green on exact SHAs.

## Final Reaudit

GO for candidate commit and push. The manager confirmed final trace hashes,
version/ABI alignment, exact local CI predicates, STARK documents, and the
Runtime-first publication order. Tagging and final release publication remain
blocked until GitHub Actions is green on both exact pushed SHAs.

The manager was read-only and did not edit files.
