# Project Status

FluidRuntime v0.15.0 is verified locally as of 2026-08-01.
It connects live FluidGateway decisions to the existing bounded v0.12 native
action without changing the hook ABI or its exact-content safety gate.

## Release Target

- Target branch/tag: `main` / `v0.15.0`
- Canonical Gateway contract: [FluidGateway v0.64.0](https://github.com/maxhuntert1414-max/FluidGateway/releases/tag/v0.64.0)
- Target release: [FluidRuntime v0.15.0](https://github.com/maxhuntert1414-max/FluidRuntime/releases/tag/v0.15.0)
- FluidLink workflow: [GitHub Actions](https://github.com/maxhuntert1414-max/FluidRuntime/actions/workflows/fluidlink.yml)
- Runtime validation: [GitHub Actions](https://github.com/maxhuntert1414-max/FluidRuntime/actions/workflows/ci.yml)

## Local Release Gate

- Managed tests: 133/133 passed.
- Focused FluidLink/Gateway/process-binding tests: 54/54 passed.
- FluidGateway complete suite: 242/242 passed.
- FluidGateway FluidLink tests: 43/43 passed across v1 and v2.
- Cross-process Python/.NET probe: 11/11 v1 and 11/11 v2 round trips passed.
- Contract file and negotiated SHA-256:
  `0d24d96aec32d74e123f9e198e51adde74ddf190e8c40b0ac18bddf5c4108b2f`.
- FluidLink frame bytes: 3,189 for v1 versus 1,880 for v2, saving 1,309
  bytes or 41.05% for the same cross-process semantic flow.
- `FluidLink.0.2.1.nupkg` inspected with the DLL, README, v1/v2 contracts,
  and v2 golden vectors present.
- Native tests: 9/9 Release and 9/9 Debug passed.
- Negative control-policy matrix: 320/320 WARP processes passed.
- Exact local CI evidence contract: passed.
- Remote CI is verified after the release candidate is pushed.
- WARP update-upload trace: 4/4 raw runs passed; claim blocked as intended.
- RX 580 update-upload trace: 22/22 raw runs passed; scoped gate passed.
- Generic, manager, sustained, readback, and staging-upload regression smokes
  passed after ABI and hook changes.
- Raw WARP, RX 580, and policy-matrix traces are committed with the source.

The v0.15 closed-loop gate also passed:

- live FluidGateway 0.64.0 authorization over FluidLink v2;
- exact IPv4 loopback tuple bound through the Windows TCP owner table to the
  caller-supplied expected Gateway PID and executable SHA-256;
- target and hook opened before authorization with write/delete sharing denied,
  then matched against the launched process before policy publication;
- 11 hardware authorization sessions, 814 round trips, and 704 exact candidate
  decisions;
- 64 native skips and 268,435,456 logical bytes avoided per optimized run;
- 22/22 RX 580 raw runs with content, guard, accounting, and rollback agreement;
- malformed-response, accepted-connection stall, and valid slow-response
  controls each produced a new baseline with 70 forwarded calls, zero skips,
  and zero policy fields; the slow peer completed two RTTs before the 500 ms
  total deadline;
- freshly configured native Release build: 9/9 CTests;
- closed-loop performance claim blocked because authorization is outside the
  measured native interval.

The v0.15 code does not modify native source or ABI. It adds a fail-closed bridge
that may publish the existing action-8 policy only after exact live decisions.

## New In v0.15.0

- `gateway-update-upload-lab` asks a live FluidGateway for one seed decision and
  64 duplicate-upload decisions before every optimized owned run.
- Authorization requires an OS-verified expected peer process, exact contract,
  capabilities, heartbeat, session, pair/phase, opcode, execution state, action
  mask, and budget. The server name remains advertised metadata.
- The authorization maps to one short-lived action-8 epoch with budget 64; the
  hook's destination generation and full 4 MiB `memcmp` remain the final gate.
- A context SHA-256 binds nonce, peer PID/hash/start time, frozen target/hook
  hashes, pair/phase, resource size/count, action mask, and budget. It is carried
  in the Runtime session and operation reason.
- Target executable and hook DLL SHA-256, peer/target/ring PID evidence,
  published policy mask/budget/expiration, FluidLink bytes, deadline progress,
  and latency are recorded.
- Gateway timeout is distinguished from caller cancellation. A timeout or
  malformed response occurs before optimized target launch and runs a verified
  baseline.
- The adversarial integration harness uses malformed, stalled, and valid-but-
  slow peers. One linked deadline covers connect, verification, and all 74 RTTs.
- `PeerProcessBindingVerified=true` does not mean cryptographic authentication;
  the report explicitly records `PeerCryptographicallyAuthenticated=false`.
- Closed-loop `PerformanceClaimAllowed` is always false until Gateway decision
  time is inside an end-to-end measurement and meets a production latency gate.
- FluidLink package 0.2.1 adds read-only connected endpoint inspection. No wire
  FluidLink v2 or native ABI change was required.

## New In v0.14.0

- `FluidLink` 0.2.0 adds the preferred `fluidlink-v2` wire protocol while
  retaining all v1 public types and server compatibility.
- V2 payloads are opcode-specific positional binary; the wire carries no JSON.
- Presence and capability registries use numeric bitmasks rather than repeated
  field names or capability strings.
- Time is encoded as integer microseconds and memory as integer bytes.
- Python and .NET validate the same contract SHA-256 and full-frame golden
  vectors. The 17-frame fixture covers every message/runtime-event opcode, all
  optional masks, lifecycle endings, execute/deduplicate decisions, one numeric
  `InvalidPayload` error, and heartbeat.
- The typed .NET client serializes concurrent calls, checks exact response
  correlation, preserves valid sessions only across recoverable runtime-event
  rejection, and invalidates on fatal typed peer errors or protocol drift.
- Python rejects implicit identifier coercion, rejects registration fields on
  resource release, and classifies malformed binary separately from adapter
  rejection.
- The probe runs real v1 and v2 connections for the same event flow and records
  frame bytes, exact fixed-point decision values, and application RTT.
- Delta snapshots are deferred because no repeated snapshot body crosses v2.
- A generic shared-memory FluidLink transport is deferred pending a separate
  record/atomic/backpressure/ACL/crash-recovery contract and sustained benchmark.
- FluidLink remains advisory and separate from the native hook ring/control ABI.

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

For v0.15, the native workload still passed its scoped gate on the RX 580, but
the Gateway-managed wrapper deliberately blocks a performance claim with
`gateway-authorization-outside-native-timing-window`. Authorization p50/p95 was
34,643 / 94,087 us across 11 sessions and 74 round trips per session. The result
is functional closed-loop evidence, not end-to-end acceleration.

## Operating Level

FluidRuntime can inspect process/GPU/memory telemetry, observe an owned D3D11
resource pipeline, publish bounded managed policies, and interfere reversibly
with four owned-lab patterns: generic `CopyResource`, default-to-staging
readback, staging-to-default upload, and exact full-buffer `UpdateSubresource`.
It can also exchange bounded events and compact decisions with FluidGateway
through FluidLink. One exact owned-lab path now turns those decisions into a
bounded native action budget; all other Gateway management remains advisory.

It still does not inject into external games, schedule OS threads, control
physical RAM/VRAM residency, actuate presentation, or support D3D12/Vulkan.

## Read Next

- [v0.15.0 Gateway-managed actuation evidence](evidence/v0.15.0-gateway-managed-update-upload.md)
- [v0.14.0 FluidLink v2 evidence](evidence/v0.14.0-fluidlink-v2.md)
- [v0.13.0 FluidLink evidence](evidence/v0.13.0-fluidlink-binary-interop.md)
- [v0.12.0 evidence](evidence/v0.12.0-update-upload-elision.md)
- [Architecture](architecture.md)
- [Roadmap](roadmap.md)
- [Full handoff](BRIEFING-CLAUDE-CODE.md)
- [v0.11.0 evidence](evidence/v0.11.0-upload-elision.md)
