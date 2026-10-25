using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CorePilot.Models;
using CorePilot.Services;

namespace CorePilot.ViewModels;

/// <summary>One row in the profile-apply diff view.</summary>
public sealed partial class ProfileDiffRow : ObservableObject
{
    public required string InstanceId { get; init; }
    public required string DeviceName { get; init; }
    public required string CurrentSummary { get; init; }
    public required string NewSummary { get; init; }
    public bool DevicePresent { get; init; }

    [ObservableProperty] private bool _include = true;
}

public sealed partial class ProfilesViewModel : ObservableObject
{
    private readonly BackupService _backup;
    private readonly AffinityRegistryService _registry;
    private readonly DevicesViewModel _devices;
    private readonly IDialogService _dialogs;

    public ObservableCollection<string> Profiles { get; } = new();
    public ObservableCollection<ProfileDiffRow> DiffRows { get; } = new();

    [ObservableProperty] private string? _selectedProfile;
    [ObservableProperty] private string _newProfileName = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _hasDiff;

    private CorePilotProfile? _loadedProfile;

    public ProfilesViewModel(
        BackupService backup,
        AffinityRegistryService registry,
        DevicesViewModel devices,
        IDialogService dialogs)
    {
        _backup = backup;
        _registry = registry;
        _devices = devices;
        _dialogs = dialogs;
    }

    public void RefreshList()
    {
        Profiles.Clear();
        foreach (var name in _backup.ListProfileNames())
            Profiles.Add(name);
    }

    [RelayCommand]
    private async Task SaveCurrentAsync()
    {
        var name = NewProfileName.Trim();
        if (name.Length == 0)
        {
            await _dialogs.AlertAsync("Name required", "Type a profile name first.");
            return;
        }

        var profile = new CorePilotProfile { Name = name, CreatedUtc = DateTime.UtcNow };
        foreach (var vm in _devices.AllDevices)
        {
            var state = vm.WrittenState;
            if (!state.HasAnySetting) continue;
            profile.Devices[vm.InstanceId] =
                AffinityAssignment.FromMask(state.Policy, state.Priority, state.Mask);
        }

        _backup.SaveProfile(profile);
        RefreshList();
        SelectedProfile = name;
        Status = $"Saved '{name}' with {profile.Devices.Count} device assignment(s).";
    }

    [RelayCommand]
    private async Task LoadForDiffAsync()
    {
        var name = SelectedProfile;
        if (name is null) return;

        var profile = _backup.LoadProfile(name);
        if (profile is null)
        {
            Status = $"Could not read profile '{name}'.";
            return;
        }
        _loadedProfile = profile;

        DiffRows.Clear();
        foreach (var (instanceId, assignment) in profile.Devices)
        {
            var vm = _devices.AllDevices.FirstOrDefault(d => d.InstanceId == instanceId);
            var current = vm?.WrittenState;
            DiffRows.Add(new ProfileDiffRow
            {
                InstanceId = instanceId,
                DeviceName = vm?.DisplayName ?? instanceId,
                DevicePresent = vm is not null,
                Include = vm is not null,
                CurrentSummary = current is null || !current.HasAnySetting
                    ? "Default"
                    : $"{current.Policy} · {current.Mask}",
                NewSummary = $"{assignment.Policy} · {assignment.ToMask()}",
            });
        }
        HasDiff = DiffRows.Count > 0;
        Status = HasDiff
            ? $"Profile '{name}': review the diff, then apply."
            : $"Profile '{name}' contains no assignments.";

        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        var profile = _loadedProfile;
        if (profile is null) return;

        var rows = DiffRows.Where(r => r.Include && r.DevicePresent).ToList();
        if (rows.Count == 0)
        {
            await _dialogs.AlertAsync("Nothing selected", "Check at least one device row to apply.");
            return;
        }
        if (!await _dialogs.ConfirmAsync("Apply profile",
                $"Write affinity settings for {rows.Count} device(s) from profile '{profile.Name}'? Existing settings are backed up first. Devices must be restarted to take effect."))
            return;

        var applied = 0;
        foreach (var row in rows)
        {
            if (!profile.Devices.TryGetValue(row.InstanceId, out var assignment)) continue;
            var mask = assignment.ToMask();
            try
            {
                if (_backup.LoadBackup(row.InstanceId) is null)
                    _backup.SaveBackup(_registry.CaptureBackup(row.InstanceId));

                var policy = assignment.Policy ?? IrqPolicy.SpecifiedProcessors;
                await Task.Run(() => _registry.Write(row.InstanceId, policy, assignment.Priority, mask.IsEmpty ? null : mask));

                var vm = _devices.AllDevices.FirstOrDefault(d => d.InstanceId == row.InstanceId);
                if (vm is not null)
                {
                    vm.WrittenState = _registry.Read(row.InstanceId);
                    vm.IsPending = true;
                    vm.RefreshSummaries();
                }
                applied++;
            }
            catch (Exception ex)
            {
                Status = $"Failed on {row.DeviceName}: {ex.Message}";
                return;
            }
        }
        Status = $"Applied {applied} assignment(s). Restart the devices to take effect.";
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (SelectedProfile is null) return;
        _backup.DeleteProfile(SelectedProfile);
        RefreshList();
        DiffRows.Clear();
        HasDiff = false;
        Status = $"Deleted '{SelectedProfile}'.";
    }
}
