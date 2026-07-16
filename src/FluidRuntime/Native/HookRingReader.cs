using System.IO.MemoryMappedFiles;

namespace FluidRuntime.Native;

public sealed unsafe class HookRingReader : IDisposable
{
    public const uint ExpectedMagic = 0x47524C46;
    public const uint ExpectedAbiVersion = 5;
    public const int HeaderSize = 64;
    public const int ExpectedEventSize = 80;
    public const string MappingNamePrefix = "Local\\FluidRuntimeHook-";

    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _basePointer;
    private long _nextSequence;
    private bool _disposed;

    private HookRingReader(
        string mappingName,
        MemoryMappedFile mapping,
        MemoryMappedViewAccessor view)
    {
        MappingName = mappingName;
        _mapping = mapping;
        _view = view;

        var magic = _view.ReadUInt32(0);
        Thread.MemoryBarrier();
        AbiVersion = _view.ReadUInt32(4);
        Capacity = _view.ReadUInt32(8);
        EventSize = _view.ReadUInt32(12);
        QpcFrequency = _view.ReadUInt64(40);
        ProcessId = _view.ReadUInt64(48);
        if (magic != ExpectedMagic ||
            AbiVersion != ExpectedAbiVersion ||
            Capacity == 0 ||
            EventSize != ExpectedEventSize)
        {
            throw new InvalidDataException("The native hook ring header is incompatible.");
        }

        byte* pointer = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        _basePointer = pointer + _view.PointerOffset;
        _nextSequence = ReadInt64Atomic(24);
    }

    public string MappingName { get; }

    public uint AbiVersion { get; }

    public uint Capacity { get; }

    public uint EventSize { get; }

    public ulong QpcFrequency { get; }

    public ulong ProcessId { get; }

    public long LostSequenceCount { get; private set; }

    public long NativeOverrunCount => ReadInt64Atomic(32);

    public static HookRingReader OpenForProcess(int processId) =>
        Open(MappingNamePrefix + processId);

    public static HookRingReader Open(string mappingName)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Named hook rings require Windows.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(mappingName);

        var mapping = MemoryMappedFile.OpenExisting(
            mappingName,
            MemoryMappedFileRights.ReadWrite);
        try
        {
            var view = mapping.CreateViewAccessor(
                0,
                0,
                MemoryMappedFileAccess.ReadWrite);
            try
            {
                return new HookRingReader(mappingName, mapping, view);
            }
            catch
            {
                view.Dispose();
                throw;
            }
        }
        catch
        {
            mapping.Dispose();
            throw;
        }
    }

    public IReadOnlyList<HookIpcEvent> ReadAvailable()
    {
        var events = new List<HookIpcEvent>();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var upperSequence = ReadInt64Atomic(16);
        var earliestAvailable = Math.Max(0, upperSequence - Capacity);
        if (_nextSequence < earliestAvailable)
        {
            LostSequenceCount += earliestAvailable - _nextSequence;
            _nextSequence = earliestAvailable;
        }

        while (_nextSequence < upperSequence)
        {
            var slotIndex = _nextSequence % Capacity;
            var offset = HeaderSize + slotIndex * EventSize;
            var publishedBefore = ReadInt64Atomic(offset);
            if (publishedBefore != _nextSequence)
            {
                break;
            }

            Thread.MemoryBarrier();
            var item = new HookIpcEvent(
                Sequence: publishedBefore,
                QpcTicks: _view.ReadInt64(offset + 8),
                Type: (HookEventType)_view.ReadUInt32(offset + 16),
                ThreadId: _view.ReadUInt32(offset + 20),
                ResourceA: _view.ReadUInt64(offset + 24),
                ResourceB: _view.ReadUInt64(offset + 32),
                SizeBytes: _view.ReadUInt64(offset + 40),
                Generation: _view.ReadUInt64(offset + 48),
                Flags: _view.ReadUInt32(offset + 56),
                SubresourceA: _view.ReadUInt32(offset + 60),
                SubresourceB: _view.ReadUInt32(offset + 64),
                RegionKey: _view.ReadUInt64(offset + 72));
            Thread.MemoryBarrier();
            if (ReadInt64Atomic(offset) != publishedBefore)
            {
                break;
            }

            events.Add(item);
            ++_nextSequence;
        }

        Interlocked.Exchange(ref *(long*)(_basePointer + 24), _nextSequence);
        return events;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _mapping.Dispose();
    }

    private long ReadInt64Atomic(long offset) =>
        Interlocked.Read(ref *(long*)(_basePointer + offset));
}
