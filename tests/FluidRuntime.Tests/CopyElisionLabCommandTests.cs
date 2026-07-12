using System.Text.Json;
using FluidRuntime.Cli;
using FluidRuntime.Runtime;

namespace FluidRuntime.Tests;

public sealed class CopyElisionLabCommandTests
{
    [Fact]
    public void BuildReport_requires_matching_content_and_one_skipped_copy()
    {
        var baseline = CreateRun(copyElisionEnabled: false, forwardedCopies: 6, skippedCopies: 0);
        var optimized = CreateRun(copyElisionEnabled: true, forwardedCopies: 5, skippedCopies: 1);

        var report = CopyElisionLabCommand.BuildReport(baseline, optimized);

        Assert.True(report.ContentEquivalent);
        Assert.Equal(1, report.AvoidedCopyCount);
        Assert.Equal(4096UL, report.AvoidedCopyBytes);

        var mismatched = optimized with { DestinationBufferHash = "different" };
        Assert.Throws<InvalidDataException>(() =>
            CopyElisionLabCommand.BuildReport(baseline, mismatched));

        var rollbackFailed = optimized with { RollbackRestored = false };
        Assert.Throws<InvalidDataException>(() =>
            CopyElisionLabCommand.BuildReport(baseline, rollbackFailed));
    }

    private static HookLabReport CreateRun(
        bool copyElisionEnabled,
        long forwardedCopies,
        long skippedCopies)
    {
        using var document = JsonDocument.Parse("{}");
        return new HookLabReport(
            Mode: "fluidruntime-hook-ipc-lab-v0.5",
            ReadOnly: !copyElisionEnabled,
            WouldModifySystem: false,
            CopyElisionEnabled: copyElisionEnabled,
            TargetProcessId: 42,
            RingName: "test",
            RingAbiVersion: 1,
            QpcFrequency: 10_000_000,
            EventCount: 20,
            LostSequenceCount: 0,
            NativeOverrunCount: 0,
            EventTypeCounts: new Dictionary<string, long> { ["CopyResource"] = 6 },
            CopyResourceBytes: 49152,
            RedundantCopyCandidateCount: 3,
            RedundantCopyBytes: 24576,
            AvoidableCopySharePercent: 50,
            ForwardedCopyCount: forwardedCopies,
            ForwardedCopyBytes: 49152UL - (ulong)skippedCopies * 4096UL,
            SkippedCopyCount: skippedCopies,
            SkippedCopyBytes: (ulong)skippedCopies * 4096UL,
            ContentEquivalent: true,
            RollbackRestored: true,
            DestinationBufferHash: "buffer",
            DestinationTextureHash: "texture",
            QpcFrequencyFromTarget: 10_000_000,
            WorkloadQpcTicks: 1000,
            TargetReport: document.RootElement.Clone());
    }
}
