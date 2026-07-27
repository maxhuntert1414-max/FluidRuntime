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
        ApplyCopyElision(events);

        Assert.True(HookLabRunner.MatchesDeterministicWorkload(events, 1, true));

        events[9] = events[9] with { Flags = 3 };
        Assert.False(HookLabRunner.MatchesDeterministicWorkload(events, 1, true));
    }

    [Fact]
    public void Deterministic_workload_requires_managed_policy_acceptance_event()
    {
        var events = BuildDeterministicWorkload();
        ApplyCopyElision(events);
        events.Insert(0, new HookIpcEvent(
            Sequence: 0,
            QpcTicks: 900,
            Type: HookEventType.ControlPolicyAccepted,
            ThreadId: 7,
            ResourceA: 1,
            ResourceB: HookRingReader.SkipRedundantCopyResourceAction,
            SizeBytes: 1,
            Generation: 2000,
            Flags: 0));

        Assert.True(HookLabRunner.MatchesDeterministicWorkload(
            events,
            expectedPresentCount: 1,
            copyElisionEnabled: true,
            managedControlPolicy: true));

        events.RemoveAt(0);
        Assert.False(HookLabRunner.MatchesDeterministicWorkload(
            events,
            expectedPresentCount: 1,
            copyElisionEnabled: true,
            managedControlPolicy: true));
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

        var wrongSubresource = BuildDeterministicWorkload();
        wrongSubresource[16] = wrongSubresource[16] with { SubresourceB = 0 };
        Assert.False(HookLabRunner.MatchesDeterministicWorkload(wrongSubresource, 1));

        var wrongRegion = BuildDeterministicWorkload();
        wrongRegion[25] = wrongRegion[25] with { RegionKey = wrongRegion[24].RegionKey };
        Assert.False(HookLabRunner.MatchesDeterministicWorkload(wrongRegion, 1));

        var impreciseGpuWrite = BuildDeterministicWorkload();
        impreciseGpuWrite[21] = impreciseGpuWrite[21] with { Flags = 0 };
        Assert.False(HookLabRunner.MatchesDeterministicWorkload(impreciseGpuWrite, 1));

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
        var definitions = new List<(
            HookEventType Type,
            ulong ResourceA,
            ulong ResourceB,
            ulong SizeBytes,
            ulong Generation,
            uint Flags,
            uint SubresourceA,
            uint SubresourceB)>
        {
            (HookEventType.CreateBuffer, 1UL, 0UL, 4096UL, 1UL, 0U, 0U, 0U),
            (HookEventType.CreateBuffer, 2UL, 0UL, 4096UL, 0UL, 0U, 0U, 0U),
            (HookEventType.CreateBuffer, 3UL, 0UL, 4096UL, 0UL, 0U, 0U, 0U),
            (HookEventType.MapWrite, 3UL, 0UL, 4096UL, 0UL, 4U, 0U, 0U),
            (HookEventType.UnmapWrite, 3UL, 0UL, 4096UL, 1UL, 0U, 0U, 0U),
            (HookEventType.CopyResource, 2UL, 1UL, 4096UL, 1UL, 0U, 0U, 0U),
            (HookEventType.CopyResource, 2UL, 1UL, 4096UL, 2UL, 1U, 0U, 0U),
            (HookEventType.UpdateSubresource, 1UL, 0UL, 4096UL, 2UL, 0U, 0U, 0U),
            (HookEventType.CopyResource, 2UL, 1UL, 4096UL, 3UL, 0U, 0U, 0U),
            (HookEventType.CopyResource, 2UL, 1UL, 4096UL, 4UL, 1U, 0U, 0U),
            (HookEventType.CreateTexture2D, 4UL, 0UL, 16384UL, 1UL, 0U, 0U, 0U),
            (HookEventType.CreateTexture2D, 5UL, 0UL, 16384UL, 0UL, 0U, 0U, 0U),
            (HookEventType.CopyResource, 5UL, 4UL, 16384UL, 1UL, 0U, 0U, 0U),
            (HookEventType.CopyResource, 5UL, 4UL, 16384UL, 2UL, 1U, 0U, 0U),
            (HookEventType.CreateTexture2D, 6UL, 0UL, 5120UL, 1UL, 0U, 0U, 0U),
            (HookEventType.CreateTexture2D, 7UL, 0UL, 5120UL, 0UL, 0U, 0U, 0U),
            (HookEventType.CopySubresourceRegion, 7UL, 6UL, 1024UL, 1UL, 0U, 1U, 1U),
            (HookEventType.CopySubresourceRegion, 7UL, 6UL, 0UL, 1UL, 0U, 1U, 1U),
            (HookEventType.CopySubresourceRegion, 7UL, 6UL, 1024UL, 2UL, 1U, 1U, 1U),
            (HookEventType.UpdateSubresource, 6UL, 0UL, 4096UL, 2UL, 0U, 0U, 0U),
            (HookEventType.CopySubresourceRegion, 7UL, 6UL, 1024UL, 3UL, 1U, 1U, 1U),
            (HookEventType.ClearRenderTargetView, 6UL, 0UL, 4096UL, 3UL, 8U, 0U, 0U),
            (HookEventType.CopySubresourceRegion, 7UL, 6UL, 1024UL, 4UL, 1U, 1U, 1U),
            (HookEventType.UpdateSubresource, 6UL, 0UL, 1024UL, 4UL, 0U, 1U, 0U),
            (HookEventType.CopySubresourceRegion, 7UL, 6UL, 256UL, 5UL, 0U, 1U, 1U),
            (HookEventType.CopySubresourceRegion, 7UL, 6UL, 256UL, 6UL, 0U, 1U, 1U),
            (HookEventType.CopySubresourceRegion, 7UL, 6UL, 1024UL, 7UL, 0U, 1U, 1U),
            (HookEventType.CopySubresourceRegion, 7UL, 6UL, 1024UL, 8UL, 1U, 1U, 1U),
            (HookEventType.ClearUnorderedAccessViewFloat, 6UL, 0UL, 1024UL, 5UL, 8U, 1U, 0U),
            (HookEventType.CopySubresourceRegion, 7UL, 6UL, 1024UL, 9UL, 0U, 1U, 1U),
            (HookEventType.CopySubresourceRegion, 7UL, 6UL, 1024UL, 10UL, 1U, 1U, 1U),
            (HookEventType.CreateBuffer, 8UL, 0UL, 256UL, 0UL, 0U, 0U, 0U),
            (HookEventType.ResourceRetire, 8UL, 0UL, 256UL, 0UL, 0U, 0U, 0U)
        };
        for (var cycle = 0; cycle < 64; ++cycle)
        {
            var resourceId = (ulong)(9 + cycle);
            definitions.Add((HookEventType.CreateBuffer, resourceId, 0, 512, 0, 0, 0, 0));
            definitions.Add((
                HookEventType.ResourceReuse,
                resourceId - 1,
                resourceId,
                512,
                0,
                0,
                0,
                0));
            definitions.Add((HookEventType.ResourceDestroy, resourceId, 0, 512, 0, 0, 0, 0));
        }
        definitions.Add((HookEventType.Present, 0, 0, 0, 1, 0, 0, 0));

        var events = definitions.Select((item, index) => new HookIpcEvent(
            Sequence: index,
            QpcTicks: 1000 + index,
            Type: item.Type,
            ThreadId: 7,
            ResourceA: item.ResourceA,
            ResourceB: item.ResourceB,
            SizeBytes: item.SizeBytes,
            Generation: item.Generation,
            Flags: item.Flags,
            SubresourceA: item.SubresourceA,
            SubresourceB: item.SubresourceB)).ToList();
        const ulong fullRegion = 100;
        events[16] = events[16] with { RegionKey = fullRegion };
        events[17] = events[17] with { RegionKey = 200 };
        events[18] = events[18] with { RegionKey = fullRegion };
        events[20] = events[20] with { RegionKey = fullRegion };
        events[22] = events[22] with { RegionKey = fullRegion };
        events[24] = events[24] with { RegionKey = 300 };
        events[25] = events[25] with { RegionKey = 400 };
        events[26] = events[26] with { RegionKey = fullRegion };
        events[27] = events[27] with { RegionKey = fullRegion };
        events[29] = events[29] with { RegionKey = fullRegion };
        events[30] = events[30] with { RegionKey = fullRegion };
        return events;
    }

    private static void ApplyCopyElision(List<HookIpcEvent> events)
    {
        events[6] = events[6] with { Generation = 1, Flags = 3 };
        events[8] = events[8] with { Generation = 2 };
        events[9] = events[9] with { Generation = 3 };
    }
}
