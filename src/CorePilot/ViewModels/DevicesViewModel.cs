using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CorePilot.Models;
using CorePilot.Services;

namespace CorePilot.ViewModels;

public sealed partial class DevicesViewModel : ObservableObject
{
    private readonly DeviceEnumerationService _enumeration;
    private readonly AffinityRegistryService _registry;
    private readonly BackupService _backup;
    private readonly DeviceRestartService _restart;
    private readonly CoreRadarViewModel _radar;
    private readonly IDialogService _dialogs;

    private readonly List<DeviceViewModel> _allDevices = new();

    public ObservableCollection<DeviceViewModel> FilteredDevices { get; } = new();

    public IReadOnlyList<DeviceViewModel> AllDevices => _allDevices;

    public IReadOnlyList<IrqPolicy> PolicyOptions { get; } = new[]
    {
        IrqPolicy.MachineDefault,
        IrqPolicy.AllCloseProcessors,
        IrqPolicy.OneCloseProcessor,
        IrqPolicy.AllProcessorsInMachine,
        IrqPolicy.SpecifiedProcessors,
        IrqPolicy.SpreadMessagesAcrossAllProcessors,
    };

    public IReadOnlyList<IrqPriority> PriorityOptions { get; } = new[]
    {
        IrqPriority.Undefined, IrqPriority.Low, IrqPriority.Medium, IrqPriority.High, IrqPriority.Critical,
    };

    /// <summary>The CPU tiles shown in the detail pane (shared live radar state).</summary>
    public ObservableCollection<CoreViewModel> Cores => _radar.Cores;

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private DeviceViewModel? _selectedDevice;
    [ObservableProperty] private IrqPolicy _selectedPolicy = IrqPolicy.MachineDefault;
    [ObservableProperty] private IrqPriority _selectedPriority = IrqPriority.Undefined;
    [ObservableProperty] private string _detailStatus = "";
    [ObservableProperty] private bool _isBusy;

    public bool HasSelectedDevice => SelectedDevice is not null;
    public bool MaskRequired => SelectedPolicy == IrqPolicy.SpecifiedProcessors;

    public DevicesViewModel(
        DeviceEnumerationService enumeration,
        AffinityRegistryService registry,
        BackupService backup,
        DeviceRestartService restart,
        CoreRadarViewModel radar,
        IDialogService dialogs)
    {
        _enumeration = enumeration;
        _registry = registry;
        _backup = backup;
        _restart = restart;
        _radar = radar;
        _dialogs = dialogs;
    }

    public async Task LoadAsync()
    {
        IsBusy = true;
        DetailStatus = "Enumerating devices…";
        try
        {
            var devices = await _enumeration.EnumerateAsync();
            _allDevices.Clear();
            foreach (var device in devices)
            {
                var vm = new DeviceViewModel(device);
                vm.WrittenState = _registry.Read(device.InstanceId);
                _allDevices.Add(vm);
            }
            ApplyFilter();
            DetailStatus = $"{_allDevices.Count} devices with interrupt management";
        }
        catch (Exception ex)
        {
            DetailStatus = "Enumeration failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        FilteredDevices.Clear();
        foreach (var vm in _allDevices)
        {
            if (query.Length == 0
                || vm.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || vm.InstanceId.Contains(query, StringComparison.OrdinalIgnoreCase)
                || vm.ClassName.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                FilteredDevices.Add(vm);
            }
        }
    }

    partial void OnSelectedDeviceChanged(DeviceViewModel? value) => LoadDeviceIntoDetail(value);

    partial void OnSelectedPolicyChanged(IrqPolicy value) => OnPropertyChanged(nameof(MaskRequired));

    private void LoadDeviceIntoDetail(DeviceViewModel? vm)
    {
        OnPropertyChanged(nameof(HasSelectedDevice));
        foreach (var core in Cores) core.IsSelected = false;
        if (vm is null) return;

        var state = vm.WrittenState;
        SelectedPolicy = state.Policy ?? IrqPolicy.MachineDefault;
        SelectedPriority = state.Priority ?? IrqPriority.Undefined;

        if (state.Mask is not null)
        {
            foreach (var core in Cores)
                core.IsSelected = state.Mask.Contains(core.Group, core.IndexInGroup);
        }

        DetailStatus = vm.IsBootCritical
            ? "Boot-critical device: affinity can be written but device restart is blocked (applies on reboot)."
            : "";
    }

    public GroupAffinityMask CurrentMaskSelection()
    {
        var mask = new GroupAffinityMask();
        foreach (var core in Cores.Where(c => c.IsSelected))
            mask.Add(core.Group, core.IndexInGroup);
        return mask;
    }

    /// <summary>Called by Core Radar: replace the selection with the recommended cores.</summary>
    public void AssignCoresToSelectedDevice(IReadOnlyList<CoreViewModel> cores)
    {
        if (SelectedDevice is null)
        {
            DetailStatus = "Select a device in the Devices tab first.";
            return;
        }
        foreach (var core in Cores) core.IsSelected = false;
        foreach (var core in cores) core.IsSelected = true;
        SelectedPolicy = IrqPolicy.SpecifiedProcessors;
        DetailStatus = $"Assigned {cores.Count} recommended cores to {SelectedDevice.DisplayName}. Apply to write.";
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        var vm = SelectedDevice;
        if (vm is null || IsBusy) return;

        var mask = CurrentMaskSelection();
        if (SelectedPolicy == IrqPolicy.SpecifiedProcessors && mask.IsEmpty)
        {
            await _dialogs.AlertAsync("No cores selected",
                "The 'Specified processors' policy needs at least one core checked in the CPU grid.");
            return;
        }

        var maskSummary = mask.IsEmpty ? "no mask" : $"{mask.ProcessorCount} cores ({mask})";
        var choice = await _dialogs.ConfirmApplyAsync(
            vm.DisplayName,
            $"Policy: {SelectedPolicy}\nPriority: {SelectedPriority}\nMask: {maskSummary}",
            canRestart: !vm.IsBootCritical);
        if (choice == ApplyChoice.Cancel) return;

        IsBusy = true;
        try
        {
            // Keep the first-ever backup so restore always returns to pre-CorePilot state.
            if (_backup.LoadBackup(vm.InstanceId) is null)
                _backup.SaveBackup(_registry.CaptureBackup(vm.InstanceId));

            var maskToWrite = mask.IsEmpty && !IrqPolicyInfo.RequiresMask(SelectedPolicy) ? null : mask;
            IrqPriority? priorityToWrite = SelectedPriority == IrqPriority.Undefined ? null : SelectedPriority;

            await Task.Run(() => _registry.Write(vm.InstanceId, SelectedPolicy, priorityToWrite, maskToWrite));

            vm.WrittenState = _registry.Read(vm.InstanceId);
            vm.IsPending = true;
            vm.RefreshSummaries();

            if (choice == ApplyChoice.WriteAndRestart)
                await RestartDeviceAsync(vm);
            else
                DetailStatus = "Written. Restart the device (or reboot) for the change to take effect.";
        }
        catch (Exception ex)
        {
            await _dialogs.AlertAsync("Apply failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RestartSelectedAsync()
    {
        if (SelectedDevice is not null)
            await RestartDeviceAsync(SelectedDevice);
    }

    private async Task RestartDeviceAsync(DeviceViewModel vm)
    {
        if (vm.IsBootCritical)
        {
            await _dialogs.AlertAsync("Restart blocked",
                $"'{vm.DisplayName}' is boot-critical. Reboot the machine to apply the new affinity.");
            return;
        }
        if (!await _dialogs.ConfirmAsync("Restart device",
                $"'{vm.DisplayName}' will be disabled and re-enabled. It will briefly stop working (e.g. network or audio may drop). Continue?"))
            return;

        IsBusy = true;
        DetailStatus = $"Restarting {vm.DisplayName}…";
        try
        {
            await _restart.RestartAsync(vm.Device);
            vm.IsPending = false;
            vm.RefreshSummaries();
            DetailStatus = $"{vm.DisplayName} restarted — affinity policy is now active.";
        }
        catch (DeviceRestartException ex)
        {
            DetailStatus = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RestoreDefaultsAsync()
    {
        var vm = SelectedDevice;
        if (vm is null || IsBusy) return;

        if (!await _dialogs.ConfirmAsync("Restore defaults",
                $"Remove the IRQPilot affinity settings for '{vm.DisplayName}' and restore whatever existed before? Restart the device afterwards to take effect."))
            return;

        IsBusy = true;
        try
        {
            var backup = _backup.LoadBackup(vm.InstanceId);
            await Task.Run(() => _registry.Restore(vm.InstanceId, backup));
            vm.WrittenState = _registry.Read(vm.InstanceId);
            vm.IsPending = true;
            vm.RefreshSummaries();
            LoadDeviceIntoDetail(vm);
            DetailStatus = "Defaults restored. Restart the device (or reboot) for the change to take effect.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RestoreAllAsync()
    {
        var backups = _backup.LoadAllBackups();
        if (backups.Count == 0)
        {
            await _dialogs.AlertAsync("Nothing to restore", "No IRQPilot backups exist yet.");
            return;
        }
        if (!await _dialogs.ConfirmAsync("Restore all devices",
                $"Restore pre-IRQPilot affinity settings for {backups.Count} device(s)? Restart devices afterwards to take effect."))
            return;

        IsBusy = true;
        try
        {
            foreach (var backup in backups)
            {
                await Task.Run(() => _registry.Restore(backup.InstanceId, backup));
                var vm = _allDevices.FirstOrDefault(d => d.InstanceId == backup.InstanceId);
                if (vm is not null)
                {
                    vm.WrittenState = _registry.Read(vm.InstanceId);
                    vm.IsPending = true;
                    vm.RefreshSummaries();
                }
            }
            if (SelectedDevice is not null) LoadDeviceIntoDetail(SelectedDevice);
            DetailStatus = $"Restored {backups.Count} device(s).";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();
}
