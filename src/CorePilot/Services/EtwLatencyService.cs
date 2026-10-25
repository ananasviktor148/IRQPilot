using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using CorePilot.Interop;
using CorePilot.Models;
using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Cpu;

namespace CorePilot.Services;

/// <summary>
/// Rolling windowed ETW capture: runs the NT Kernel Logger (DPC + ISR + image
/// load events) to an ETL file for a fixed window, stops it, then analyzes the
/// file offline with the TraceProcessor engine. This capture-analyze-display
/// loop is used instead of a live real-time session because offline analysis
/// is far more reliable under load.
/// </summary>
public sealed class EtwLatencyService
{
    public const int WindowSeconds = 10;

    private const int ErrorAlreadyExists = 183;

    private readonly ProcessorTopology _topology;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public bool IsRunning => _loop is not null;

    /// <summary>Raised on a background thread when a window finished analysis.</summary>
    public event Action<EtwWindowReport>? ReportReady;

    /// <summary>Raised on a background thread when capture or analysis fails.</summary>
    public event Action<string>? ErrorOccurred;

    public EtwLatencyService(ProcessorTopology topology) => _topology = topology;

    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_loop is not null)
        {
            try { await _loop; } catch (OperationCanceledException) { }
            _loop = null;
        }
        StopKernelSession();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var windowStart = DateTime.Now;
            string etlPath;
            try
            {
                etlPath = await Task.Run(() => CaptureWindow(ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(FriendlyCaptureError(ex));
                break;
            }

            try
            {
                var report = await Task.Run(() => Analyze(etlPath, windowStart), ct);
                ReportReady?.Invoke(report);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke("Trace analysis failed: " + ex.Message);
            }
            finally
            {
                try { File.Delete(etlPath); } catch (IOException) { }
            }
        }
    }

    private static string FriendlyCaptureError(Exception ex) => ex switch
    {
        EtwSessionException { IsAlreadyRunning: true } =>
            "The NT Kernel Logger session is already in use by another tool " +
            "(e.g. WPR, xperf, Process Explorer). Close it and toggle ETW mode again.",
        _ => "ETW capture failed: " + ex.Message,
    };

    // ---- Capture ----

    private string CaptureWindow(CancellationToken ct)
    {
        var etlPath = Path.Combine(Path.GetTempPath(), $"CorePilot-{Guid.NewGuid():N}.etl");

        // Clean up a stale session from a previous crash.
        StopKernelSession();

        var name = NativeMethods.KernelLoggerName;
        var structSize = Marshal.SizeOf<NativeMethods.EVENT_TRACE_PROPERTIES>();
        var fileBytes = Encoding.Unicode.GetBytes(etlPath + "\0");
        var nameBytes = Encoding.Unicode.GetBytes(name + "\0");
        var totalSize = structSize + fileBytes.Length + nameBytes.Length;

        var buffer = Marshal.AllocHGlobal(totalSize);
        try
        {
            var props = new NativeMethods.EVENT_TRACE_PROPERTIES
            {
                Wnode = new NativeMethods.WNODE_HEADER
                {
                    BufferSize = (uint)totalSize,
                    Flags = NativeMethods.WNODE_FLAG_TRACED_GUID,
                    ClientContext = 1, // QPC clock
                },
                BufferSize = 64,          // KB per buffer
                MinimumBuffers = 16,
                MaximumBuffers = 128,
                LogFileMode = NativeMethods.EVENT_TRACE_FILE_MODE_SEQUENTIAL,
                FlushTimer = 1,
                EnableFlags = (uint)(NativeMethods.EVENT_TRACE_FLAG_DPC
                                   | NativeMethods.EVENT_TRACE_FLAG_INTERRUPT
                                   | NativeMethods.EVENT_TRACE_FLAG_IMAGE_LOAD),
                LogFileNameOffset = (uint)structSize,
                LoggerNameOffset = (uint)(structSize + fileBytes.Length),
            };
            Marshal.StructureToPtr(props, buffer, false);
            Marshal.Copy(fileBytes, 0, buffer + structSize, fileBytes.Length);
            Marshal.Copy(nameBytes, 0, buffer + structSize + fileBytes.Length, nameBytes.Length);

            var error = NativeMethods.StartTraceW(out var handle, name, buffer);
            if (error == ErrorAlreadyExists)
            {
                StopKernelSession();
                error = NativeMethods.StartTraceW(out handle, name, buffer);
            }
            if (error != 0)
                throw new EtwSessionException(error);

            try
            {
                // Wait out the window without blocking a thread pool thread on sleep.
                var deadline = DateTime.UtcNow.AddSeconds(WindowSeconds);
                while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                    Thread.Sleep(100);
            }
            finally
            {
                var stopProps = new NativeMethods.EVENT_TRACE_PROPERTIES
                {
                    Wnode = new NativeMethods.WNODE_HEADER { BufferSize = (uint)structSize },
                };
                var stopBuffer = Marshal.AllocHGlobal(structSize);
                try
                {
                    Marshal.StructureToPtr(stopProps, stopBuffer, false);
                    NativeMethods.ControlTraceW(handle, null, stopBuffer, NativeMethods.EVENT_TRACE_CONTROL_STOP);
                }
                finally
                {
                    Marshal.FreeHGlobal(stopBuffer);
                }
            }

            return etlPath;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void StopKernelSession()
    {
        var structSize = Marshal.SizeOf<NativeMethods.EVENT_TRACE_PROPERTIES>();
        var buffer = Marshal.AllocHGlobal(structSize);
        try
        {
            var props = new NativeMethods.EVENT_TRACE_PROPERTIES
            {
                Wnode = new NativeMethods.WNODE_HEADER { BufferSize = (uint)structSize },
            };
            Marshal.StructureToPtr(props, buffer, false);
            NativeMethods.ControlTraceW(0, NativeMethods.KernelLoggerName, buffer, NativeMethods.EVENT_TRACE_CONTROL_STOP);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ---- Analysis ----

    private EtwWindowReport Analyze(string etlPath, DateTime windowStart)
    {
        var report = new EtwWindowReport { WindowStartLocal = windowStart, WindowSeconds = WindowSeconds };
        var perCore = Enumerable.Range(0, _topology.TotalCores)
            .Select(i => new CoreEtwResult { GlobalProcessorIndex = i })
            .ToArray();
        var drivers = new Dictionary<string, DriverLatencyEntry>(StringComparer.OrdinalIgnoreCase);

        using var processor = TraceProcessor.Create(etlPath);
        var pending = processor.UseInterruptHandlingData();
        processor.Process();

        if (pending.HasResult)
        {
            var data = pending.Result;
            foreach (var activity in data.Activity)
                Accumulate(activity.Interval, (double)activity.Duration.TotalMicroseconds, perCore, drivers);
        }

        foreach (var core in perCore.Where(c => c.DpcCount > 0 || c.IsrCount > 0))
            report.PerCore.Add(core);

        foreach (var entry in drivers.Values
                     .OrderByDescending(d => d.MaxMicroseconds)
                     .Take(15))
            report.TopDrivers.Add(entry);

        if (report.PerCore.Count == 0)
            report.Note = "The window contained no DPC/ISR events. The system may have been idle.";

        return report;
    }

    private static void Accumulate(
        IInterruptHandlingInterval interval,
        double microseconds,
        CoreEtwResult[] perCore,
        Dictionary<string, DriverLatencyEntry> drivers)
    {
        var coreIndex = (int)interval.Processor;
        if (coreIndex < 0 || coreIndex >= perCore.Length) return;

        var core = perCore[coreIndex];
        var isDpc = interval.Type == InterruptHandlingType.DeferredProcedureCall;
        if (isDpc)
        {
            core.DpcCount++;
            core.MaxDpcMicroseconds = Math.Max(core.MaxDpcMicroseconds, microseconds);
        }
        else
        {
            core.IsrCount++;
            core.MaxIsrMicroseconds = Math.Max(core.MaxIsrMicroseconds, microseconds);
        }

        var image = interval.HandlerImage;
        var driverName = image?.FileName ?? "(unknown driver)";
        if (!drivers.TryGetValue(driverName, out var entry))
        {
            entry = new DriverLatencyEntry { DriverName = driverName, DriverPath = image?.Path };
            drivers[driverName] = entry;
        }
        entry.ExecutionCount++;
        if (isDpc)
        {
            entry.MaxDpcMicroseconds = Math.Max(entry.MaxDpcMicroseconds, microseconds);
            entry.TotalDpcMicroseconds += microseconds;
        }
        else
        {
            entry.MaxIsrMicroseconds = Math.Max(entry.MaxIsrMicroseconds, microseconds);
            entry.TotalIsrMicroseconds += microseconds;
        }
    }

    private sealed class EtwSessionException : Exception
    {
        public bool IsAlreadyRunning => ErrorCode == ErrorAlreadyExists;
        public int ErrorCode { get; }

        public EtwSessionException(int errorCode)
            : base($"StartTrace failed with Win32 error {errorCode}.")
            => ErrorCode = errorCode;
    }
}
