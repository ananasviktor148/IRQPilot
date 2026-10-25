using System.Security.Principal;
using System.Windows;
using CorePilot.Services;
using CorePilot.ViewModels;

namespace CorePilot;

public partial class App : Application
{
    private PerformanceMonitorService? _monitor;
    private EtwLatencyService? _etw;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        if (!IsElevated())
        {
            MessageBox.Show(
                "IRQPilot manages interrupt affinity, which requires writing to HKLM and " +
                "restarting devices. Please run IRQPilot as Administrator.",
                "IRQPilot — Administrator required",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }

        var topology = ProcessorTopology.Query();
        _monitor = new PerformanceMonitorService(topology);
        _etw = new EtwLatencyService(topology);
        var enumeration = new DeviceEnumerationService();
        var registry = new AffinityRegistryService();
        var backup = new BackupService();
        var restart = new DeviceRestartService();

        var window = new MainWindow();
        var dialogs = window.Dialogs;

        var radar = new CoreRadarViewModel(topology, _monitor, _etw);
        var devices = new DevicesViewModel(enumeration, registry, backup, restart, radar, dialogs);
        var profiles = new ProfilesViewModel(backup, registry, devices, dialogs);

        window.DataContext = new MainViewModel(radar, devices, profiles, _monitor, _etw);
        window.Closed += (_, _) =>
        {
            radar.Stop();
            _monitor.Dispose();
        };
        window.Show();
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
