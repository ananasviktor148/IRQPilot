using System.Text.Json.Serialization;

namespace CorePilot.Models;

/// <summary>JSON-serializable form of an affinity assignment (profiles + backups).</summary>
public sealed class AffinityAssignment
{
    [JsonPropertyName("policy")]
    public IrqPolicy? Policy { get; set; }

    [JsonPropertyName("priority")]
    public IrqPriority? Priority { get; set; }

    /// <summary>Mask as "group:hexMask" pairs, e.g. ["0:0xC0", "1:0x3"].</summary>
    [JsonPropertyName("mask")]
    public List<string> Mask { get; set; } = new();

    public static AffinityAssignment FromMask(IrqPolicy? policy, IrqPriority? priority, GroupAffinityMask? mask)
    {
        var assignment = new AffinityAssignment { Policy = policy, Priority = priority };
        if (mask is not null)
        {
            foreach (var (group, value) in mask.Groups.OrderBy(kv => kv.Key))
                assignment.Mask.Add($"{group}:0x{value:X}");
        }
        return assignment;
    }

    public GroupAffinityMask ToMask()
    {
        var mask = new GroupAffinityMask();
        foreach (var entry in Mask)
        {
            var parts = entry.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2) continue;
            if (!ushort.TryParse(parts[0], out var group)) continue;
            var hex = parts[1].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? parts[1][2..] : parts[1];
            if (!ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var value)) continue;
            for (var i = 0; i < GroupAffinityMask.MaxProcessorsPerGroup; i++)
                if ((value & (1UL << i)) != 0) mask.Add(group, i);
        }
        return mask;
    }
}

public sealed class CorePilotProfile
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("createdUtc")]
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Device instance id -> assignment.</summary>
    [JsonPropertyName("devices")]
    public Dictionary<string, AffinityAssignment> Devices { get; set; } = new();
}

/// <summary>
/// Backup of pre-existing registry state for one device, captured before the
/// app writes anything. Restore deletes values we wrote and re-creates values
/// that existed before, with their original type and data.
/// </summary>
public sealed class DeviceBackup
{
    [JsonPropertyName("instanceId")]
    public string InstanceId { get; set; } = "";

    [JsonPropertyName("backedUpUtc")]
    public DateTime BackedUpUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Values that existed before our write (name -> typed data).</summary>
    [JsonPropertyName("originalValues")]
    public List<BackedUpValue> OriginalValues { get; set; } = new();
}

public sealed class BackedUpValue
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>"DWord" | "QWord" | "Binary"</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("dword")]
    public uint Dword { get; set; }

    [JsonPropertyName("qword")]
    public ulong Qword { get; set; }

    [JsonPropertyName("binary")]
    public byte[]? Binary { get; set; }
}
