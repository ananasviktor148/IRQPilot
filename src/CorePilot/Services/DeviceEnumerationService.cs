using System.Runtime.InteropServices;
using System.Text;
using CorePilot.Interop;
using CorePilot.Models;
using Microsoft.Win32;

namespace CorePilot.Services;

/// <summary>
/// Enumerates present PnP devices via CfgMgr32 and keeps the ones that
/// participate in interrupt management (have an "Interrupt Management" key).
/// </summary>
public sealed class DeviceEnumerationService
{
    private static readonly HashSet<string> BootCriticalClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Computer", "Processor", "ACPI", "Chipset",
    };

    public Task<List<InterruptDevice>> EnumerateAsync() => Task.Run(Enumerate);

    private List<InterruptDevice> Enumerate()
    {
        var devices = new List<InterruptDevice>();

        var cr = NativeMethods.CM_Get_Device_ID_List_SizeW(out var charCount, null, 0);
        if (cr != NativeMethods.CR_SUCCESS || charCount <= 0)
            return devices;

        var buffer = new char[charCount + 1];
        cr = NativeMethods.CM_Get_Device_ID_ListW(null, buffer, buffer.Length, 0);
        if (cr != NativeMethods.CR_SUCCESS)
            return devices;

        foreach (var instanceId in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var device = TryBuildDevice(instanceId);
            if (device is not null)
                devices.Add(device);
        }

        return devices
            .OrderBy(d => d.ClassName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private InterruptDevice? TryBuildDevice(string instanceId)
    {
        // Only include devices that actually participate in interrupt management:
        // they have the "Interrupt Management" subkey written by the PnP manager.
        using var interruptKey = Registry.LocalMachine.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Enum\{instanceId}\Device Parameters\Interrupt Management", writable: false);
        if (interruptKey is null)
            return null;

        if (NativeMethods.CM_Locate_DevNodeW(out var devInst, instanceId, 0) != NativeMethods.CR_SUCCESS)
            return null;

        var friendlyName = GetStringProperty(devInst, NativeMethods.CM_DRP_FRIENDLYNAME)
                        ?? GetStringProperty(devInst, NativeMethods.CM_DRP_DEVICEDESC)
                        ?? instanceId;
        var className = GetStringProperty(devInst, NativeMethods.CM_DRP_CLASS) ?? "Unknown";
        var hardwareId = GetMultiStringProperty(devInst, NativeMethods.CM_DRP_HARDWAREID)?.FirstOrDefault();
        var service = GetStringProperty(devInst, NativeMethods.CM_DRP_SERVICE);
        var configFlags = GetDwordProperty(devInst, NativeMethods.CM_DRP_CONFIGFLAGS) ?? 0;

        var status = 0;
        var isStarted = NativeMethods.CM_Get_DevNode_Status(out status, out _, devInst, 0) == NativeMethods.CR_SUCCESS
                        && (status & NativeMethods.DN_STARTED) != 0;

        var msiSupported = false;
        using (var msiKey = interruptKey.OpenSubKey("MessageSignaledInterruptProperties", writable: false))
        {
            if (msiKey?.GetValue("MSISupported") is int msi)
                msiSupported = msi == 1;
        }

        var isBootCritical =
            (configFlags & NativeMethods.CONFIGFLAG_BOOT_DEVICE) != 0
            || BootCriticalClasses.Contains(className)
            || IsBootStartService(service);

        return new InterruptDevice
        {
            InstanceId = instanceId,
            FriendlyName = friendlyName,
            ClassName = className,
            HardwareId = hardwareId,
            ServiceName = service,
            MsiSupported = msiSupported,
            IsStarted = isStarted,
            IsBootCritical = isBootCritical,
        };
    }

    /// <summary>A service with Start=0 loads at boot; its device must never be restarted.</summary>
    private static bool IsBootStartService(string? serviceName)
    {
        if (string.IsNullOrEmpty(serviceName)) return false;
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}", writable: false);
        return key?.GetValue("Start") is int start && start == 0;
    }

    private static string? GetStringProperty(uint devInst, int property)
    {
        var bytes = GetPropertyBytes(devInst, property, out var regType);
        if (bytes is null || (regType != (int)RegistryValueKind.String && regType != (int)RegistryValueKind.ExpandString))
            return null;
        return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
    }

    private static string[]? GetMultiStringProperty(uint devInst, int property)
    {
        var bytes = GetPropertyBytes(devInst, property, out var regType);
        if (bytes is null || regType != (int)RegistryValueKind.MultiString)
            return null;
        return Encoding.Unicode.GetString(bytes)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static uint? GetDwordProperty(uint devInst, int property)
    {
        var bytes = GetPropertyBytes(devInst, property, out var regType);
        if (bytes is null || regType != (int)RegistryValueKind.DWord || bytes.Length < 4)
            return null;
        return BitConverter.ToUInt32(bytes, 0);
    }

    private static byte[]? GetPropertyBytes(uint devInst, int property, out int regType)
    {
        regType = 0;
        var length = 0;
        // First call sizes the buffer; CfgMgr32 answers CR_BUFFER_SMALL with the
        // required byte count, or CR_INVALID_PROPERTY when the value is absent.
        var cr = NativeMethods.CM_Get_DevNode_Registry_PropertyW(devInst, property, out regType, IntPtr.Zero, ref length, 0);
        if (length <= 0 || (cr != NativeMethods.CR_SUCCESS && cr != NativeMethods.CR_BUFFER_SMALL))
            return null;

        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            cr = NativeMethods.CM_Get_DevNode_Registry_PropertyW(devInst, property, out regType, buffer, ref length, 0);
            if (cr != NativeMethods.CR_SUCCESS)
                return null;
            var bytes = new byte[length];
            Marshal.Copy(buffer, bytes, 0, length);
            return bytes;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
