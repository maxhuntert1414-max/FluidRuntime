# STARK-PRODUCTIONS Planner Trace: v0.7

## Assignment

Plan the smallest safe foundation for D3D11 resource destruction, pointer reuse,
and provenance without expanding actuation to external processes.

## Decisions

- Do not hook the shared `IUnknown::Release` vtable in the first lifecycle slice.
- Do not attach `SetPrivateDataInterface` sentinels yet because a live sentinel
  can call code from an unloaded DLL after detach.
- Add an explicit owned-target retirement boundary that never dereferences a
  retired pointer.
- Remove active state, pending maps, destination copy history, and source-linked
  copy history at retirement.
- Never reuse a resource ID; link recent pointer reuse to a fresh monotonic ID.
- Bound retired-pointer history to prevent unbounded memory growth.
- Reconstruct active and retired IDs in the managed runtime and fail closed on
  invalid lifecycle transitions or operations involving retired resources.

## Limits

This slice proves cooperative retirement, not automatic COM destruction. A
future experiment may evaluate destruction sentinels or narrowly scoped Release
observation only with a safe unload and quiescence protocol.

The planner was read-only and did not edit files directly.
