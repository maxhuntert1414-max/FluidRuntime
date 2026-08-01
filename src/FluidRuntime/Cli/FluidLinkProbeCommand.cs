using System.Diagnostics;
using System.Text.Json;
using FluidLink;
using FluidRuntime.Runtime;

namespace FluidRuntime.Cli;

public static class FluidLinkProbeCommand
{
    private const string ClientName = "fluidruntime";
    private const string ClientVersion = "0.14.0";
    private const string ProbeSessionId = "fluidruntime-link-probe";
    private const string ProbeNonce = "fluidruntime-link-probe";
    private const string RamResourceId = "fluidlink-ram-buffer";
    private const string VramResourceId = "fluidlink-vram-buffer";
    private const ulong TransferSizeBytes = 64UL * 1024 * 1024;
    private const uint TransferCostMicroseconds = 800;
    private const int RuntimeEventCount = 8;
    private const int RoundTripCount = RuntimeEventCount + 3;

    public static async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        if (args.Any(argument => argument is "--help" or "-h"))
        {
            Console.WriteLine(FluidLinkProbeOptions.Usage);
            return 0;
        }

        try
        {
            var options = FluidLinkProbeOptions.Parse(args);
            var report = await RunProbeAsync(options, cancellationToken);
            var outputPath = Path.GetFullPath(options.OutputPath);
            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                WriteIndented = true
            });
            await File.WriteAllTextAsync(
                outputPath,
                json + Environment.NewLine,
                cancellationToken);

            Console.WriteLine(
                $"FluidLink negotiated {report.Protocol} with " +
                $"{report.ServerName} {report.ServerVersion}.");
            Console.WriteLine(
                $"Runtime events: {report.RuntimeEventCount}; " +
                $"v2 round trips: {report.RoundTripCount}; " +
                $"heartbeat: {(report.HeartbeatVerified ? "verified" : "failed")}.");
            Console.WriteLine(
                $"Duplicate upload decision: {report.DuplicatePolicy}; " +
                $"executed={report.DuplicateUploadExecuted}.");
            Console.WriteLine(
                $"FluidLink v2 frame bytes: sent={report.BytesSent}; " +
                $"received={report.BytesReceived}; total={report.TotalFrameBytes}.");
            Console.WriteLine(
                $"Same-flow FluidLink v1 baseline: " +
                $"{report.V1BaselineTotalFrameBytes} bytes; " +
                $"v2 reduction={report.ByteReductionVsV1Percent:F2}%.");
            Console.WriteLine(
                $"Application RTT (v2): p50={report.RoundTripP50Microseconds} us; " +
                $"p95={report.RoundTripP95Microseconds} us; " +
                $"max={report.RoundTripMaxMicroseconds} us.");
            Console.WriteLine(report.IntercommunicationVerified
                ? "FluidLink v2 intercommunication gate: passed."
                : "FluidLink v2 intercommunication gate: failed.");
            Console.WriteLine($"Report: {outputPath}");
            return report.IntercommunicationVerified ? 0 : 1;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            FluidLinkProtocolException or
            FluidLinkV2ProtocolException or
            InvalidDataException)
        {
            Console.Error.WriteLine($"FluidLink input/protocol error: {exception.Message}");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FluidLink probe failed: {exception.Message}");
            return 1;
        }
    }

    internal static async Task<FluidLinkProbeReport> RunProbeAsync(
        FluidLinkProbeOptions options,
        CancellationToken cancellationToken = default)
    {
        var legacy = await RunLegacyBaselineAsync(options, cancellationToken);
        var current = await RunV2Async(options, cancellationToken);

        var bytesSaved = legacy.TotalFrameBytes - current.TotalFrameBytes;
        var reductionPercent = legacy.TotalFrameBytes > 0
            ? Math.Round(bytesSaved * 100.0 / legacy.TotalFrameBytes, 2)
            : 0;
        var contractVerified = string.Equals(
            current.Welcome.ContractSha256,
            FluidLinkV2Protocol.ContractSha256,
            StringComparison.Ordinal);
        var capabilitiesReady =
            (FluidLinkV2Protocol.RequiredCapabilities &
             ~current.Welcome.AcceptedCapabilities) == 0;
        var firstExecuted = current.FirstUpload.Executed is true;
        var duplicateExecuted = current.DuplicateUpload.Executed is true;
        var fixedPointDecisionVerified =
            current.DuplicateUpload.SavedMicroseconds == TransferCostMicroseconds &&
            current.DuplicateUpload.SavedBytes == TransferSizeBytes;
        var verified =
            legacy.Verified &&
            contractVerified &&
            current.Welcome.MaxPayloadBytes == FluidLinkV2Protocol.MaxPayloadBytes &&
            capabilitiesReady &&
            string.Equals(current.Heartbeat, ProbeNonce, StringComparison.Ordinal) &&
            firstExecuted &&
            !duplicateExecuted &&
            current.DuplicateUpload.DecisionOpcode ==
                FluidLinkV2DecisionOpcode.DeduplicateIdenticalTransfer &&
            fixedPointDecisionVerified &&
            current.RoundTripMicroseconds.Count == RoundTripCount &&
            bytesSaved > 0 &&
            reductionPercent >= 40;

        return new FluidLinkProbeReport(
            Mode: "fluidlink-probe-v0.14.0",
            Protocol: FluidLinkV2Protocol.Version,
            Transport: "tcp-loopback",
            ContractSha256: FluidLinkV2Protocol.ContractSha256,
            ContractVerified: contractVerified,
            BinaryFraming: true,
            HeaderBytes: FluidLinkV2Protocol.HeaderSize,
            PayloadEncoding: "opcode-specific-positional-binary",
            JsonPayloads: false,
            FixedPointUnits: true,
            MaxPayloadBytes: current.Welcome.MaxPayloadBytes,
            SessionId: current.Welcome.SessionId,
            ServerName: current.Welcome.ServerName,
            ServerVersion: current.Welcome.ServerVersion,
            AcceptedCapabilities: (ulong)current.Welcome.AcceptedCapabilities,
            AcceptedCapabilityNames: CapabilityNames(
                current.Welcome.AcceptedCapabilities),
            RuntimeEventCount: RuntimeEventCount,
            RoundTripCount: RoundTripCount,
            V1BaselineRoundTripCount: RoundTripCount,
            HeartbeatVerified: string.Equals(
                current.Heartbeat,
                ProbeNonce,
                StringComparison.Ordinal),
            FirstUploadExecuted: firstExecuted,
            DuplicateUploadExecuted: duplicateExecuted,
            DuplicateDecisionOpcode: (byte)current.DuplicateUpload.DecisionOpcode,
            DuplicatePolicy: FluidLinkV2Protocol.DecisionPolicyName(
                current.DuplicateUpload.DecisionOpcode),
            EstimatedSavedMicroseconds:
                current.DuplicateUpload.SavedMicroseconds,
            EstimatedSavedBytes: current.DuplicateUpload.SavedBytes,
            EstimatedSavedMs:
                current.DuplicateUpload.SavedMicroseconds / 1000.0,
            EstimatedSavedMib:
                current.DuplicateUpload.SavedBytes / (1024.0 * 1024.0),
            NumericOpcodes: true,
            BytesSent: current.BytesSent,
            BytesReceived: current.BytesReceived,
            TotalFrameBytes: current.TotalFrameBytes,
            V1BaselineBytesSent: legacy.BytesSent,
            V1BaselineBytesReceived: legacy.BytesReceived,
            V1BaselineTotalFrameBytes: legacy.TotalFrameBytes,
            BytesSavedVsV1: bytesSaved,
            ByteReductionVsV1Percent: reductionPercent,
            RoundTripP50Microseconds: Percentile(
                current.RoundTripMicroseconds,
                0.50),
            RoundTripP95Microseconds: Percentile(
                current.RoundTripMicroseconds,
                0.95),
            RoundTripMaxMicroseconds: current.RoundTripMicroseconds.Max(),
            DeltaEncodingEnabled: false,
            SharedMemoryTransportEnabled: false,
            IntercommunicationVerified: verified,
            Scope: "loopback-positional-binary-control-transport",
            Limitations:
            [
                "Byte counters cover FluidLink frames delivered to TCP, not TCP/IP overhead.",
                "The v1 and v2 measurements are sequential loopback sessions using the same semantic events.",
                "Fixed-point units are enforced at the FluidLink wire boundary; Gateway internals still adapt to their current model.",
                "The probe models resource intent; it does not observe physical RAM, VRAM, or PCIe traffic.",
                "The returned decision is advisory and does not authorize the native hook.",
                "Delta snapshots and shared-memory transport are not part of FluidLink v2.",
                "FluidLink v2 is a local user-space protocol without hostile-peer authentication."
            ]);
    }

    private static async Task<LegacyProbeResult> RunLegacyBaselineAsync(
        FluidLinkProbeOptions options,
        CancellationToken cancellationToken)
    {
        await using var client = new FluidLinkClient(
            options.Host,
            options.Port,
            TimeSpan.FromMilliseconds(options.TimeoutMs));
        var welcome = await client.HandshakeAsync(
            ClientName,
            ClientVersion,
            cancellationToken: cancellationToken);
        var heartbeat = await client.PingAsync(ProbeNonce, cancellationToken);
        await SendLegacyEventAsync(client, FluidLinkEventOpcode.Session, new
        {
            Action = "begin",
            Id = ProbeSessionId,
            Budgets = new { FrameMs = 16.667, RamMb = 4096, VramMb = 4096 }
        }, cancellationToken);
        await SendLegacyEventAsync(client, FluidLinkEventOpcode.Frame, new
        {
            Action = "begin",
            Frame = 0,
            TargetFrameMs = 16.667
        }, cancellationToken);
        await SendLegacyEventAsync(client, FluidLinkEventOpcode.Resource, new
        {
            Id = RamResourceId,
            Kind = "buffer",
            Memory = "ram",
            SizeMb = 64
        }, cancellationToken);
        await SendLegacyEventAsync(client, FluidLinkEventOpcode.Resource, new
        {
            Id = VramResourceId,
            Kind = "buffer",
            Memory = "vram",
            SizeMb = 64
        }, cancellationToken);
        var firstUpload = await SendLegacyEventAsync(
            client,
            FluidLinkEventOpcode.Operation,
            new
            {
                Id = "fluidlink-upload-1",
                OperationType = "upload",
                Source = RamResourceId,
                Target = VramResourceId,
                Queue = "copy",
                CostMs = 0.8,
                SizeMb = 64,
                Frame = 0
            },
            cancellationToken);
        var duplicateUpload = await SendLegacyEventAsync(
            client,
            FluidLinkEventOpcode.Operation,
            new
            {
                Id = "fluidlink-upload-2",
                OperationType = "upload",
                Source = RamResourceId,
                Target = VramResourceId,
                Queue = "copy",
                CostMs = 0.8,
                SizeMb = 64,
                Frame = 0
            },
            cancellationToken);
        await SendLegacyEventAsync(client, FluidLinkEventOpcode.Frame, new
        {
            Action = "end",
            Frame = 0
        }, cancellationToken);
        await SendLegacyEventAsync(client, FluidLinkEventOpcode.Session, new
        {
            Action = "end"
        }, cancellationToken);
        await client.GoodbyeAsync(cancellationToken);

        var capabilitiesReady = FluidLinkProtocol.RequiredRuntimeCapabilities.All(
            capability => welcome.AcceptedCapabilities.Contains(
                capability,
                StringComparer.Ordinal));
        var verified =
            string.Equals(
                welcome.ContractSha256,
                FluidLinkProtocol.ContractSha256,
                StringComparison.Ordinal) &&
            capabilitiesReady &&
            string.Equals(heartbeat, ProbeNonce, StringComparison.Ordinal) &&
            firstUpload.Executed is true &&
            duplicateUpload.Executed is false &&
            duplicateUpload.DecisionOpcode ==
                FluidLinkDecisionOpcode.DeduplicateIdenticalTransfer;
        return new LegacyProbeResult(
            client.BytesSent,
            client.BytesReceived,
            client.BytesSent + client.BytesReceived,
            verified);
    }

    private static async Task<V2ProbeResult> RunV2Async(
        FluidLinkProbeOptions options,
        CancellationToken cancellationToken)
    {
        var roundTripMicroseconds = new List<long>(RoundTripCount);
        await using var client = new FluidLinkV2Client(
            options.Host,
            options.Port,
            TimeSpan.FromMilliseconds(options.TimeoutMs));
        var welcome = await MeasureAsync(
            roundTripMicroseconds,
            () => client.HandshakeAsync(
                ClientName,
                ClientVersion,
                cancellationToken: cancellationToken));
        var heartbeat = await MeasureAsync(
            roundTripMicroseconds,
            () => client.PingAsync(ProbeNonce, cancellationToken));
        await MeasureAsync(
            roundTripMicroseconds,
            () => client.SendSessionEventAsync(
                new FluidLinkV2SessionEvent(
                    FluidLinkV2LifecycleAction.Begin,
                    ProbeSessionId,
                    FrameBudgetMicroseconds: 16_667,
                    RamBudgetBytes: 4UL * 1024 * 1024 * 1024,
                    VramBudgetBytes: 4UL * 1024 * 1024 * 1024),
                cancellationToken));
        await MeasureAsync(
            roundTripMicroseconds,
            () => client.SendFrameEventAsync(
                new FluidLinkV2FrameEvent(
                    FluidLinkV2LifecycleAction.Begin,
                    Frame: 0,
                    TargetFrameMicroseconds: 16_667),
                cancellationToken));
        await MeasureAsync(
            roundTripMicroseconds,
            () => client.SendResourceEventAsync(
                FluidLinkV2ResourceEvent.Register(
                    RamResourceId,
                    FluidLinkV2ResourceKind.Buffer,
                    FluidLinkV2MemoryLayer.Ram,
                    FluidLinkV2Lifetime.Unknown,
                    TransferSizeBytes),
                cancellationToken));
        await MeasureAsync(
            roundTripMicroseconds,
            () => client.SendResourceEventAsync(
                FluidLinkV2ResourceEvent.Register(
                    VramResourceId,
                    FluidLinkV2ResourceKind.Buffer,
                    FluidLinkV2MemoryLayer.Vram,
                    FluidLinkV2Lifetime.Unknown,
                    TransferSizeBytes),
                cancellationToken));
        var firstUpload = await MeasureAsync(
            roundTripMicroseconds,
            () => client.SendOperationEventAsync(
                new FluidLinkV2OperationEvent(
                    FluidLinkV2OperationType.Upload,
                    FluidLinkV2Queue.Copy,
                    "fluidlink-upload-1",
                    TransferCostMicroseconds,
                    TransferSizeBytes,
                    Source: RamResourceId,
                    Target: VramResourceId,
                    Frame: 0),
                cancellationToken));
        var duplicateUpload = await MeasureAsync(
            roundTripMicroseconds,
            () => client.SendOperationEventAsync(
                new FluidLinkV2OperationEvent(
                    FluidLinkV2OperationType.Upload,
                    FluidLinkV2Queue.Copy,
                    "fluidlink-upload-2",
                    TransferCostMicroseconds,
                    TransferSizeBytes,
                    Source: RamResourceId,
                    Target: VramResourceId,
                    Frame: 0),
                cancellationToken));
        await MeasureAsync(
            roundTripMicroseconds,
            () => client.SendFrameEventAsync(
                new FluidLinkV2FrameEvent(
                    FluidLinkV2LifecycleAction.End,
                    Frame: 0),
                cancellationToken));
        await MeasureAsync(
            roundTripMicroseconds,
            () => client.SendSessionEventAsync(
                new FluidLinkV2SessionEvent(
                    FluidLinkV2LifecycleAction.End,
                    SessionId: string.Empty),
                cancellationToken));
        await MeasureAsync(
            roundTripMicroseconds,
            () => client.GoodbyeAsync(cancellationToken));

        return new V2ProbeResult(
            welcome,
            heartbeat,
            firstUpload,
            duplicateUpload,
            client.BytesSent,
            client.BytesReceived,
            client.BytesSent + client.BytesReceived,
            roundTripMicroseconds);
    }

    private static Task<FluidLinkRuntimeDecision> SendLegacyEventAsync(
        FluidLinkClient client,
        FluidLinkEventOpcode eventOpcode,
        object payload,
        CancellationToken cancellationToken) =>
        client.SendRuntimeEventAsync(eventOpcode, payload, cancellationToken);

    private static async Task<T> MeasureAsync<T>(
        ICollection<long> samples,
        Func<Task<T>> action)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await action();
        }
        finally
        {
            samples.Add(ElapsedMicroseconds(started));
        }
    }

    private static async Task MeasureAsync(
        ICollection<long> samples,
        Func<Task> action)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await action();
        }
        finally
        {
            samples.Add(ElapsedMicroseconds(started));
        }
    }

    private static long ElapsedMicroseconds(long started) =>
        checked((long)Math.Round(
            (Stopwatch.GetTimestamp() - started) * 1_000_000.0 /
            Stopwatch.Frequency,
            MidpointRounding.AwayFromZero));

    private static long Percentile(IReadOnlyCollection<long> values, double percentile)
    {
        if (values.Count == 0)
        {
            return 0;
        }
        var ordered = values.Order().ToArray();
        var index = Math.Max(
            0,
            (int)Math.Ceiling(percentile * ordered.Length) - 1);
        return ordered[index];
    }

    private static IReadOnlyList<string> CapabilityNames(
        FluidLinkV2Capability capabilities) =>
        Enum.GetValues<FluidLinkV2Capability>()
            .Where(value => value != FluidLinkV2Capability.None)
            .Where(value => capabilities.HasFlag(value))
            .Select(value => value.ToString())
            .ToArray();

    private sealed record LegacyProbeResult(
        long BytesSent,
        long BytesReceived,
        long TotalFrameBytes,
        bool Verified);

    private sealed record V2ProbeResult(
        FluidLinkV2Welcome Welcome,
        string Heartbeat,
        FluidLinkV2RuntimeDecision FirstUpload,
        FluidLinkV2RuntimeDecision DuplicateUpload,
        long BytesSent,
        long BytesReceived,
        long TotalFrameBytes,
        IReadOnlyList<long> RoundTripMicroseconds);
}
