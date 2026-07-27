using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using FluidRuntime.Native;
using FluidRuntime.Runtime;

namespace FluidRuntime.Tests;

public sealed class HookRingReaderTests
{
    [Fact]
    public void Open_rejects_a_truncated_mapping_before_acquiring_unsafe_access()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var mappingName = $"Local\\FluidRuntimeHook-Test-{Guid.NewGuid():N}";
        var mappingSize = HookRingReader.HeaderSize + 2 * HookRingReader.ExpectedEventSize;
        using var mapping = MemoryMappedFile.CreateNew(
            mappingName,
            mappingSize,
            MemoryMappedFileAccess.ReadWrite);
        using var writer = mapping.CreateViewAccessor(
            0,
            mappingSize,
            MemoryMappedFileAccess.ReadWrite);
        WriteHeader(writer, (int)HookRingReader.ExpectedCapacity);

        Assert.Throws<InvalidDataException>(() => HookRingReader.Open(mappingName));
    }

    [Fact]
    public void Shared_state_properties_reject_reads_after_dispose()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var mappingName = $"Local\\FluidRuntimeHook-Test-{Guid.NewGuid():N}";
        using var mapping = MemoryMappedFile.CreateNew(
            mappingName,
            HookRingReader.ExpectedMappingSize,
            MemoryMappedFileAccess.ReadWrite);
        using var writer = mapping.CreateViewAccessor(
            0,
            HookRingReader.ExpectedMappingSize,
            MemoryMappedFileAccess.ReadWrite);
        WriteHeader(writer, (int)HookRingReader.ExpectedCapacity);
        var reader = HookRingReader.Open(mappingName);

        reader.Dispose();

        Assert.Throws<ObjectDisposedException>(() => reader.ControlSnapshot);
        Assert.Throws<ObjectDisposedException>(() => reader.NativeOverrunCount);
    }

    [Fact]
    public void ReadAvailable_reads_published_events_and_advances_shared_cursor()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var mappingName = $"Local\\FluidRuntimeHook-Test-{Guid.NewGuid():N}";
        const int capacity = (int)HookRingReader.ExpectedCapacity;
        const int mappingSize = HookRingReader.ExpectedMappingSize;
        using var mapping = MemoryMappedFile.CreateNew(
            mappingName,
            mappingSize,
            MemoryMappedFileAccess.ReadWrite);
        using var writer = mapping.CreateViewAccessor(
            0,
            mappingSize,
            MemoryMappedFileAccess.ReadWrite);
        WriteHeader(writer, capacity);
        WriteEvent(writer, 0, HookEventType.Present, sizeBytes: 0, flags: 0);
        WriteEvent(
            writer,
            1,
            HookEventType.CopySubresourceRegion,
            sizeBytes: 4096,
            flags: 1,
            subresourceA: 3,
            subresourceB: 2,
            regionKey: 0x123456789ABCDEF0UL);
        WriteEvent(
            writer,
            2,
            HookEventType.ClearUnorderedAccessViewFloat,
            sizeBytes: 1024,
            flags: 8,
            subresourceA: 1);
        writer.Write(16, 3L);

        using var reader = HookRingReader.Open(mappingName);
        var events = reader.ReadAvailable();

        Assert.Equal(3, events.Count);
        Assert.Equal(HookEventType.Present, events[0].Type);
        Assert.Equal(HookEventType.CopySubresourceRegion, events[1].Type);
        Assert.Equal(4096UL, events[1].SizeBytes);
        Assert.True(events[1].IsRedundantSubresourceCopyCandidate);
        Assert.Equal(3U, events[1].SubresourceA);
        Assert.Equal(2U, events[1].SubresourceB);
        Assert.Equal(0x123456789ABCDEF0UL, events[1].RegionKey);
        Assert.Equal(HookEventType.ClearUnorderedAccessViewFloat, events[2].Type);
        Assert.True(events[2].IsPreciseSubresourceWrite);
        Assert.Equal(1U, events[2].SubresourceA);
        Assert.Equal(3, writer.ReadInt64(24));
        Assert.Equal(0, reader.LostSequenceCount);
    }

    [Fact]
    public void ReadAvailable_reports_sequences_overwritten_before_consumption()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var mappingName = $"Local\\FluidRuntimeHook-Test-{Guid.NewGuid():N}";
        const int capacity = (int)HookRingReader.ExpectedCapacity;
        const int mappingSize = HookRingReader.ExpectedMappingSize;
        using var mapping = MemoryMappedFile.CreateNew(
            mappingName,
            mappingSize,
            MemoryMappedFileAccess.ReadWrite);
        using var writer = mapping.CreateViewAccessor(
            0,
            mappingSize,
            MemoryMappedFileAccess.ReadWrite);
        WriteHeader(writer, capacity);
        for (var sequence = 2; sequence < capacity + 2; ++sequence)
        {
            WriteEvent(
                writer,
                sequence,
                HookEventType.Present,
                sizeBytes: 0,
                flags: 0,
                slot: sequence % capacity);
        }
        writer.Write(16, (long)capacity + 2);

        using var reader = HookRingReader.Open(mappingName);
        var events = reader.ReadAvailable();

        Assert.Equal(capacity, events.Count);
        Assert.Equal(2, events[0].Sequence);
        Assert.Equal(capacity + 1, events[^1].Sequence);
        Assert.Equal(2, reader.LostSequenceCount);
        Assert.Equal(capacity + 2, writer.ReadInt64(24));
    }

    [Fact]
    public async Task OpenRingAsync_retries_until_the_native_header_is_published()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var mappingName = HookRingReader.MappingNamePrefix + Environment.ProcessId;
        const int capacity = (int)HookRingReader.ExpectedCapacity;
        const int mappingSize = HookRingReader.ExpectedMappingSize;
        using var mapping = MemoryMappedFile.CreateNew(
            mappingName,
            mappingSize,
            MemoryMappedFileAccess.ReadWrite);
        using var writer = mapping.CreateViewAccessor(
            0,
            mappingSize,
            MemoryMappedFileAccess.ReadWrite);
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var openTask = HookLabRunner.OpenRingAsync(process, cancellation.Token);
        await Task.Delay(50, cancellation.Token);
        WriteHeader(writer, capacity);

        using var reader = await openTask;
        Assert.Equal((ulong)Environment.ProcessId, reader.ProcessId);
    }

    [Fact]
    public async Task Control_policy_is_published_once_and_requires_native_acknowledgment()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var mappingName = $"Local\\FluidRuntimeHook-Test-{Guid.NewGuid():N}";
        const int capacity = (int)HookRingReader.ExpectedCapacity;
        const int mappingSize = HookRingReader.ExpectedMappingSize;
        using var mapping = MemoryMappedFile.CreateNew(
            mappingName,
            mappingSize,
            MemoryMappedFileAccess.ReadWrite);
        using var writer = mapping.CreateViewAccessor(
            0,
            mappingSize,
            MemoryMappedFileAccess.ReadWrite);
        WriteHeader(writer, capacity);
        using var reader = HookRingReader.Open(mappingName);

        var policy = reader.PublishCopyElisionPolicy(TimeSpan.FromSeconds(1));

        Assert.Equal(1, policy.Epoch);
        Assert.Equal(1, writer.ReadInt64(72));
        Assert.Equal((long)HookRingReader.SkipRedundantCopyResourceAction, writer.ReadInt64(96));
        Assert.Equal(1, writer.ReadInt64(104));
        Assert.Throws<InvalidOperationException>(() =>
            reader.PublishCopyElisionPolicy(TimeSpan.FromSeconds(1)));

        writer.Write(112, 1L);
        writer.Write(120, (long)HookControlPolicyStatus.Exhausted);
        Thread.MemoryBarrier();
        writer.Write(80, 1L);
        var acknowledged = await reader.WaitForControlAcknowledgmentAsync(
            policy.Epoch,
            TimeSpan.FromSeconds(1));

        Assert.Equal(policy.Epoch, acknowledged.AcknowledgedEpoch);
        Assert.Equal(1, acknowledged.AppliedActionCount);
        Assert.Equal(HookControlPolicyStatus.Exhausted, acknowledged.Status);
    }

    [Fact]
    public void Readback_policy_publishes_the_dedicated_action()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var mappingName = $"Local\\FluidRuntimeHook-Test-{Guid.NewGuid():N}";
        const int capacity = (int)HookRingReader.ExpectedCapacity;
        const int mappingSize = HookRingReader.ExpectedMappingSize;
        using var mapping = MemoryMappedFile.CreateNew(
            mappingName,
            mappingSize,
            MemoryMappedFileAccess.ReadWrite);
        using var writer = mapping.CreateViewAccessor(
            0,
            mappingSize,
            MemoryMappedFileAccess.ReadWrite);
        WriteHeader(writer, capacity);
        using var reader = HookRingReader.Open(mappingName);

        var policy = reader.PublishReadbackElisionPolicy(
            TimeSpan.FromSeconds(1),
            actionBudget: 64);

        Assert.Equal(HookRingReader.SkipRedundantReadbackCopyAction, policy.ActionMask);
        Assert.Equal(64UL, policy.ActionBudget);
        Assert.Equal(
            (long)HookRingReader.SkipRedundantReadbackCopyAction,
            writer.ReadInt64(96));
        Assert.Equal(64, writer.ReadInt64(104));
    }

    [Fact]
    public async Task Control_policy_rejects_invalid_lifetime_and_native_rejection()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var mappingName = $"Local\\FluidRuntimeHook-Test-{Guid.NewGuid():N}";
        const int capacity = (int)HookRingReader.ExpectedCapacity;
        const int mappingSize = HookRingReader.ExpectedMappingSize;
        using var mapping = MemoryMappedFile.CreateNew(
            mappingName,
            mappingSize,
            MemoryMappedFileAccess.ReadWrite);
        using var writer = mapping.CreateViewAccessor(
            0,
            mappingSize,
            MemoryMappedFileAccess.ReadWrite);
        WriteHeader(writer, capacity);
        using var reader = HookRingReader.Open(mappingName);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            reader.PublishCopyElisionPolicy(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            reader.PublishCopyElisionPolicy(TimeSpan.FromSeconds(5)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            reader.PublishCopyElisionPolicy(TimeSpan.FromSeconds(1), actionBudget: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            reader.PublishCopyElisionPolicy(
                TimeSpan.FromSeconds(1),
                HookRingReader.MaxControlActionBudget + 1));

        var policy = reader.PublishCopyElisionPolicy(TimeSpan.FromSeconds(1));
        writer.Write(120, (long)HookControlPolicyStatus.Rejected);
        Thread.MemoryBarrier();
        writer.Write(80, policy.Epoch);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            reader.WaitForControlAcknowledgmentAsync(
                policy.Epoch,
                TimeSpan.FromSeconds(1)));
        Assert.Contains("rejected", error.Message);
    }

    [Fact]
    public async Task Control_policy_allows_only_one_concurrent_publisher()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var mappingName = $"Local\\FluidRuntimeHook-Test-{Guid.NewGuid():N}";
        const int capacity = (int)HookRingReader.ExpectedCapacity;
        const int mappingSize = HookRingReader.ExpectedMappingSize;
        using var mapping = MemoryMappedFile.CreateNew(
            mappingName,
            mappingSize,
            MemoryMappedFileAccess.ReadWrite);
        using var writer = mapping.CreateViewAccessor(
            0,
            mappingSize,
            MemoryMappedFileAccess.ReadWrite);
        WriteHeader(writer, capacity);
        using var firstReader = HookRingReader.Open(mappingName);
        using var secondReader = HookRingReader.Open(mappingName);
        using var gate = new Barrier(2);

        async Task<Exception?> PublishAsync(HookRingReader reader)
        {
            await Task.Yield();
            gate.SignalAndWait();
            try
            {
                reader.PublishCopyElisionPolicy(TimeSpan.FromSeconds(1));
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }

        var results = await Task.WhenAll(
            PublishAsync(firstReader),
            PublishAsync(secondReader));

        Assert.Single(results, result => result is null);
        Assert.Single(results, result => result is InvalidOperationException);
        Assert.Equal(1, writer.ReadInt64(72));
    }

    [Fact]
    public void Control_policy_publishes_the_maximum_bounded_action_budget()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var mappingName = $"Local\\FluidRuntimeHook-Test-{Guid.NewGuid():N}";
        using var mapping = MemoryMappedFile.CreateNew(
            mappingName,
            HookRingReader.ExpectedMappingSize,
            MemoryMappedFileAccess.ReadWrite);
        using var writer = mapping.CreateViewAccessor(
            0,
            HookRingReader.ExpectedMappingSize,
            MemoryMappedFileAccess.ReadWrite);
        WriteHeader(writer, (int)HookRingReader.ExpectedCapacity);
        using var reader = HookRingReader.Open(mappingName);

        var policy = reader.PublishCopyElisionPolicy(
            TimeSpan.FromSeconds(1),
            HookRingReader.MaxControlActionBudget);

        Assert.Equal(HookRingReader.MaxControlActionBudget, policy.ActionBudget);
        Assert.Equal(
            (long)HookRingReader.MaxControlActionBudget,
            writer.ReadInt64(104));
    }

    private static void WriteHeader(MemoryMappedViewAccessor writer, int capacity)
    {
        writer.Write(0, 0U);
        writer.Write(4, HookRingReader.ExpectedAbiVersion);
        writer.Write(8, (uint)capacity);
        writer.Write(12, (uint)HookRingReader.ExpectedEventSize);
        writer.Write(16, 0L);
        writer.Write(24, 0L);
        writer.Write(32, 0L);
        writer.Write(40, (ulong)Stopwatch.Frequency);
        writer.Write(48, (ulong)Environment.ProcessId);
        writer.Write(64, HookRingReader.ExpectedControlMagic);
        writer.Write(68, HookRingReader.ExpectedControlAbiVersion);
        Thread.MemoryBarrier();
        writer.Write(0, HookRingReader.ExpectedMagic);
    }

    private static void WriteEvent(
        MemoryMappedViewAccessor writer,
        int sequence,
        HookEventType type,
        ulong sizeBytes,
        uint flags,
        int? slot = null,
        uint subresourceA = 0,
        uint subresourceB = 0,
        ulong regionKey = 0)
    {
        var offset = HookRingReader.HeaderSize +
            (slot ?? sequence) * HookRingReader.ExpectedEventSize;
        writer.Write(offset + 8, 1000L + sequence);
        writer.Write(offset + 16, (uint)type);
        writer.Write(offset + 20, 7U);
        writer.Write(offset + 24, 1UL);
        writer.Write(offset + 32, 2UL);
        writer.Write(offset + 40, sizeBytes);
        writer.Write(offset + 48, 3UL);
        writer.Write(offset + 56, flags);
        writer.Write(offset + 60, subresourceA);
        writer.Write(offset + 64, subresourceB);
        writer.Write(offset + 68, 0U);
        writer.Write(offset + 72, regionKey);
        Thread.MemoryBarrier();
        writer.Write(offset, (long)sequence);
    }
}
