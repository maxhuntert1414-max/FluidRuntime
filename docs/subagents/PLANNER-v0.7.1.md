# STARK-PRODUCTIONS Planner Trace: v0.7.1

## Assignment

Review an opt-in D3D11 Release-hook design for automatic destruction observation
without compromising detach, rollback, or DLL unload.

## Decisions

- Keep normal `FluidHookAttach` free of Release interception.
- Patch only Release slots observed from owned-target Buffer/Texture2D creation.
- Store one mandatory original per dynamic slot and reject missing originals.
- Never hold FluidRuntime locks while calling the original Release.
- Remove destroyed state through the same provenance and ABA-history path used
  by cooperative retirement.
- Restore all dynamic slots and drain active calls before clearing the registry
  or unloading the DLL.
- Limit the first claim to the same returned interface identity.
- Treat the Release return count as owned-lab evidence only.

## Risks

- Interface aliases may use a different vtable and are not covered.
- Shared vtables also forward untracked objects through the hook.
- Automatic observation remains unsuitable for external targets until alias,
  race, and long-session tests pass.

The planner was read-only and did not edit files directly.
