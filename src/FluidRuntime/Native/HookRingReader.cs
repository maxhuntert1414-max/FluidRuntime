using System.Diagnostics;
using System.IO.MemoryMappedFiles;

namespace FluidRuntime.Native;

public sealed class HookRingReader : IDisposable
{
    public const uint ExpectedMagic = 0x47524C46;
    public const uint ExpectedAbiVersion = 6;
    public const uint ExpectedControlMagic = 0x4C544346;
    public const uint ExpectedControlAbiVersion = 1;
    public const int RingHeaderSize = 64;
    public const int ControlBlockSize = 64;
    public const int HeaderSize = RingHeaderSize + ControlBlockSize;
    public const int ExpectedEventSize = 80;
    public const uint ExpectedCapacity = 1024;
    public const int ExpectedMappingSize =
        HeaderSize + (int)ExpectedCapacity * ExpectedEventSize;
    public const string MappingNamePrefix = "Local\\FluidRuntimeHook-";
    public const ulong SkipRedundantCopyResourceAction = 1;

    private const int ControlPublishedEpochOffset = 72;
    private const int ControlAcknowledgedEpochOffset = 80;
    private const int ControlExpiresAtQpcOffset = 88;
    private const int ControlActionMaskOffset = 96;
    private const int ControlActionBudgetOffset = 104;
    private const int ControlAppliedActionCountOffset = 112;
    private const int ControlStatusOffset = 120;

    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _view;
    private readonly unsafe byte* _basePointer;
    private long _nextSequence;
    private bool _disposed;

    private unsafe HookRingReader(
        string mappingName,
        MemoryMappedFile mapping,
        MemoryMappedViewAccessor view)
    {
        MappingName = mappingName;
        _mapping = mapping;
        _view = view;

        if (_view.Capacity < ExpectedMappingSize)
        {
            throw new InvalidDataException("The native hook ring mapping size is incompatible.");
        }

        var magic = _view.ReadUInt32(0);
        Thread.MemoryBarrier();
        AbiVersion = _view.ReadUInt32(4);
        Capacity = _view.ReadUInt32(8);
        EventSize = _view.ReadUInt32(12);
        QpcFrequency = _view.ReadUInt64(40);
        ProcessId = _view.ReadUInt64(48);
        var controlMagic = _view.ReadUInt32(RingHeaderSize);
        var controlAbiVersion = _view.ReadUInt32(RingHeaderSize + 4);
        if (magic != ExpectedMagic ||
            AbiVersion != ExpectedAbiVersion ||
            controlMagic != ExpectedControlMagic ||
            controlAbiVersion != ExpectedControlAbiVersion ||
            Capacity != ExpectedCapacity ||
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

    public long NativeOverrunCount
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return ReadInt64Atomic(32);
        }
    }

    public HookControlSnapshot ControlSnapshot
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return ReadStableControlSnapshot();
        }
    }

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

    public unsafe IReadOnlyList<HookIpcEvent> ReadAvailable()
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

    public HookControlPolicy PublishCopyElisionPolicy(TimeSpan lifetime)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromSeconds(4))
        {
            throw new ArgumentOutOfRangeException(
                nameof(lifetime),
                "Control policy lifetime must be greater than zero and at most four seconds.");
        }
        if ((ulong)Stopwatch.Frequency != QpcFrequency)
        {
            throw new InvalidDataException(
                "Managed and native QPC frequencies do not match.");
        }
        const long epoch = 1;
        var lifetimeTicks = checked((long)Math.Ceiling(lifetime.TotalSeconds * QpcFrequency));
        var expiresAtQpc = checked(Stopwatch.GetTimestamp() + lifetimeTicks);
        const long publishingSentinel = -1;
        if (CompareExchangeInt64Atomic(
                ControlPublishedEpochOffset,
                publishingSentinel,
                comparand: 0) != 0)
        {
            throw new InvalidOperationException(
                "The v1 control block accepts one policy epoch per target process.");
        }

        try
        {
            WriteInt64Atomic(ControlAcknowledgedEpochOffset, 0);
            WriteInt64Atomic(ControlExpiresAtQpcOffset, expiresAtQpc);
            WriteInt64Atomic(ControlActionMaskOffset, (long)SkipRedundantCopyResourceAction);
            WriteInt64Atomic(ControlActionBudgetOffset, 1);
            WriteInt64Atomic(ControlAppliedActionCountOffset, 0);
            WriteInt64Atomic(ControlStatusOffset, (long)HookControlPolicyStatus.None);
            Thread.MemoryBarrier();
            WriteInt64Atomic(ControlPublishedEpochOffset, epoch);
        }
        catch
        {
            WriteInt64Atomic(ControlPublishedEpochOffset, 0);
            throw;
        }
        return new HookControlPolicy(
            epoch,
            expiresAtQpc,
            SkipRedundantCopyResourceAction,
            ActionBudget: 1);
    }

    public async Task<HookControlSnapshot> WaitForControlAcknowledgmentAsync(
        long epoch,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (epoch <= 0 || timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(epoch),
                "A positive epoch and timeout are required.");
        }

        var timeoutTicks = checked((long)Math.Ceiling(
            timeout.TotalSeconds * Stopwatch.Frequency));
        var deadline = checked(Stopwatch.GetTimestamp() + timeoutTicks);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = ControlSnapshot;
            if (snapshot.AcknowledgedEpoch == epoch)
            {
                if (snapshot.Status is HookControlPolicyStatus.Accepted or
                    HookControlPolicyStatus.Exhausted)
                {
                    return snapshot;
                }
                throw new InvalidDataException(
                    $"Native control policy was {snapshot.Status.ToString().ToLowerInvariant()}.");
            }
            await Task.Delay(5, cancellationToken);
        }

        throw new TimeoutException("Native control policy acknowledgment timed out.");
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

    private unsafe long ReadInt64Atomic(long offset) =>
        Interlocked.Read(ref *(long*)(_basePointer + offset));

    private HookControlSnapshot ReadStableControlSnapshot()
    {
        const int maximumAttempts = 8;
        for (var attempt = 0; attempt < maximumAttempts; ++attempt)
        {
            var first = new HookControlSnapshot(
                PublishedEpoch: ReadInt64Atomic(ControlPublishedEpochOffset),
                AcknowledgedEpoch: ReadInt64Atomic(ControlAcknowledgedEpochOffset),
                AppliedActionCount: ReadInt64Atomic(ControlAppliedActionCountOffset),
                Status: (HookControlPolicyStatus)ReadInt64Atomic(ControlStatusOffset));
            Thread.MemoryBarrier();
            var second = new HookControlSnapshot(
                PublishedEpoch: ReadInt64Atomic(ControlPublishedEpochOffset),
                AcknowledgedEpoch: ReadInt64Atomic(ControlAcknowledgedEpochOffset),
                AppliedActionCount: ReadInt64Atomic(ControlAppliedActionCountOffset),
                Status: (HookControlPolicyStatus)ReadInt64Atomic(ControlStatusOffset));
            if (first == second)
            {
                return second;
            }
        }

        throw new InvalidDataException("The native control policy state did not stabilize.");
    }

    private unsafe void WriteInt64Atomic(long offset, long value) =>
        Interlocked.Exchange(ref *(long*)(_basePointer + offset), value);

    private unsafe long CompareExchangeInt64Atomic(
        long offset,
        long value,
        long comparand) =>
        Interlocked.CompareExchange(ref *(long*)(_basePointer + offset), value, comparand);
}
