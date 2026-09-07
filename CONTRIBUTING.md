# Contributing to FluidRuntime

FluidRuntime coordinates explicit, bounded controls. Diagnostics and decisions
belong to the separate [FluidGateway](https://github.com/maxhuntert1414-max/FluidGateway)
repository. Runtime owns identity checks, native execution, expiry and rollback.

## Build and Test

Install the .NET 10 SDK. Native work additionally needs Visual Studio C++ Build
Tools, CMake and the Windows SDK; Vulkan builds need the Vulkan SDK.

```powershell
dotnet test FluidRuntime.slnx -c Release -warnaserror
cmake -S native -B native/build -A x64
cmake --build native/build --config Release
ctest --test-dir native/build -C Release --output-on-failure
```

Read [native Gateway integration](docs/native-gateway.md) before changing the live
authorization path. Owned GPU labs are separate from third-party applications;
passing an owned lab does not establish general game compatibility or FPS gains.

## Code Map

- `src/FluidLink`: wire codec, client and strict contract validation.
- `src/FluidRuntime/Cli`: arguments and command boundaries.
- `src/FluidRuntime/Runtime`: coordination, identity and authority checks.
- `native/include`, `native/src`: cooperative native libraries and targets.
- `tests/FluidRuntime.Tests`: managed regressions and negative controls.
- `tools`: opt-in integration scripts and development-only benchmarks.

## Readability and Review

Follow the checked-in EditorConfig: four spaces, explicit blocks and consistent
line breaks. Keep changes scoped; do not reformat unrelated modules. Use
`dotnet format whitespace FluidRuntime.slnx --include <changed-files>` for touched
C# files and `--verify-no-changes` to check them. The development benchmark is a
separate project under `tools/GatewayComparison` and should be checked separately.

Explain resource lifetime, synchronization and fail-closed invariants where they
are not obvious. Prefer descriptive identifiers and readable statements to dense
one-line control flow. Separate formatting from behavioral commits when possible.

For a PR, include the failing case, the regression test and the commands/results
you verified. Native changes require Release, Debug and ASAN validation, including
content equivalence and original-execution fallback. Never remove PID/hash checks,
broaden process authority, suppress a failing gate or claim performance from a
single favorable run. Publish and verify a Gateway release before updating its
pin in this repository. Contributions use the repository's MIT license.
