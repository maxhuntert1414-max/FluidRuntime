using FluidRuntime.Native;
using FluidRuntime.Runtime;

namespace FluidRuntime.Tests;

public sealed class HookLabRunnerTests
{
    [Fact]
    public void Deterministic_workload_rejects_a_swapped_copy_pair()
    {
        var events = BuildDeterministicWorkload();
        Assert.True(HookLabRunner.MatchesDeterministicWorkload(events, 1));

        events[5] = events[5] with
        {
            ResourceA = events[5].ResourceB,
            ResourceB = events[5].ResourceA
        };

        Assert.False(HookLabRunner.MatchesDeterministicWorkload(events, 1));
    }

    [Fact]
    public void Deterministic_workload_accepts_only_the_first_redundant_copy_as_skipped()
    {
        var events = BuildDeterministicWorkload();
        events[6] = events[6] with { Flags = 3 };

        Assert.True(HookLabRunner.MatchesDeterministicWorkload(events, 1, true));

        events[9] = events[9] with { Flags = 3 };
        Assert.False(HookLabRunner.MatchesDeterministicWorkload(events, 1, true));
    }

    [Fact]
    public void Deterministic_workload_rejects_order_generation_flags_and_refresh_drift()
    {
        var reordered = BuildDeterministicWorkload();
        (reordered[5], reordered[6]) = (reordered[6], reordered[5]);
        Assert.False(HookLabRunner.MatchesDeterministicWorkload(reordered, 1));

        var wrongGeneration = BuildDeterministicWorkload();
        wrongGeneration[5] = wrongGeneration[5] with { Generation = 99 };
        Assert.False(HookLabRunner.MatchesDeterministicWorkload(wrongGeneration, 1));

        var wrongFlags = BuildDeterministicWorkload();
        wrongFlags[5] = wrongFlags[5] with { Flags = 1 };
        Assert.False(HookLabRunner.MatchesDeterministicWorkload(wrongFlags, 1));

        var invalidRefresh = BuildDeterministicWorkload();
        invalidRefresh.Insert(5, new HookIpcEvent(
            Sequence: 5,
            QpcTicks: 1005,
            Type: HookEventType.HookRefresh,
            ThreadId: 7,
            ResourceA: 2,
            ResourceB: 0,
            SizeBytes: 0,
            Generation: 1,
            Flags: 0));
        Assert.False(HookLabRunner.MatchesDeterministicWorkload(invalidRefresh, 1));
    }

    [Fact]
    public void Deterministic_workload_accepts_lifecycle_without_pointer_reuse()
    {
        var events = BuildDeterministicWorkload();
        events.RemoveAll(item => item.Type == HookEventType.ResourceReuse);

        Assert.True(HookLabRunner.MatchesDeterministicWorkload(events, 1));
    }

    private static List<HookIpcEvent> BuildDeterministicWorkload()
    {
        var definitions = new[]
        {
            (HookEventType.CreateBuffer, 1UL, 0UL, 4096UL, 1UL, 0U),
            (HookEventType.CreateBuffer, 2UL, 0UL, 4096UL, 0UL, 0U),
            (HookEventType.CreateBuffer, 3UL, 0UL, 4096UL, 0UL, 0U),
            (HookEventType.MapWrite, 3UL, 0UL, 4096UL, 0UL, 4U),
            (HookEventType.UnmapWrite, 3UL, 0UL, 4096UL, 1UL, 0U),
            (HookEventType.CopyResource, 2UL, 1UL, 4096UL, 1UL, 0U),
            (HookEventType.CopyResource, 2UL, 1UL, 4096UL, 2UL, 1U),
            (HookEventType.UpdateSubresource, 1UL, 0UL, 4096UL, 2UL, 0U),
            (HookEventType.CopyResource, 2UL, 1UL, 4096UL, 3UL, 0U),
            (HookEventType.CopyResource, 2UL, 1UL, 4096UL, 4UL, 1U),
            (HookEventType.CreateTexture2D, 4UL, 0UL, 16384UL, 1UL, 0U),
            (HookEventType.CreateTexture2D, 5UL, 0UL, 16384UL, 0UL, 0U),
            (HookEventType.CopyResource, 5UL, 4UL, 16384UL, 1UL, 0U),
            (HookEventType.CopyResource, 5UL, 4UL, 16384UL, 2UL, 1U),
            (HookEventType.CreateBuffer, 6UL, 0UL, 256UL, 0UL, 0U),
            (HookEventType.ResourceRetire, 6UL, 0UL, 256UL, 0UL, 0U),
            (HookEventType.CreateBuffer, 7UL, 0UL, 256UL, 0UL, 0U),
            (HookEventType.ResourceReuse, 6UL, 7UL, 256UL, 0UL, 0U),
            (HookEventType.ResourceRetire, 7UL, 0UL, 256UL, 0UL, 0U),
            (HookEventType.Present, 0UL, 0UL, 0UL, 1UL, 0U)
        };
        return definitions.Select((item, index) => new HookIpcEvent(
            Sequence: index,
            QpcTicks: 1000 + index,
            Type: item.Item1,
            ThreadId: 7,
            ResourceA: item.Item2,
            ResourceB: item.Item3,
            SizeBytes: item.Item4,
            Generation: item.Item5,
            Flags: item.Item6)).ToList();
    }
}
