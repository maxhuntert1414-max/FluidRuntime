# Project Status

FluidRuntime v0.13.0 is a locally verified release candidate as of 2026-07-29.
The v0.12.0 native actuation evidence remains unchanged.

## Public Release

- Candidate branch/tag: `main` / `v0.13.0`
- FluidLink workflow: [GitHub Actions](https://github.com/maxhuntert1414-max/FluidRuntime/actions/workflows/fluidlink.yml)
- Runtime workflow: [GitHub Actions](https://github.com/maxhuntert1414-max/FluidRuntime/actions/workflows/ci.yml)
- Release target: [FluidRuntime v0.13.0](https://github.com/maxhuntert1414-max/FluidRuntime/releases/tag/v0.13.0)
- The status will be marked remote-verified only after both workflows and the
  public release are confirmed.

## Local Release Gate

- Managed tests: 93/93 passed.
- FluidLink .NET tests: 14/14 passed.
- FluidGateway complete suite: 222/222 passed.
- FluidGateway FluidLink tests: 23/23 passed.
- Cross-process Python/.NET probe: 11/11 round trips passed.
- Contract file and negotiated SHA-256:
  `10b46685472d13d2d49cc81aa1f7df2d654c1ec53fdc666e086e0d062ad114fa`.
- FluidLink frame bytes: 3,189 versus 6,570 equivalent JSON-envelope bytes,
  a 51.46% reduction for the same synthetic semantics.
- Native tests: 9/9 Release and 9/9 Debug passed.
- Negative control-policy matrix: 320/320 WARP processes passed.
- Exact local CI evidence contract: passed.
- Feature and `main` remote CI evidence contracts: passed.
- WARP update-upload trace: 4/4 raw runs passed; claim blocked as intended.
- RX 580 update-upload trace: 22/22 raw runs passed; scoped gate passed.
- Generic, manager, sustained, readback, and staging-upload regression smokes
  passed after ABI and hook changes.
- Raw WARP, RX 580, and policy-matrix traces are committed with the source.

The native lines above are the unchanged v0.12.0 release gate. Version 0.13.0
does not modify native source, ABI, hook policy, or hardware claims.

## New In v0.13.0

- `FluidLink` 0.1.0 packages the typed loopback .NET client and binary codec.
- The 56-byte little-endian header carries numeric message, event, and decision
  opcodes, sequence, correlation IDs, session ID, flags, and payload length.
- Dynamic data remains a strict JSON object bounded to 1 MiB, depth 64, finite
  numbers, and explicit UTF-8 limits for handshake/control strings.
- Hello/Welcome negotiate the exact cross-repository contract fingerprint and
  required capabilities before accepting runtime events.
- The client serializes concurrent round trips and invalidates the connection
  on framing, heartbeat, session, subject, sequence, or correlation drift.
- The Gateway keeps raw JSONL as an isolated compatibility mode.
- The probe proves one executed upload intent followed by decision opcode `2`
  and `executed=false` for the modeled duplicate.
- FluidLink is advisory and is not authority for the native control block.

## New In v0.12.0

- Ring ABI 9 and snapshot ABI 12 add content-compared update events and exact
  observed/tracked/candidate/forwarded/skipped/cache counters.
- Attach-options ABI 3 bounds retained source content to one 4 MiB resource.
- Control action bit 8 is dedicated to repeated full-buffer
  `UpdateSubresource`; unknown action moved to bit 16.
- `update-upload-elision-lab` performs 67 direct 4 MiB uploads: three required
  and 64 redundant.
- A one-bit A-to-B change proves content mismatch forwarding.
- An intervening C `CopyResource` write proves generation invalidation before B
  is re-uploaded.
- Exact `memcmp` authorizes a candidate; FNV-1a hashes only label evidence.
- Baseline runs forward all 67 direct updates. Optimized runs forward three and
  skip 64, avoiding 268,435,456 logical source bytes.
- The manager exposes `ram-gpu-direct-update` as active only in the owned lab;
  physical `ram-vram-residency` remains blocked.

## Evidence Claim

Positive scope:

`owned-d3d11-default-buffer-full-update-subresource-exact-content-workload-only`

Claim basis:

`gpu-interval-improvement-with-bounded-cpu-content-comparison-overhead`

AMD Radeon RX 580 2048SP, LUID `000000000000d8c9`:

| Metric | Baseline p50 | Optimized p50 | Baseline p95 | Optimized p95 |
| --- | ---: | ---: | ---: | ---: |
| CPU workload QPC | 309,334.000 us | 82,718.050 us | 333,514.890 us | 89,121.825 us |
| GPU timestamp interval | 260,434.700 us | 2,500.480 us | 275,276.644 us | 3,213.016 us |

CPU and GPU each favored optimized in 10/10 measured pairs. CPU paired
p50/p95 deltas were -73.442% and -67.795%; GPU paired p50/p95 deltas were
-99.046% and -98.831%. Every CPU pair stayed inside the predeclared
+1,000 us / +10% regression envelope.

The GPU value is a disjoint-guarded interval around the owned workload, not GPU
busy. These measurements do not prove physical RAM/VRAM placement, PCIe bytes,
FPS, power, texture/partial uploads, external-game support, or a general cache.

## Operating Level

FluidRuntime can inspect process/GPU/memory telemetry, observe an owned D3D11
resource pipeline, publish bounded managed policies, and interfere reversibly
with four owned-lab patterns: generic `CopyResource`, default-to-staging
readback, staging-to-default upload, and exact full-buffer `UpdateSubresource`.
It can also exchange bounded advisory events and compact decisions with
FluidGateway through the binary FluidLink control transport.

It still does not inject into external games, schedule OS threads, control
physical RAM/VRAM residency, actuate presentation, or support D3D12/Vulkan.

## Read Next

- [v0.13.0 FluidLink evidence](evidence/v0.13.0-fluidlink-binary-interop.md)
- [v0.12.0 evidence](evidence/v0.12.0-update-upload-elision.md)
- [Architecture](architecture.md)
- [Roadmap](roadmap.md)
- [Full handoff](BRIEFING-CLAUDE-CODE.md)
- [v0.11.0 evidence](evidence/v0.11.0-upload-elision.md)
