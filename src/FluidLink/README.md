# FluidLink

FluidLink is the versioned local transport library between FluidRuntime and
FluidGateway. Package 0.1.0 implements the `fluidlink-v1` binary protocol with
no third-party dependencies.

## Wire Format

- fixed 56-byte little-endian header;
- 4-byte `FLNK` magic and 1-byte wire version;
- 1-byte message, event, and decision opcodes;
- 8-byte monotonic sequence;
- 16-byte message and session identities;
- explicit 32-bit payload length with a 1 MiB limit;
- compact UTF-8 JSON object only for dynamic event data, bounded to depth 64
  with finite numbers;
- exact SHA-256 contract negotiation during Hello/Welcome;
- exact-read handling for fragmented TCP delivery.

The control envelope is binary. The dynamic payload is intentionally JSON in
v1 so adapters can evolve fields without a custom unsafe serializer. The
canonical layout and opcode registry are bundled as
`contracts/fluidlink-v1.contract.json`.

## Client

```csharp
await using var client = new FluidLinkClient("127.0.0.1", 8765);
var welcome = await client.HandshakeAsync("my-runtime-adapter", "0.1.0");

var decision = await client.SendRuntimeEventAsync(
    FluidLinkEventOpcode.Operation,
    new
    {
        Id = "upload-2",
        OperationType = "upload",
        Source = "ram-buffer",
        Target = "vram-buffer",
        SizeMb = 64
    });

if (decision.DecisionOpcode ==
    FluidLinkDecisionOpcode.DeduplicateIdenticalTransfer)
{
    Console.WriteLine("Gateway classified the transfer as redundant.");
}

await client.GoodbyeAsync();
```

The client permits loopback endpoints only, negotiates the exact contract and
required capabilities, serializes concurrent round trips, correlates every
response, validates heartbeat/session/sub-opcode state, bounds payloads, and
invalidates the connection on malformed or truncated frames. `BytesSent`,
`BytesReceived`, and `EquivalentJsonEnvelopeBytes` expose reproducible frame
measurements. They exclude TCP/IP overhead.

## Authority Boundary

FluidLink is not authorization for the native hook. A Gateway decision remains
advisory until a separate bounded native policy validates target opt-in,
provenance, action, budget, expiration, equivalence, evidence, and rollback.
The protocol is local user-space IPC without hostile-peer authentication.
