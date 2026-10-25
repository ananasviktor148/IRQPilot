using CorePilot.Interop;
using CorePilot.Models;

namespace CorePilot.Services;

public sealed class DeviceRestartException : Exception
{
    public DeviceRestartException(string message) : base(message) { }
}

/// <summary>
/// Restarts a device (disable + re-enable) so the PnP manager re-reads its
/// affinity policy. Boot-critical devices are hard-blocked here regardless of
/// what the UI allowed through.
/// </summary>
public sealed class DeviceRestartService
{
    public Task RestartAsync(InterruptDevice device) => Task.Run(() => Restart(device));

    public void Restart(InterruptDevice device)
    {
        if (device.IsBootCritical)
            throw new DeviceRestartException(
                $"'{device.DisplayName}' is boot-critical or runs a boot-start driver. " +
                "Restarting it could crash the system. Change takes effect on next reboot instead.");

        var cr = NativeMethods.CM_Locate_DevNodeW(out var devInst, device.InstanceId, 0);
        if (cr != NativeMethods.CR_SUCCESS)
            throw new DeviceRestartException(
                $"Could not locate '{device.DisplayName}' (CfgMgr32 error {cr}). The device may have been removed.");

        cr = NativeMethods.CM_Disable_DevNode(devInst, 0);
        if (cr != NativeMethods.CR_SUCCESS)
            throw new DeviceRestartException(
                $"Windows refused to disable '{device.DisplayName}' (CfgMgr32 error {cr}). " +
                "The driver may not support dynamic disable; the change applies on next reboot.");

        try
        {
            // Give the PnP manager a moment to quiesce interrupts.
            Thread.Sleep(500);
        }
        finally
        {
            cr = NativeMethods.CM_Enable_DevNode(devInst, 0);
        }

        if (cr != NativeMethods.CR_SUCCESS)
            throw new DeviceRestartException(
                $"'{device.DisplayName}' was disabled but Windows refused to re-enable it " +
                $"(CfgMgr32 error {cr}). Re-enable it from Device Manager.");
    }
}
