# FluidLink

FluidLink is the versioned local transport library between FluidRuntime and
FluidGateway. Package 0.3.0 has no third-party dependencies and keeps both wire
generations available:

| Protocol | Payload | Units | Status |
| --- | --- | --- | --- |
| `fluidlink-v2` | Opcode-specific positional binary | Integer microseconds and bytes | Preferred |
| `fluidlink-v2-batched-runtime-events-v1` | One positional operation template plus ordered decision vector | Integer microseconds and bytes | Opt-in profile |
| `fluidlink-v1` | Bounded UTF-8 JSON object | Legacy decimal milliseconds and MiB | Compatible |

Version 2 is additive. Existing v1 callers do not change, and a connection may
use exactly one wire version.

## Version 2 Wire Contract

- fixed 56-byte little-endian header;
- 4-byte `FLNK` magic and 1-byte wire version;
- 1-byte message, event, and decision opcodes;
- 8-byte monotonic sequence;
- 16-byte message and session identities;
- explicit 32-bit payload length, capped at 65,535 bytes;
- positional payload schemas with presence bitmasks for optional fields;
- capability negotiation as one 64-bit mask;
- time encoded as unsigned integer microseconds;
- memory encoded as unsigned integer bytes;
- strict bounded UTF-8 only for actual text fields;
- exact SHA-256 contract negotiation during Hello/Welcome.

The canonical manifest and cross-language full-frame vectors are packaged at
`contracts/fluidlink-v2.contract.json` and
`contracts/fluidlink-v2.golden.json`. Its 17 vectors cover every message and
runtime-event opcode, lifecycle endings, optional masks, execute/deduplicate
decisions, heartbeat, one numeric `InvalidPayload` error, and goodbye. Other
decision/error registry values are codec-tested without one vector each. A
contract edit requires a new fingerprint and matching Python/.NET vectors.

Package 0.3.0 also packages
`contracts/fluidlink-v2-batch.contract.json` and
`contracts/fluidlink-v2-batch.golden.json`. The batch profile adds capability
bit 7, event opcode 105, and decision opcode 7 without changing the base v2
contract hash. A batch carries one shared operation shape and a count from 1 to
256. Its response carries one validated decision per expanded operation, in
the same order. A partial server failure closes the session and returns no
decision vector.

## Typed Client

```csharp
await using var client = new FluidLinkV2Client("127.0.0.1", 8765);
var welcome = await client.HandshakeAsync("my-runtime-adapter", "0.3.0");

await client.SendSessionEventAsync(new FluidLinkV2SessionEvent(
    FluidLinkV2LifecycleAction.Begin,
    "game-session",
    FrameBudgetMicroseconds: 16_667,
    RamBudgetBytes: 4UL * 1024 * 1024 * 1024,
    VramBudgetBytes: 4UL * 1024 * 1024 * 1024));

var decision = await client.SendOperationEventAsync(
    new FluidLinkV2OperationEvent(
        FluidLinkV2OperationType.Upload,
        FluidLinkV2Queue.Copy,
        "upload-2",
        CostMicroseconds: 800,
        SizeBytes: 64UL * 1024 * 1024,
        Source: "ram-buffer",
        Target: "vram-buffer",
        Frame: 0));

if (decision.DecisionOpcode ==
    FluidLinkV2DecisionOpcode.DeduplicateIdenticalTransfer)
{
    Console.WriteLine($"Avoidable logical bytes: {decision.SavedBytes}");
}

await client.GoodbyeAsync();
```

Use the batch profile only when repeated operations have the same fields:

```csharp
var welcome = await client.HandshakeBatchAsync("my-runtime-adapter", "0.3.0");
var decisions = await client.SendOperationBatchAsync(
    new FluidLinkV2OperationBatchEvent(
        Guid.NewGuid().ToString("N"),
        65,
        FluidLinkV2OperationType.Upload,
        FluidLinkV2Queue.Copy,
        CostMicroseconds: 0,
        SizeBytes: 4UL * 1024 * 1024,
        Source: "ram-buffer",
        Target: "vram-buffer"));
```

The client verifies the negotiated profile, echoed batch ID, exact cardinality,
accepted status, and execution/opcode consistency for every vector entry.

The client permits loopback endpoints only, serializes concurrent round trips,
negotiates the exact contract and required capabilities, validates every enum,
mask, length, heartbeat, session, sequence, message ID, subject, and decision,
and fails closed on malformed or truncated frames. A typed peer rejection is
surfaced to the caller. Only `RuntimeEventRejected` preserves an otherwise valid
session; fatal typed peer errors, framing, or correlation drift invalidate the
connection and require a new handshake.

Package 0.3.0 also exposes read-only `LocalEndPoint` and `RemoteEndPoint`
properties while connected. A Windows consumer can correlate that exact TCP
tuple with the OS owner table without receiving the underlying socket. Endpoint
inspection is transport evidence, not cryptographic peer authentication.

`BytesSent` and `BytesReceived` count complete FluidLink frames handed to TCP.
They exclude TCP/IP overhead. In the v0.14 cross-process probe, the same 11
request/response semantics used 3,189 v1 frame bytes and 1,880 v2 frame bytes,
reducing this control-flow byte count by 1,309 bytes, or 41.05%.

## Scope

FluidLink carries advisory control intent. It does not authorize the native
hook, observe physical RAM/VRAM or PCIe traffic, or create unified memory.
The batch profile reduces repeated serialization and request/response turns. It
does not prove lower physical RAM/VRAM or PCIe traffic. Version 2 does not claim
delta snapshots or shared-memory transport: the current
state request has no state body to delta-encode, and shared memory needs a
separate synchronization, backpressure, access-control, and crash-recovery
contract before it can replace TCP.

A Gateway decision still needs a bounded native policy that validates target
opt-in, provenance, action, budget, expiration, equivalence, evidence, and
rollback. FluidLink is local user-space IPC without hostile-peer authentication.
