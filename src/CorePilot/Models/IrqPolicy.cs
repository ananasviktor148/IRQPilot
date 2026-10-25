namespace CorePilot.Models;

/// <summary>
/// IRQ_DEVICE_POLICY values as stored in the DevicePolicy registry value.
/// Numeric values verified against the WDK documentation (wdm.h).
/// </summary>
public enum IrqPolicy : uint
{
    MachineDefault = 0,
    AllCloseProcessors = 1,
    OneCloseProcessor = 2,
    AllProcessorsInMachine = 3,
    SpecifiedProcessors = 4,
    SpreadMessagesAcrossAllProcessors = 5,
    // 6 (IrqPolicyAllProcessorsInMachineWhenSteered) is reserved for system use.
    // Note: the WDK defines no separate "...InGroup" variant; on multi-group
    // systems, policy 5 spreads messages across all processors in all groups.
}

/// <summary>IRQ_PRIORITY values for the DevicePriority registry value.</summary>
public enum IrqPriority : uint
{
    Undefined = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4,
}

public static class IrqPolicyInfo
{
    public static string Describe(IrqPolicy policy) => policy switch
    {
        IrqPolicy.MachineDefault => "Machine default (Windows decides)",
        IrqPolicy.AllCloseProcessors => "All close processors (NUMA-local)",
        IrqPolicy.OneCloseProcessor => "One close processor",
        IrqPolicy.AllProcessorsInMachine => "All processors in machine",
        IrqPolicy.SpecifiedProcessors => "Specified processors (use core selection)",
        IrqPolicy.SpreadMessagesAcrossAllProcessors => "Spread MSI messages across all processors",
        _ => $"Unknown ({(uint)policy})",
    };

    public static bool IsDefined(uint value) => value <= 5;

    /// <summary>Policies that require an explicit processor mask to be meaningful.</summary>
    public static bool RequiresMask(IrqPolicy policy) => policy == IrqPolicy.SpecifiedProcessors;
}
