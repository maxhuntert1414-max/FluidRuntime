using System.Text.Json;
using FluidLink;
using FluidRuntime.Runtime;

namespace FluidRuntime.Cli;

public static class FluidLinkProbeCommand
{
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
                $"FluidLink negotiated binary {report.Protocol} with " +
                $"{report.ServerName} {report.ServerVersion}.");
            Console.WriteLine(
                $"Runtime events: {report.RuntimeEventCount}; " +
                $"round trips: {report.RoundTripCount}; " +
                $"heartbeat: {(report.HeartbeatVerified ? "verified" : "failed")}.");
            Console.WriteLine(
                $"Duplicate upload decision: {report.DuplicatePolicy}; " +
                $"executed={report.DuplicateUploadExecuted}.");
            Console.WriteLine(
                $"FluidLink frame bytes: sent={report.BytesSent}; " +
                $"received={report.BytesReceived}; total={report.TotalFrameBytes}.");
            Console.WriteLine(
                $"Equivalent JSON envelope: {report.EquivalentJsonEnvelopeBytes} bytes; " +
                $"binary reduction={report.BinaryByteReductionPercent:F2}%.");
            Console.WriteLine(report.IntercommunicationVerified
                ? "FluidLink intercommunication gate: passed."
                : "FluidLink intercommunication gate: failed.");
            Console.WriteLine($"Report: {outputPath}");
            return report.IntercommunicationVerified ? 0 : 1;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            FluidLinkProtocolException or
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
        await using var client = new FluidLinkClient(
            options.Host,
            options.Port,
            TimeSpan.FromMilliseconds(options.TimeoutMs));
        var welcome = await client.HandshakeAsync(
            "fluidruntime",
            "0.13.0",
            cancellationToken: cancellationToken);

        const string nonce = "fluidruntime-link-probe";
        var heartbeat = await client.PingAsync(nonce, cancellationToken);
        var eventCount = 0;
        await SendEventAsync(client, FluidLinkEventOpcode.Session, new
        {
            Action = "begin",
            Id = "fluidruntime-link-probe",
            Budgets = new { FrameMs = 16.667, RamMb = 4096, VramMb = 4096 }
        }, cancellationToken);
        eventCount += 1;
        await SendEventAsync(client, FluidLinkEventOpcode.Frame, new
        {
            Action = "begin",
            Frame = 0,
            TargetFrameMs = 16.667
        }, cancellationToken);
        eventCount += 1;
        await SendEventAsync(client, FluidLinkEventOpcode.Resource, new
        {
            Id = "fluidlink-ram-buffer",
            Kind = "buffer",
            Memory = "ram",
            SizeMb = 64
        }, cancellationToken);
        eventCount += 1;
        await SendEventAsync(client, FluidLinkEventOpcode.Resource, new
        {
            Id = "fluidlink-vram-buffer",
            Kind = "buffer",
            Memory = "vram",
            SizeMb = 64
        }, cancellationToken);
        eventCount += 1;
        var firstUpload = await SendEventAsync(
            client,
            FluidLinkEventOpcode.Operation,
            new
            {
                Id = "fluidlink-upload-1",
                OperationType = "upload",
                Source = "fluidlink-ram-buffer",
                Target = "fluidlink-vram-buffer",
                Queue = "copy",
                CostMs = 0.8,
                SizeMb = 64,
                Frame = 0
            },
            cancellationToken);
        eventCount += 1;
        var duplicateUpload = await SendEventAsync(
            client,
            FluidLinkEventOpcode.Operation,
            new
            {
                Id = "fluidlink-upload-2",
                OperationType = "upload",
                Source = "fluidlink-ram-buffer",
                Target = "fluidlink-vram-buffer",
                Queue = "copy",
                CostMs = 0.8,
                SizeMb = 64,
                Frame = 0
            },
            cancellationToken);
        eventCount += 1;
        await SendEventAsync(client, FluidLinkEventOpcode.Frame, new
        {
            Action = "end",
            Frame = 0
        }, cancellationToken);
        eventCount += 1;
        await SendEventAsync(client, FluidLinkEventOpcode.Session, new
        {
            Action = "end"
        }, cancellationToken);
        eventCount += 1;
        await client.GoodbyeAsync(cancellationToken);

        var firstExecuted = firstUpload.Executed is true;
        var duplicateExecuted = duplicateUpload.Executed is true;
        var decisionOpcode = duplicateUpload.DecisionOpcode;
        var decisionOpcodeValue = (byte)decisionOpcode;
        var duplicatePolicy = FluidLinkProtocol.DecisionPolicyName(decisionOpcode);
        var savedMs = duplicateUpload.SavedMilliseconds;
        var savedMb = duplicateUpload.SavedMegabytes;
        var capabilitiesReady = FluidLinkProtocol.RequiredRuntimeCapabilities.All(
            capability => welcome.AcceptedCapabilities.Contains(
                capability,
                StringComparer.Ordinal));
        var contractVerified = string.Equals(
            welcome.ContractSha256,
            FluidLinkProtocol.ContractSha256,
            StringComparison.Ordinal);
        var verified = contractVerified &&
            welcome.MaxPayloadBytes == FluidLinkProtocol.MaxPayloadBytes &&
            welcome.MaxJsonDepth == FluidLinkProtocol.MaxJsonDepth &&
            capabilitiesReady &&
            string.Equals(heartbeat, nonce, StringComparison.Ordinal) &&
            firstExecuted &&
            !duplicateExecuted &&
            decisionOpcode == FluidLinkDecisionOpcode.DeduplicateIdenticalTransfer;
        var totalFrameBytes = client.BytesSent + client.BytesReceived;
        var binaryBytesSaved = client.EquivalentJsonEnvelopeBytes - totalFrameBytes;
        var binaryReductionPercent = client.EquivalentJsonEnvelopeBytes > 0
            ? Math.Round(
                binaryBytesSaved * 100.0 / client.EquivalentJsonEnvelopeBytes,
                2)
            : 0;
        verified = verified && binaryBytesSaved > 0 && binaryReductionPercent > 0;

        return new FluidLinkProbeReport(
            Mode: "fluidlink-probe-v0.13.0",
            Protocol: FluidLinkProtocol.Version,
            ContractSha256: FluidLinkProtocol.ContractSha256,
            ContractVerified: contractVerified,
            BinaryFraming: true,
            HeaderBytes: FluidLinkProtocol.HeaderSize,
            PayloadEncoding: "utf-8-json-object",
            MaxPayloadBytes: welcome.MaxPayloadBytes,
            MaxJsonDepth: welcome.MaxJsonDepth,
            SessionId: welcome.SessionId,
            ServerName: welcome.ServerName,
            ServerVersion: welcome.ServerVersion,
            AcceptedCapabilities: welcome.AcceptedCapabilities,
            RuntimeEventCount: eventCount,
            RoundTripCount: eventCount + 3,
            HeartbeatVerified: string.Equals(heartbeat, nonce, StringComparison.Ordinal),
            FirstUploadExecuted: firstExecuted,
            DuplicateUploadExecuted: duplicateExecuted,
            DuplicateDecisionOpcode: decisionOpcodeValue,
            DuplicatePolicy: duplicatePolicy,
            EstimatedSavedMs: savedMs,
            EstimatedSavedMb: savedMb,
            NumericOpcodes: true,
            CompactDecisions: welcome.AcceptedCapabilities.Contains(
                "compact.decisions.v1",
                StringComparer.Ordinal),
            BytesSent: client.BytesSent,
            BytesReceived: client.BytesReceived,
            TotalFrameBytes: totalFrameBytes,
            EquivalentJsonEnvelopeBytes: client.EquivalentJsonEnvelopeBytes,
            BinaryBytesSaved: binaryBytesSaved,
            BinaryByteReductionPercent: binaryReductionPercent,
            IntercommunicationVerified: verified,
            Scope: "loopback-binary-control-framing-with-json-dynamic-payload",
            Limitations:
            [
                "Byte counters cover FluidLink frames delivered to TCP, not TCP/IP overhead.",
                "The probe models resource intent; it does not observe physical PCIe traffic.",
                "The returned decision is advisory and does not authorize the native hook.",
                "FluidLink v1 is a local user-space protocol without hostile-peer authentication."
            ]);
    }

    private static Task<FluidLinkRuntimeDecision> SendEventAsync(
        FluidLinkClient client,
        FluidLinkEventOpcode eventOpcode,
        object payload,
        CancellationToken cancellationToken) =>
        client.SendRuntimeEventAsync(eventOpcode, payload, cancellationToken);
}
