using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluidRuntime.Native;

public static class D3D12ObservationRunParser
{
    public const string NativeMode =
        "fluidruntime-owned-d3d12-observation-v0.1.0";
    public const string ClaimScope =
        "owned-d3d12-upload-default-readback-observation-only";
    public const ulong BufferBytes = 4UL * 1024UL * 1024UL;

    private static readonly string[] RequiredLimitations =
    [
        "DXGI budgets and usage are snapshots, not physical transfer counters.",
        "Logical bytes describe commands issued by this owned workload only.",
        "This probe does not hook, inject, schedule, or alter external applications."
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static D3D12ObservationRunReport Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        D3D12ObservationRunReport report;
        try
        {
            report = JsonSerializer.Deserialize<D3D12ObservationRunReport>(
                json,
                JsonOptions) ?? throw new InvalidDataException(
                    "The D3D12 observation target returned an empty document.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The D3D12 observation target returned invalid JSON.",
                exception);
        }

        Validate(report);
        return report;
    }

    private static void Validate(D3D12ObservationRunReport report)
    {
        if (!string.Equals(report.Mode, NativeMode, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Unsupported D3D12 observation mode '{report.Mode}'.");
        }
        if (!report.TargetOwned ||
            !report.CooperativeLoad ||
            report.RemoteInjection ||
            !report.ReadOnlyObservation ||
            report.ActuationEnabled ||
            report.PhysicalTransferBytesMeasured ||
            report.DebugWarningCount != 0 ||
            report.DebugErrorCount != 0 ||
            report.DebugMessageValidationAvailable != report.DebugLayerEnabled)
        {
            throw new InvalidDataException(
                "The D3D12 target violated the owned observation-only boundary.");
        }
        if (report.Adapter is null ||
            report.Architecture is null ||
            report.Queue is null ||
            report.Transfer is null ||
            report.Memory is null ||
            report.Limitations is null)
        {
            throw new InvalidDataException(
                "The D3D12 target omitted a required contract section.");
        }
        if (report.RenderDriver is not ("warp" or "hardware") ||
            report.ProcessId <= 0 ||
            report.CapturedAtUnixMs <= 0)
        {
            throw new InvalidDataException(
                "The D3D12 target omitted a valid driver or process identity.");
        }
        if (string.IsNullOrWhiteSpace(report.Adapter.Description) ||
            !IsHex(report.Adapter.Luid, 16))
        {
            throw new InvalidDataException(
                "The D3D12 target omitted a valid adapter identity.");
        }
        if (!report.Architecture.Available ||
            report.Architecture.NodeCount == 0 ||
            report.Architecture.ResourceHeapTier is not (1 or 2))
        {
            throw new InvalidDataException(
                "The D3D12 target omitted required architecture capabilities.");
        }
        if (report.Queue.Type != "copy" || report.Queue.Priority != "normal" ||
            report.Queue.TimestampFrequencySupported !=
                (report.Queue.TimestampFrequencyHz > 0))
        {
            throw new InvalidDataException(
                "The D3D12 target violated the copy-queue contract.");
        }

        var transfer = report.Transfer;
        if (transfer.BufferBytes != BufferBytes ||
            transfer.LogicalUploadBytes != BufferBytes ||
            transfer.LogicalReadbackBytes != BufferBytes ||
            transfer.LogicalTotalCopyBytes != 2 * BufferBytes ||
            transfer.UploadHeapType != "upload" ||
            transfer.DefaultHeapType != "default" ||
            transfer.ReadbackHeapType != "readback" ||
            transfer.UploadInitialState != "generic-read" ||
            transfer.DefaultInitialState != "common" ||
            transfer.DefaultFirstAccessPromotion != "copy-dest" ||
            transfer.DefaultStateBeforeReadbackCopy != "copy-source" ||
            transfer.ExpectedDefaultPostExecuteState != "common-via-buffer-decay" ||
            transfer.ReadbackInitialState != "copy-dest" ||
            transfer.CommandListType != "copy" ||
            transfer.CommandListCount != 1 ||
            transfer.CopyCommandCount != 2 ||
            transfer.ResourceBarrierCount != 1 ||
            transfer.SubmittedCommandListCount != 1)
        {
            throw new InvalidDataException(
                "The D3D12 target violated the fixed transfer contract.");
        }
        if (transfer.FenceSignaledValue != 1 ||
            transfer.FenceCompletedValue < transfer.FenceSignaledValue ||
            !transfer.WaitCompleted)
        {
            throw new InvalidDataException(
                "The D3D12 target did not complete its fence contract.");
        }
        if (transfer.HashAlgorithm != "fnv1a64" ||
            !IsHex(transfer.SourceHash, 16) ||
            !string.Equals(
                transfer.SourceHash,
                transfer.ReadbackHash,
                StringComparison.Ordinal) ||
            !transfer.ContentEquivalent)
        {
            throw new InvalidDataException(
                "The D3D12 target did not prove exact round-trip content.");
        }
        if (!IsFiniteNonNegative(transfer.CpuRecordMicroseconds) ||
            !IsFiniteNonNegative(transfer.SubmitToFenceMicroseconds) ||
            !IsFiniteNonNegative(transfer.TotalWorkloadMicroseconds) ||
            transfer.TotalWorkloadMicroseconds < transfer.CpuRecordMicroseconds ||
            transfer.TotalWorkloadMicroseconds < transfer.SubmitToFenceMicroseconds)
        {
            throw new InvalidDataException(
                "The D3D12 target returned invalid timing observations.");
        }
        if (report.Memory.Source != "idxgiadapter3-query-video-memory-info")
        {
            throw new InvalidDataException(
                "The D3D12 target returned an unsupported memory source.");
        }
        if (report.Memory.LocalBefore is null ||
            report.Memory.LocalAfter is null ||
            report.Memory.NonLocalBefore is null ||
            report.Memory.NonLocalAfter is null)
        {
            throw new InvalidDataException(
                "The D3D12 target omitted a DXGI memory snapshot.");
        }
        ValidateUnavailableSnapshot(report.Memory.LocalBefore);
        ValidateUnavailableSnapshot(report.Memory.LocalAfter);
        ValidateUnavailableSnapshot(report.Memory.NonLocalBefore);
        ValidateUnavailableSnapshot(report.Memory.NonLocalAfter);
        if (!string.Equals(report.ClaimScope, ClaimScope, StringComparison.Ordinal) ||
            !report.Limitations.SequenceEqual(RequiredLimitations, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "The D3D12 target omitted its evidence boundary.");
        }
    }

    private static void ValidateUnavailableSnapshot(D3D12VideoMemorySnapshot snapshot)
    {
        if (!snapshot.Available &&
            (snapshot.BudgetBytes != 0 ||
             snapshot.CurrentUsageBytes != 0 ||
             snapshot.CurrentReservationBytes != 0 ||
             snapshot.AvailableForReservationBytes != 0))
        {
            throw new InvalidDataException(
                "An unavailable DXGI memory snapshot contained measurements.");
        }
    }

    private static bool IsFiniteNonNegative(double value) =>
        double.IsFinite(value) && value >= 0;

    private static bool IsHex(string? value, int length) =>
        value is not null && value.Length == length && value.All(Uri.IsHexDigit);
}
