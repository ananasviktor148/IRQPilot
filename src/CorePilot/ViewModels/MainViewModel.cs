using CommunityToolkit.Mvvm.ComponentModel;
using CorePilot.Services;

namespace CorePilot.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    public CoreRadarViewModel Radar { get; }
    public DevicesViewModel Devices { get; }
    public ProcessesViewModel Processes { get; }
    public ProfilesViewModel Profiles { get; }

    [ObservableProperty] private string _appStatus = "Ready";

    public MainViewModel(
        CoreRadarViewModel radar,
        DevicesViewModel devices,
        ProcessesViewModel processes,
        ProfilesViewModel profiles,
        PerformanceMonitorService monitor,
        EtwLatencyService etw)
    {
        Radar = radar;
        Devices = devices;
        Processes = processes;
        Profiles = profiles;

        radar.AssignRecommendedRequested += cores =>
        {
            devices.AssignCoresToSelectedDevice(cores);
            AppStatus = "Recommended cores assigned to the selected device — review and Apply.";
        };

        radar.Start();
        _ = devices.LoadAsync();
        _ = processes.LoadAsync();
        profiles.RefreshList();
    }
}
