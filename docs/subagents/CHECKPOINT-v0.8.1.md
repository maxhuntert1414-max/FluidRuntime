# v0.8.1 Interrupted Checkpoint

Captured on 2026-07-16 because the workstation needed to shut down.

## Git state

- Branch: `wip/v0.8.1-policy-matrix-checkpoint`
- Base: `21bbc5f` (`v0.8.0`)
- Publication: none; `main`, `origin/main`, and tag `v0.8.0` were not changed

## Implemented so far

- Eight managed control-policy cases and raw owned-lab publication.
- Native target parsing and expected-state validation for the matrix cases.
- Explicit `stdin` gates for publication and accepted-then-expired ordering.
- Managed matrix options, command, runner, raw evidence model, and normalized
  determinism projection.
- Fixed intended matrix: 8 cases x 20 repetitions x Release/Debug WARP.

## Verification at interruption

- Managed Release build: passed with 0 warnings and 0 errors.
- Native build: not run because `cmake`/`msbuild` were unavailable on this
  terminal's PATH. The native changes remain unverified.
- No matrix execution, unit-test suite, trace, review, docs release update, tag,
  or push has been completed.

## Resume gate

1. Wire `control-policy-matrix` into `Program.cs` and help output.
2. Build native Release/Debug with the configured Visual Studio/CMake toolchain.
3. Fix compile/contract issues and add unit tests.
4. Run all managed/native tests and the full 320-process matrix.
5. Capture evidence, run reviewer/debugger loop, update Runtime/Gateway docs,
   then publish only after every gate is green.
