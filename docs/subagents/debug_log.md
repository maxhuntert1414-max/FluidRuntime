# v0.8.0 Debug Log

- Expiry TOCTOU -> second QPC check after reservation, rollback, monotonic status.
- Provenance/action race -> serialized policy-enabled hook operations.
- Delayed entry after unload -> pin module and retain forwarding state.
- Truncated mapping access -> require exact capacity and minimum view size.
- Mixed control snapshot -> bounded stable double-read, fail closed.
- Publisher race -> atomic owner sentinel.
- Stale create after detach -> all retained entrypoints pure-forward when inactive.
- Unsafe reattach generations -> reject every later attach in the process.

Every blocking issue returned through REVIEWER -> DEBUGGER/implementation ->
REVIEWER until the independent final GO.
