# Security Policy

## Supported Line

FluidRuntime is pre-1.0 research software. Security fixes target the latest
release and `main`.

## Reporting

Use GitHub private vulnerability reporting or a private Security Advisory.
Include the affected version, Windows build, reproduction, expected impact,
and whether the issue crosses a process or privilege boundary.

## Execution Boundary

Native commands require explicit target and hook paths. Managed launchers own
the processes they start, bind their paths and SHA-256 identities, terminate
the full owned process tree on cancellation or timeout, and write final reports
through same-directory atomic replacement. These controls are not permission
to inject into third-party software, bypass anti-cheat or DRM, or test systems
you do not own.

FluidLink is loopback-only. Gateway identity is pinned by PID, executable hash,
process start time, and the Windows TCP owner table before owned-lab policy is
accepted. The protocol is not intended to cross a machine or privilege
boundary.

The software remains experimental. Run native paths only against the included
owned targets and retain baseline evidence for comparison.
