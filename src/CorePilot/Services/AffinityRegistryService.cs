using CorePilot.Models;
using Microsoft.Win32;

namespace CorePilot.Services;

/// <summary>
/// Reads and writes the documented Interrupt Affinity Policy registry values:
///   HKLM\SYSTEM\CurrentControlSet\Enum\{instanceId}\Device Parameters\
///     Interrupt Management\Affinity Policy
///     -> DevicePolicy (REG_DWORD), DevicePriority (REG_DWORD),
///        AssignmentSetOverride (REG_DWORD | REG_QWORD | REG_BINARY KAFFINITY)
/// </summary>
public sealed class AffinityRegistryService
{
    public const string DevicePolicyValueName = "DevicePolicy";
    public const string DevicePriorityValueName = "DevicePriority";
    public const string AssignmentSetOverrideValueName = "AssignmentSetOverride";

    public static readonly string[] ManagedValueNames =
    {
        DevicePolicyValueName, DevicePriorityValueName, AssignmentSetOverrideValueName,
    };

    private static string AffinityPolicyKeyPath(string instanceId) =>
        $@"SYSTEM\CurrentControlSet\Enum\{instanceId}\Device Parameters\Interrupt Management\Affinity Policy";

    public AffinityPolicyState Read(string instanceId)
    {
        var state = new AffinityPolicyState();
        using var key = Registry.LocalMachine.OpenSubKey(AffinityPolicyKeyPath(instanceId), writable: false);
        if (key is null)
            return state;

        var valueNames = key.GetValueNames();

        if (valueNames.Contains(DevicePolicyValueName)
            && key.GetValue(DevicePolicyValueName) is int policy && IrqPolicyInfo.IsDefined((uint)policy))
            state.Policy = (IrqPolicy)(uint)policy;

        if (valueNames.Contains(DevicePriorityValueName)
            && key.GetValue(DevicePriorityValueName) is int priority && priority <= 4)
            state.Priority = (IrqPriority)(uint)priority;

        if (valueNames.Contains(AssignmentSetOverrideValueName))
        {
            var maskRaw = key.GetValue(AssignmentSetOverrideValueName);
            var maskKind = key.GetValueKind(AssignmentSetOverrideValueName);
            state.Mask = maskKind switch
            {
                RegistryValueKind.DWord when maskRaw is int d => GroupAffinityMask.FromDword((uint)d),
                RegistryValueKind.QWord when maskRaw is long q => GroupAffinityMask.FromQword((ulong)q),
                RegistryValueKind.Binary when maskRaw is byte[] b => SafeDecodeBinary(b),
                _ => null,
            };
        }
        return state;
    }

    private static GroupAffinityMask? SafeDecodeBinary(byte[] bytes)
    {
        try { return GroupAffinityMask.FromBinary(bytes); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>Capture the current values of everything we may overwrite.</summary>
    public DeviceBackup CaptureBackup(string instanceId)
    {
        var backup = new DeviceBackup { InstanceId = instanceId, BackedUpUtc = DateTime.UtcNow };
        using var key = Registry.LocalMachine.OpenSubKey(AffinityPolicyKeyPath(instanceId), writable: false);
        if (key is null)
            return backup;

        foreach (var name in ManagedValueNames)
        {
            if (!key.GetValueNames().Contains(name)) continue;
            var raw = key.GetValue(name);
            if (raw is null) continue;
            var backed = key.GetValueKind(name) switch
            {
                RegistryValueKind.DWord when raw is int d =>
                    new BackedUpValue { Name = name, Kind = "DWord", Dword = (uint)d },
                RegistryValueKind.QWord when raw is long q =>
                    new BackedUpValue { Name = name, Kind = "QWord", Qword = (ulong)q },
                RegistryValueKind.Binary when raw is byte[] b =>
                    new BackedUpValue { Name = name, Kind = "Binary", Binary = b },
                _ => null,
            };
            if (backed is not null)
                backup.OriginalValues.Add(backed);
        }
        return backup;
    }

    public void Write(string instanceId, IrqPolicy policy, IrqPriority? priority, GroupAffinityMask? mask)
    {
        using var key = Registry.LocalMachine.CreateSubKey(AffinityPolicyKeyPath(instanceId), writable: true)
            ?? throw new InvalidOperationException($"Cannot open affinity policy key for {instanceId}.");

        key.SetValue(DevicePolicyValueName, (int)(uint)policy, RegistryValueKind.DWord);

        if (priority is not null and not Models.IrqPriority.Undefined)
            key.SetValue(DevicePriorityValueName, (int)(uint)priority.Value, RegistryValueKind.DWord);

        if (mask is not null && !mask.IsEmpty)
        {
            var encoded = mask.Encode();
            switch (encoded.Kind)
            {
                case AffinityEncodingKind.Dword:
                    key.SetValue(AssignmentSetOverrideValueName, (int)encoded.DwordValue, RegistryValueKind.DWord);
                    break;
                case AffinityEncodingKind.Qword:
                    key.SetValue(AssignmentSetOverrideValueName, (long)encoded.QwordValue, RegistryValueKind.QWord);
                    break;
                case AffinityEncodingKind.Binary:
                    key.SetValue(AssignmentSetOverrideValueName, encoded.BinaryValue, RegistryValueKind.Binary);
                    break;
            }
        }
        else if (policy == IrqPolicy.SpecifiedProcessors)
        {
            throw new InvalidOperationException(
                "IrqPolicySpecifiedProcessors requires a non-empty processor mask.");
        }
    }

    /// <summary>
    /// Restore a device to its pre-CorePilot state: delete the values we manage
    /// unless a backup says they existed before (then rewrite original data).
    /// Unrelated values in the key are never touched.
    /// </summary>
    public void Restore(string instanceId, DeviceBackup? backup)
    {
        using var key = Registry.LocalMachine.OpenSubKey(AffinityPolicyKeyPath(instanceId), writable: true);
        if (key is null)
            return;

        foreach (var name in ManagedValueNames)
        {
            key.DeleteValue(name, throwOnMissingValue: false);
        }

        if (backup is null)
            return;

        foreach (var value in backup.OriginalValues)
        {
            switch (value.Kind)
            {
                case "DWord":
                    key.SetValue(value.Name, (int)value.Dword, RegistryValueKind.DWord);
                    break;
                case "QWord":
                    key.SetValue(value.Name, (long)value.Qword, RegistryValueKind.QWord);
                    break;
                case "Binary" when value.Binary is not null:
                    key.SetValue(value.Name, value.Binary, RegistryValueKind.Binary);
                    break;
            }
        }
    }
}
