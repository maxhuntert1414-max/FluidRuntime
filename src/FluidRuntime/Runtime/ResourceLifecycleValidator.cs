using FluidRuntime.Native;

namespace FluidRuntime.Runtime;

public sealed record ResourceLifecycleValidationResult(
    bool IsValid,
    IReadOnlyCollection<ulong> ActiveResourceIds,
    IReadOnlyCollection<ulong> RetiredResourceIds,
    string? Error = null);

public static class ResourceLifecycleValidator
{
    public static ResourceLifecycleValidationResult Validate(IEnumerable<HookIpcEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var active = new SortedSet<ulong>();
        var retired = new SortedSet<ulong>();
        var maxCreatedId = 0UL;

        foreach (var item in events)
        {
            switch (item.Type)
            {
                case HookEventType.CreateBuffer:
                case HookEventType.CreateTexture2D:
                    if (item.ResourceA == 0)
                    {
                        return Invalid("Created resource id must be non-zero.", active, retired);
                    }
                    if (active.Contains(item.ResourceA) || retired.Contains(item.ResourceA))
                    {
                        return Invalid("Created resource id must be unique.", active, retired);
                    }
                    if (item.ResourceA <= maxCreatedId)
                    {
                        return Invalid("Created resource ids must be monotonic.", active, retired);
                    }
                    active.Add(item.ResourceA);
                    maxCreatedId = item.ResourceA;
                    break;

                case HookEventType.ResourceRetire:
                    var retireResult = MoveActiveToRetired("retire", item, active, retired);
                    if (retireResult is not null)
                    {
                        return retireResult;
                    }
                    break;

                case HookEventType.ResourceDestroy:
                    var destroyResult = MoveActiveToRetired("destroy", item, active, retired);
                    if (destroyResult is not null)
                    {
                        return destroyResult;
                    }
                    break;

                case HookEventType.ResourceReuse:
                    if (item.ResourceA == 0 ||
                        item.ResourceB == 0 ||
                        item.ResourceA == item.ResourceB ||
                        !retired.Contains(item.ResourceA) ||
                        !active.Contains(item.ResourceB))
                    {
                        return Invalid(
                            "Resource reuse must reference one retired id and one active id.",
                            active,
                            retired);
                    }
                    break;

                case HookEventType.MapWrite:
                case HookEventType.UnmapWrite:
                case HookEventType.UpdateSubresource:
                case HookEventType.ClearRenderTargetView:
                case HookEventType.ClearUnorderedAccessViewFloat:
                    if (!active.Contains(item.ResourceA))
                    {
                        return Invalid("Resource write event must reference an active resource.", active, retired);
                    }
                    break;

                case HookEventType.CopyResource:
                case HookEventType.CopySubresourceRegion:
                    if (!active.Contains(item.ResourceA) || !active.Contains(item.ResourceB))
                    {
                        return Invalid("Copy event must reference active destination and source resources.", active, retired);
                    }
                    break;

                case HookEventType.Present:
                case HookEventType.HookRefresh:
                    break;

                default:
                    return Invalid("Unknown hook event type.", active, retired);
            }
        }

        return new ResourceLifecycleValidationResult(true, active.ToArray(), retired.ToArray());
    }

    private static ResourceLifecycleValidationResult Invalid(
        string error,
        IEnumerable<ulong> active,
        IEnumerable<ulong> retired) =>
        new(false, active.ToArray(), retired.ToArray(), error);

    private static ResourceLifecycleValidationResult? MoveActiveToRetired(
        string action,
        HookIpcEvent item,
        SortedSet<ulong> active,
        SortedSet<ulong> retired)
    {
        if (item.ResourceA == 0 || item.ResourceB != 0 || !active.Remove(item.ResourceA))
        {
            return Invalid($"Resource {action} must remove one active resource.", active, retired);
        }
        retired.Add(item.ResourceA);
        return null;
    }
}
