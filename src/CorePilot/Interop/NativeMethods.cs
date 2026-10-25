using System.Runtime.InteropServices;

namespace CorePilot.Interop;

internal static partial class NativeMethods
{
    // ---- CfgMgr32 (device enumeration, properties, restart) ----

    internal const int CR_SUCCESS = 0;
    internal const int CR_NO_SUCH_DEVNODE = 13;
    internal const int CR_BUFFER_SMALL = 26;

    internal const int CM_DRP_DEVICEDESC = 0x00000001;
    internal const int CM_DRP_HARDWAREID = 0x00000002;
    internal const int CM_DRP_SERVICE = 0x00000005;
    internal const int CM_DRP_CLASS = 0x00000008;
    internal const int CM_DRP_CONFIGFLAGS = 0x0000000B;
    internal const int CM_DRP_FRIENDLYNAME = 0x0000000D;

    internal const int CONFIGFLAG_BOOT_DEVICE = 0x00000100;

    internal const int DN_STARTED = 0x00000008;

    [LibraryImport("CfgMgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, int ulFlags);

    [LibraryImport("CfgMgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int CM_Get_Device_ID_List_SizeW(out int pulLen, string? filter, int ulFlags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_ID_ListW")]
    internal static extern int CM_Get_Device_ID_ListW(string? filter, [Out] char[] buffer, int bufferLen, int ulFlags);

    [LibraryImport("CfgMgr32.dll", EntryPoint = "CM_Get_DevNode_Registry_PropertyW")]
    internal static partial int CM_Get_DevNode_Registry_PropertyW(uint dnDevInst, int ulProperty, out int pulRegDataType, IntPtr buffer, ref int pulLength, int ulFlags);

    [LibraryImport("CfgMgr32.dll")]
    internal static partial int CM_Get_DevNode_Status(out int pulStatus, out int pulProblemNumber, uint dnDevInst, int ulFlags);

    [LibraryImport("CfgMgr32.dll")]
    internal static partial int CM_Disable_DevNode(uint dnDevInst, int ulFlags);

    [LibraryImport("CfgMgr32.dll")]
    internal static partial int CM_Enable_DevNode(uint dnDevInst, int ulFlags);

    // ---- Kernel32 (processor groups) ----

    [LibraryImport("kernel32.dll")]
    internal static partial ushort GetActiveProcessorGroupCount();

    [LibraryImport("kernel32.dll")]
    internal static partial uint GetActiveProcessorCount(ushort groupNumber);

    // ---- DwmApi (dark window chrome) ----

    // DWMWA_USE_IMMERSIVE_DARK_MODE: 20 on Win10 20H1+ / Win11, 19 on older Win10.
    internal const int DWMWA_USE_IMMERSIVE_DARK_MODE_CURRENT = 20;
    internal const int DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY = 19;

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    // ---- AdvApi32 (ETW session control for NT Kernel Logger) ----

    internal const string KernelLoggerName = "NT Kernel Logger";

    internal const int EVENT_TRACE_CONTROL_STOP = 1;

    internal const int EVENT_TRACE_FLAG_IMAGE_LOAD = 0x00000004;
    internal const int EVENT_TRACE_FLAG_DPC = 0x00000020;
    internal const int EVENT_TRACE_FLAG_INTERRUPT = 0x00000040;

    internal const int EVENT_TRACE_FILE_MODE_SEQUENTIAL = 0x00000001;

    internal const int WNODE_FLAG_TRACED_GUID = 0x00020000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct WNODE_HEADER
    {
        public uint BufferSize;
        public uint ProviderId;
        public ulong HistoricalContext;
        public ulong TimeStamp;
        public Guid Guid;
        public uint ClientContext;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EVENT_TRACE_LOGFILEW
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? LogFileName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? LoggerName;
        public long CurrentTime;
        public uint BuffersRead;
        public uint LogFileMode;
        public IntPtr CurrentEvent;   // TRACE_EVENT, unused here
        public IntPtr LogfileHeader;  // TRACE_LOGFILE_HEADER, unused here
        public IntPtr BufferCallback;
        public uint BufferSize;
        public uint Filled;
        public uint EventsLost;
        public IntPtr EventCallback;
        public uint IsKernelTrace;
        public IntPtr Context;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct EVENT_TRACE_PROPERTIES
    {
        public WNODE_HEADER Wnode;
        public uint BufferSize;
        public uint MinimumBuffers;
        public uint MaximumBuffers;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint FlushTimer;
        public uint EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers;
        public uint FreeBuffers;
        public uint EventsLost;
        public uint BuffersWritten;
        public uint LogBuffersLost;
        public uint RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset;
        public uint LoggerNameOffset;
    }

    // Properties buffers are variable-length (strings appended past the struct),
    // so these take raw pointers; callers marshal into a contiguous buffer.
    [LibraryImport("advapi32.dll", EntryPoint = "StartTraceW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int StartTraceW(out ulong traceHandle, string instanceName, IntPtr properties);

    [LibraryImport("advapi32.dll", EntryPoint = "ControlTraceW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int ControlTraceW(ulong traceHandle, string? instanceName, IntPtr properties, int controlCode);
}
