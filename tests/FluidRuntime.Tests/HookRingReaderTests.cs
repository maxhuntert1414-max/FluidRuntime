using System.IO.MemoryMappedFiles;
using FluidRuntime.Native;
using FluidRuntime.Runtime;

namespace FluidRuntime.Tests;

public sealed class HookRingReaderTests
{
    [Fact]
    public void ReadAvailable_reads_published_events_and_advances_shared_cursor()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var mappingName = $"Local\\FluidRuntimeHook-Test-{Guid.NewGuid():N}";
        const int capacity = 2;
        const int mappingSize = HookRingReader.HeaderSize +
            capacity * HookRingReader.ExpectedEventSize;
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
        WriteEvent(writer, 1, HookEventType.CopyResource, sizeBytes: 4096, flags: 1);
        writer.Write(16, 2L);

        using var reader = HookRingReader.Open(mappingName);
        var events = reader.ReadAvailable();

        Assert.Equal(2, events.Count);
        Assert.Equal(HookEventType.Present, events[0].Type);
        Assert.Equal(HookEventType.CopyResource, events[1].Type);
        Assert.Equal(4096UL, events[1].SizeBytes);
        Assert.True(events[1].IsRedundantCopyCandidate);
        Assert.Equal(2, writer.ReadInt64(24));
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
        const int capacity = 2;
        const int mappingSize = HookRingReader.HeaderSize +
            capacity * HookRingReader.ExpectedEventSize;
        using var mapping = MemoryMappedFile.CreateNew(
            mappingName,
            mappingSize,
            MemoryMappedFileAccess.ReadWrite);
        using var writer = mapping.CreateViewAccessor(
            0,
            mappingSize,
            MemoryMappedFileAccess.ReadWrite);
        WriteHeader(writer, capacity);
        WriteEvent(writer, 2, HookEventType.Present, sizeBytes: 0, flags: 0, slot: 0);
        WriteEvent(writer, 3, HookEventType.Present, sizeBytes: 0, flags: 0, slot: 1);
        writer.Write(16, 4L);

        using var reader = HookRingReader.Open(mappingName);
        var events = reader.ReadAvailable();

        Assert.Equal([2L, 3L], events.Select(item => item.Sequence));
        Assert.Equal(2, reader.LostSequenceCount);
        Assert.Equal(4, writer.ReadInt64(24));
    }

    [Fact]
    public async Task OpenRingAsync_retries_until_the_native_header_is_published()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var mappingName = HookRingReader.MappingNamePrefix + Environment.ProcessId;
        const int capacity = 2;
        const int mappingSize = HookRingReader.HeaderSize +
            capacity * HookRingReader.ExpectedEventSize;
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

    private static void WriteHeader(MemoryMappedViewAccessor writer, int capacity)
    {
        writer.Write(0, 0U);
        writer.Write(4, HookRingReader.ExpectedAbiVersion);
        writer.Write(8, (uint)capacity);
        writer.Write(12, (uint)HookRingReader.ExpectedEventSize);
        writer.Write(16, 0L);
        writer.Write(24, 0L);
        writer.Write(32, 0L);
        writer.Write(40, 10_000_000UL);
        writer.Write(48, (ulong)Environment.ProcessId);
        Thread.MemoryBarrier();
        writer.Write(0, HookRingReader.ExpectedMagic);
    }

    private static void WriteEvent(
        MemoryMappedViewAccessor writer,
        int sequence,
        HookEventType type,
        ulong sizeBytes,
        uint flags,
        int? slot = null)
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
        Thread.MemoryBarrier();
        writer.Write(offset, (long)sequence);
    }
}
