using FluidRuntime.Native;
using FluidRuntime.Runtime;

namespace FluidRuntime.Tests;

public sealed class ResourceLifecycleValidatorTests
{
    [Fact]
    public void Validate_accepts_create_retire_create_reuse_flow()
    {
        var result = ResourceLifecycleValidator.Validate(
            [
                Event(0, HookEventType.CreateBuffer, resourceA: 1),
                Event(1, HookEventType.CreateTexture2D, resourceA: 2),
                Event(2, HookEventType.ResourceRetire, resourceA: 1),
                Event(3, HookEventType.CreateBuffer, resourceA: 3),
                Event(4, HookEventType.ResourceReuse, resourceA: 1, resourceB: 3),
                Event(5, HookEventType.MapWrite, resourceA: 3),
                Event(6, HookEventType.UnmapWrite, resourceA: 3),
                Event(7, HookEventType.UpdateSubresource, resourceA: 2),
                Event(8, HookEventType.CopyResource, resourceA: 3, resourceB: 2),
                Event(9, HookEventType.CopySubresourceRegion, resourceA: 3, resourceB: 2),
                Event(10, HookEventType.ClearRenderTargetView, resourceA: 2),
                Event(11, HookEventType.ClearUnorderedAccessViewFloat, resourceA: 2),
                Event(12, HookEventType.Present),
                Event(13, HookEventType.HookRefresh)
            ]);

        Assert.True(result.IsValid, result.Error);
        Assert.Equal([2UL, 3UL], result.ActiveResourceIds);
        Assert.Equal([1UL], result.RetiredResourceIds);
    }

    [Fact]
    public void Validate_rejects_copy_after_retire()
    {
        var result = ResourceLifecycleValidator.Validate(
            [
                Event(0, HookEventType.CreateBuffer, resourceA: 1),
                Event(1, HookEventType.CreateBuffer, resourceA: 2),
                Event(2, HookEventType.ResourceRetire, resourceA: 1),
                Event(3, HookEventType.CopyResource, resourceA: 2, resourceB: 1)
            ]);

        Assert.False(result.IsValid);
        Assert.Equal([2UL], result.ActiveResourceIds);
        Assert.Equal([1UL], result.RetiredResourceIds);
    }

    [Fact]
    public void Validate_accepts_create_destroy_create_reuse_flow()
    {
        var result = ResourceLifecycleValidator.Validate(
            [
                Event(0, HookEventType.CreateTexture2D, resourceA: 1),
                Event(1, HookEventType.ResourceDestroy, resourceA: 1),
                Event(2, HookEventType.CreateBuffer, resourceA: 2),
                Event(3, HookEventType.ResourceReuse, resourceA: 1, resourceB: 2),
                Event(4, HookEventType.MapWrite, resourceA: 2)
            ]);

        Assert.True(result.IsValid, result.Error);
        Assert.Equal([2UL], result.ActiveResourceIds);
        Assert.Equal([1UL], result.RetiredResourceIds);
    }

    [Fact]
    public void Validate_rejects_destroy_of_inactive_id()
    {
        var result = ResourceLifecycleValidator.Validate(
            [
                Event(0, HookEventType.CreateBuffer, resourceA: 1),
                Event(1, HookEventType.ResourceDestroy, resourceA: 2)
            ]);

        Assert.False(result.IsValid);
        Assert.Contains("destroy", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_rejects_copy_and_write_after_destroy()
    {
        var copyResult = ResourceLifecycleValidator.Validate(
            [
                Event(0, HookEventType.CreateBuffer, resourceA: 1),
                Event(1, HookEventType.CreateBuffer, resourceA: 2),
                Event(2, HookEventType.ResourceDestroy, resourceA: 1),
                Event(3, HookEventType.CopyResource, resourceA: 2, resourceB: 1)
            ]);
        var writeResult = ResourceLifecycleValidator.Validate(
            [
                Event(0, HookEventType.CreateBuffer, resourceA: 1),
                Event(1, HookEventType.ResourceDestroy, resourceA: 1),
                Event(2, HookEventType.MapWrite, resourceA: 1)
            ]);

        Assert.False(copyResult.IsValid);
        Assert.False(writeResult.IsValid);
    }

    [Theory]
    [InlineData(HookEventType.ClearRenderTargetView)]
    [InlineData(HookEventType.ClearUnorderedAccessViewFloat)]
    public void Validate_rejects_gpu_view_write_after_destroy(HookEventType eventType)
    {
        var result = ResourceLifecycleValidator.Validate(
            [
                Event(0, HookEventType.CreateTexture2D, resourceA: 1),
                Event(1, HookEventType.ResourceDestroy, resourceA: 1),
                Event(2, eventType, resourceA: 1)
            ]);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_rejects_reuse_without_retire()
    {
        var result = ResourceLifecycleValidator.Validate(
            [
                Event(0, HookEventType.CreateBuffer, resourceA: 1),
                Event(1, HookEventType.CreateBuffer, resourceA: 2),
                Event(2, HookEventType.ResourceReuse, resourceA: 1, resourceB: 2)
            ]);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_rejects_duplicate_id()
    {
        var result = ResourceLifecycleValidator.Validate(
            [
                Event(0, HookEventType.CreateBuffer, resourceA: 1),
                Event(1, HookEventType.CreateTexture2D, resourceA: 1)
            ]);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_requires_monotonic_create_ids()
    {
        var result = ResourceLifecycleValidator.Validate(
            [
                Event(0, HookEventType.CreateBuffer, resourceA: 2),
                Event(1, HookEventType.CreateTexture2D, resourceA: 1)
            ]);

        Assert.False(result.IsValid);
    }

    private static HookIpcEvent Event(
        long sequence,
        HookEventType type,
        ulong resourceA = 0,
        ulong resourceB = 0) =>
        new(
            Sequence: sequence,
            QpcTicks: 1000 + sequence,
            Type: type,
            ThreadId: 7,
            ResourceA: resourceA,
            ResourceB: resourceB,
            SizeBytes: 0,
            Generation: 0,
            Flags: 0);
}
