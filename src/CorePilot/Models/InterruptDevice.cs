namespace CorePilot.Models;

/// <summary>A present PnP device that participates in interrupt management.</summary>
public sealed class InterruptDevice
{
    public required string InstanceId { get; init; }
    public required string FriendlyName { get; init; }
    public required string ClassName { get; init; }
    public string? HardwareId { get; init; }
    public string? ServiceName { get; init; }
    public bool MsiSupported { get; init; }
    public bool IsStarted { get; init; }
    public bool IsBootCritical { get; init; }

    public string DisplayName =>
        string.IsNullOrWhiteSpace(FriendlyName) ? InstanceId : FriendlyName;
}

/// <summary>Snapshot of a device's Affinity Policy registry state.</summary>
public sealed class AffinityPolicyState
{
    /// <summary>Null when the DevicePolicy value is absent (machine default).</summary>
    public IrqPolicy? Policy { get; set; }

    /// <summary>Null when the DevicePriority value is absent.</summary>
    public IrqPriority? Priority { get; set; }

    /// <summary>Null when AssignmentSetOverride is absent.</summary>
    public GroupAffinityMask? Mask { get; set; }

    /// <summary>True when any of the three policy values exist in the registry.</summary>
    public bool HasAnySetting => Policy.HasValue || Priority.HasValue || Mask is not null;

    public AffinityPolicyState Clone() => new()
    {
        Policy = Policy,
        Priority = Priority,
        Mask = Mask?.Clone(),
    };
}
