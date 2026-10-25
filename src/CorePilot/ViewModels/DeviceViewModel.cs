using System.Windows;
using CorePilot.Models;

namespace CorePilot.ViewModels;

internal static class UiDispatch
{
    public static void Run(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }
}

/// <summary>A device row in the searchable list.</summary>
public sealed partial class DeviceViewModel : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public InterruptDevice Device { get; }

    /// <summary>State as last read from (or written to) the registry.</summary>
    public AffinityPolicyState WrittenState { get; set; } = new();

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private bool _isPending;

    public string DisplayName => Device.DisplayName;
    public string ClassName => Device.ClassName;
    public string InstanceId => Device.InstanceId;
    public bool MsiSupported => Device.MsiSupported;
    public bool IsBootCritical => Device.IsBootCritical;

    public string StateSummary
    {
        get
        {
            if (IsPending) return "Pending restart";
            if (!WrittenState.HasAnySetting) return "Default";
            var policy = WrittenState.Policy ?? IrqPolicy.MachineDefault;
            var mask = WrittenState.Mask;
            return mask is not null && !mask.IsEmpty
                ? $"{policy} · {mask.ProcessorCount} cores"
                : policy.ToString();
        }
    }

    public DeviceViewModel(InterruptDevice device) => Device = device;

    public void RefreshSummaries()
    {
        OnPropertyChanged(nameof(StateSummary));
        OnPropertyChanged(nameof(MsiSupported));
        OnPropertyChanged(nameof(IsBootCritical));
    }
}
