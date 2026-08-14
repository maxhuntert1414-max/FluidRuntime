# Project Status

FluidRuntime v0.21.2 is verified locally as of 2026-08-14. It hardens process
lifecycle, report persistence, native-probe deadlines, compiler defenses, and
static-analysis gates without changing the v0.21 D3D12 transfer contract.
Authority remains bounded to one owned COPY queue, two command lists, two 4 MiB
destinations, two isolated lanes, one fence, and 128 exact actions.

## Release Target

- Target branch/tag: `main` / `v0.21.2`
- Canonical Gateway contract: [FluidGateway v0.67.1](https://github.com/maxhuntert1414-max/FluidGateway/releases/tag/v0.67.1)
- Target release: [FluidRuntime v0.21.2](https://github.com/maxhuntert1414-max/FluidRuntime/releases/tag/v0.21.2)
- FluidLink workflow: [GitHub Actions](https://github.com/maxhuntert1414-max/FluidRuntime/actions/workflows/fluidlink.yml)
- Runtime validation: [GitHub Actions](https://github.com/maxhuntert1414-max/FluidRuntime/actions/workflows/ci.yml)

## Local Release Gate

- Managed tests: 194/194 passed.
- FluidGateway complete suite: 268/268 passed on Python 3.12 and Python 3.14.
- Native code analysis passes for the D3D12 transfer hook and D3D11 owned
  target with analyzer warnings treated as errors.
- All managed evidence writes are atomic; cancellation preserves the previous
  file. All launched native targets share verified process-tree cleanup.
- FluidGateway resilience suite: ten adversarial cases passed ten consecutive
  runs, for 100/100 total.
- Cross-process Python/.NET probe: 11/11 v1 and 11/11 v2 round trips passed.
- Base contract file and negotiated SHA-256:
  `0d24d96aec32d74e123f9e198e51adde74ddf190e8c40b0ac18bddf5c4108b2f`.
- Batch contract SHA-256:
  `bf8727c22ac878ceff6dd0f462d6db5e81174737e839ecdf2e263a6f55268542`;
  shared golden-vector SHA-256:
  `9a626d9b257dd7341a090a49ca649bbc88c0c3ba32ba1edabbf18166a321aeea`.
- FluidLink frame bytes: 3,189 for v1 versus 1,880 for v2, saving 1,309
  bytes or 41.05% for the same cross-process semantic flow.
- The generalized D3D12 WARP gate passed two measured pairs plus all three
  fault controls. Both performance gates remain blocked for software evidence.
- The generalized D3D12 RX 580 gate passed 30/30 measured pairs plus one
  warmup. Submit-to-fence and GPU timestamp each favored optimized in 30/30.
- RX 580 submit-to-fence delta p50/p95/p99 was
  -45,816.500 / -42,615.850 / -37,080.440 us; GPU timestamp delta was
  -46,870.000 / -42,814.100 / -38,003.560 us.
- Managed end-to-end favored optimized in 27/30 pairs. Its p50 was
  -15,615.000 us, but p95/p99 was +18,261.950 / +53,557.120 us. Therefore the
  native execution gate passes and the complete product-performance gate stays
  blocked by `managed-end-to-end-improvement-not-consistent`.
- Concurrent authorization completed 128 measured requests plus 15 warmups with
  zero failures. Concurrency-8 p99 was 215,338.490 us against a 250,000 us
  session-level budget.
- Each optimized 128-candidate run skipped 128 exact 4 MiB duplicates and
  accounted for 536,870,912 logical API bytes. This is not a physical-transfer
  counter.
- Malformed, stalled, and cumulatively slow peers each published no D3D12 policy
  and launched a clean baseline with 136 tracked calls forwarded and zero skips.
  The slow peer completed four round trips before the total deadline.
- `FluidLink.0.3.0.nupkg` inspected with the DLL, README, v1/base-v2/batch
  contracts, and both v2 golden-vector files present.
- Native tests: 21/21 Release and 21/21 Debug passed.
- Negative control-policy matrix: 320/320 WARP processes passed.
- Exact local CI evidence contract: passed.
- Remote CI remains a separate required gate for pushed `main` and release tags.
- Generic, manager, sustained, readback, and staging-upload regression smokes
  passed after ABI and hook changes.
- Raw D3D11 and D3D12 WARP/RX 580 traces are committed with the source.

The v0.16 D3D12 gate also passed:

- 5/5 WARP Release, 5/5 WARP Debug, and 10/10 RX 580 owned runs;
- exact 4 MiB content after UPLOAD-to-DEFAULT-to-READBACK in every run;
- one COPY command list, two copy commands, one explicit transition, and one
  completed fence per run;
- DEFAULT buffer `COMMON` creation, implicit `COPY_DEST` promotion,
  `COPY_SOURCE` transition, and expected post-execution buffer decay;
- Debug Layer message validation with zero warnings and zero errors;
- stable adapter/architecture identity, launched PID binding, and target
  SHA-256 verified before and after each aggregate lab;
- strict rejection of missing/unknown report fields and invalid native options;
- performance claim blocked because the lab has no optimized baseline and
  logical command bytes are not physical transfer counters.

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

## New In v0.21.0

- `fluidruntime-native-transfer-v1` defines numeric backend/operation IDs and a
  bounded queue/scope/resource/lane/fence topology shared by managed and native
  code. Vulkan has a reserved backend ID but no implementation yet.
- `fluidruntime-d3d12-transfer-hook.dll` keeps lane state by
  `(execution_scope_id, destination_resource_id)` and hooks five command-list
  methods plus queue `ExecuteCommandLists` and `Signal`.
- The owned target uses two 8 MiB UPLOAD resources, two 4 MiB DEFAULT resources,
  two independent command lists/readbacks, one COPY queue, and one registered
  fence. Baseline forwards 136 tracked copies; optimized forwards eight guards
  and skips 128 exact duplicates.
- FluidGateway authorization hashes the exact transfer topology. Missing
  topology is rejected by managed code; invalid attach topology leaves vtables
  untouched; incomplete native registration rejects policy before any skip.
- Every generalized event carries logical IDs and flag 512. Submission order,
  fence signal, lane generations, content, zero overruns, and rollback must all
  agree between ring, snapshot, target JSON, and managed report.
- Event IDs 17..22 and action bit 16 now expose backend-neutral transfer names;
  D3D12 names remain aliases. Each destination can have only one lane owner, and
  aliased ownership is rejected with zero skips before policy publication.
- The RX 580 native execution gate passed 30/30 submit-to-fence and GPU pairs.
  The full managed end-to-end gate remains blocked, so v0.21 makes no claim of
  whole-product acceleration, game FPS, input latency, power, or physical
  RAM/VRAM/PCIe reduction.
- Full methodology and raw hashes are in
  [the v0.21 evidence report](evidence/v0.21.0-d3d12-transfer-core.md).

## New In v0.20.0

- `fluidruntime-d3d12-hook.dll` patches five methods on one exact owned COPY
  command list and forwards every unrelated object unchanged.
- `gateway-d3d12-copy-lab` uses a domain-separated FluidGateway authorization
  profile and maps accepted decisions to action bit 16 with budget 1..128.
- One 8 MiB CPU-cacheable shadow matches an UPLOAD resource that is unmapped
  before command recording. The hook retains at most one 4 MiB destination
  image and performs a full comparison before every candidate action.
- One partial destination write proves automatic invalidation; an explicit
  invalidation and command-list close prove separate lifecycle boundaries.
- Baseline runs forward 132 tracked copies. Optimized runs forward four required
  copies and skip 128 exact repeats while preserving final readback content.
- RX 580 managed end-to-end, submit-to-fence, and GPU p50/p95/p99 deltas all
  passed the fixed hardware gate with 10/10 wins. CPU record tails remain
  published and mixed because both paths retain exact comparison cost.
- Invalid, stalled, and cumulatively slow Gateway peers publish no native policy
  and complete the same all-forwarded baseline on WARP and hardware.
- The result is scoped to one owned D3D12 buffer workload. Logical command bytes
  are not physical PCIe or RAM/VRAM traffic, and external games remain excluded.
- Full methodology and raw hashes are in
  [the v0.20 evidence report](evidence/v0.20.0-d3d12-copy-elision.md).

## New In v0.19.0

- Every baseline and optimized run records managed end-to-end elapsed time.
  Optimized timing must contain its own live Gateway authorization latency.
- The RX 580 performance gate now requires negative paired p50, p95, and p99
  complete-window deltas plus at least 80% optimized wins and the existing
  native CPU/GPU checks.
- A separate authorization benchmark runs 32 measured requests at concurrency
  1, 2, 4, and 8, validating all decisions, unique contexts, stable peer
  identity, failures, throughput, bytes, round trips, and p50/p95/p99.
- FluidGateway 0.67 releases a bounded worker slot before writing the final
  `GOODBYE` acknowledgement. Cleanup is idempotent and genuine saturation still
  rejects excess active sessions.
- Fail-closed reports time the authorization failure and verified baseline
  fallback in one outer window.
- The current transport decision is to retain loopback TCP for session-level
  control. No per-frame, FPS, power, PCIe, physical RAM/VRAM, external-game, or
  general-scheduler claim follows from this result.
- Full methodology and raw hashes are in
  [the v0.19 evidence report](evidence/v0.19.0-end-to-end-authorization.md).

## New In v0.18.0

- FluidGateway 0.66 isolates local clients behind an eight-worker limit and
  rejects excess connections. Initial headers, in-progress frames, and idle
  sessions use monotonic absolute deadlines that do not reset per byte.
- The owned update-upload lane accepts an explicit `--candidate-action-count`
  from 1 through 128 and promotes 128 as the controlled-lab default.
- One seed plus 128 exact duplicate candidates travel as one 129-operation
  request and one ordered decision vector. No FluidLink fingerprint changed.
- The existing native action-8 ceiling, one-resource/4 MiB exact-content cache,
  policy ABI, expiration, generation guard, and rollback remain unchanged.
- WARP, adversarial peer controls, and 20 measured RX 580 pairs passed. The
  positive timing result applies only to the owned native D3D11 workload.
- Gateway authorization remains outside the native timing interval, so the
  closed-loop report still blocks FPS, latency, power, PCIe, physical RAM/VRAM,
  external-game, and general-scheduler claims.
- Full evidence and raw hashes are recorded in
  [the v0.18 evidence report](evidence/v0.18.0-resilience-update-upload-128.md).

## New In v0.17.0

- FluidLink 0.3.0 adds an optional operation-batch profile identified by the
  exact contract SHA-256
  `bf8727c22ac878ceff6dd0f462d6db5e81174737e839ecdf2e263a6f55268542`.
- Capability bit 7, event opcode 105, decision opcode 7, and a strict 1..256
  operation limit extend the protocol without changing the original v2 hash or
  its accepted capability mask.
- Gateway authorization sends one homogeneous 65-operation request and accepts
  only an echoed batch identity plus an ordered 65-entry decision vector.
- The complete controlled authorization uses 10 round trips instead of 74 while
  retaining 71 logical runtime events and validating every operation decision.
- A malformed, rejected, incomplete, or partially failed batch closes the
  session and publishes no vector or native policy. Stall and cumulative slow-
  response controls still launch a clean baseline.
- FluidLink frame counters prove protocol-shape reduction only. Authorization
  remains outside the native timing interval, so v0.17 makes no FPS, latency,
  power, PCIe, or physical RAM/VRAM performance claim.

## New In v0.16.0

- `fluidruntime-d3d12-observation` is a separate owned executable; it does not
  reuse or modify the D3D11 hook.
- `d3d12-observe-lab` launches 1-30 bounded runs against WARP or a hardware
  adapter and writes a structured aggregate report with every raw run.
- The native path records adapter identity, UMA/cache-coherent UMA, heap tier,
  COPY queue metadata, committed UPLOAD/DEFAULT/READBACK buffers, declared
  states, commands, fence values, exact hashes, timings, and DXGI local/non-local
  memory snapshots.
- The DEFAULT buffer relies on free `COMMON` promotion for its first
  `COPY_DEST` access and one explicit transition before readback; Debug Layer
  validation is clean.
- The managed parser rejects unknown or missing fields and any drift in fixed
  sizes, heap/state facts, queue counts, hashes, fences, claim scope, adapter,
  architecture, PID, timestamp order, or executable identity.
- Observation remains non-actuating. No D3D12 command is skipped and no
  physical RAM/VRAM, PCIe, FPS, or game-performance claim is made.

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

D3D12 v0.21 actuation scope:

`owned-d3d12-multi-lane-copy-buffer-fluidgateway-authorized-exact-content-elision`

The RX 580 report sets `native_execution_gate_passed=true`: submit-to-fence and
GPU timestamp tails were negative with 30/30 optimized wins. It keeps
`performance_claim_allowed=false` because managed end-to-end p95/p99 was
positive. WARP remains blocked by `software-adapter-not-hardware` and is used as
functional evidence only.

D3D11 positive actuation scope:

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

FluidRuntime can inspect process/GPU/memory telemetry, observe owned D3D11 and
D3D12 resource paths, publish bounded managed policies, and interfere reversibly
with five owned-lab patterns: generic `CopyResource`, default-to-staging
readback, staging-to-default upload, exact full-buffer `UpdateSubresource`, and
D3D12 full-buffer `CopyBufferRegion`. The D3D12 path now models two independent
lanes plus queue submission and fence signal under a neutral transfer contract.
It can also exchange bounded events and compact decisions with FluidGateway
through FluidLink. Two domain-separated owned-lab paths now turn those decisions
into bounded native action budgets; all other Gateway management remains
advisory.

It still does not inject into external games, schedule OS threads, control
physical RAM/VRAM residency, actuate presentation, or support Vulkan.

## Read Next

- [v0.21.0 generalized D3D12 transfer evidence](evidence/v0.21.0-d3d12-transfer-core.md)
- [v0.20.0 single-lane D3D12 evidence](evidence/v0.20.0-d3d12-copy-elision.md)
- [v0.16.0 D3D12 observation evidence](evidence/v0.16.0-d3d12-observation.md)
- [v0.15.0 Gateway-managed actuation evidence](evidence/v0.15.0-gateway-managed-update-upload.md)
- [v0.14.0 FluidLink v2 evidence](evidence/v0.14.0-fluidlink-v2.md)
- [v0.13.0 FluidLink evidence](evidence/v0.13.0-fluidlink-binary-interop.md)
- [v0.12.0 evidence](evidence/v0.12.0-update-upload-elision.md)
- [Architecture](architecture.md)
- [Roadmap](roadmap.md)
- [Full handoff](BRIEFING-CLAUDE-CODE.md)
- [v0.11.0 evidence](evidence/v0.11.0-upload-elision.md)
