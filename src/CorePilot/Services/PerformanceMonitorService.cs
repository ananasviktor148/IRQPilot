using System.Diagnostics;
using CorePilot.Models;

namespace CorePilot.Services;

/// <summary>
/// Samples per-logical-CPU interrupt counters every 500 ms:
/// % Interrupt Time, % DPC Time, Interrupts/sec, DPCs Queued/sec.
/// Keeps a rolling 60 s history per core for the recommendation engine.
/// </summary>
public sealed class PerformanceMonitorService : IDisposable
{
    public const int SampleIntervalMs = 500;
    public const int HistorySeconds = 60;
    private static readonly int HistoryCapacity = HistorySeconds * 1000 / SampleIntervalMs;

    private readonly ProcessorTopology _topology;
    private readonly List<CounterSet> _counters = new(); // index = global core index
    private readonly Queue<CoreSample>[] _history;
    private readonly object _historyLock = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>Raised on the sampling thread with samples indexed by global core index.</summary>
    public event Action<CoreSample[]>? Sampled;

    /// <summary>Raised when the counter set could not be initialized (e.g. counters corrupted).</summary>
    public event Action<string>? Failed;

    public PerformanceMonitorService(ProcessorTopology topology)
    {
        _topology = topology;
        _history = new Queue<CoreSample>[topology.TotalCores];
        for (var i = 0; i < _history.Length; i++)
            _history[i] = new Queue<CoreSample>(HistoryCapacity + 1);
    }

    public void Start()
    {
        if (_loop is not null) return;
        try
        {
            InitializeCounters();
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Failed?.Invoke(
                "Windows performance counters are unavailable (" + ex.Message +
                "). Core Radar needs the Processor counter set; try rebuilding counters with 'lodctr /R'.");
            return;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Stop() => _cts?.Cancel();

    private void InitializeCounters()
    {
        _counters.Clear();
        var category = new PerformanceCounterCategory("Processor");

        // Instance names are "0".."N-1" for a single group and "g,i" when the
        // machine has multiple processor groups.
        var instanceToGlobal = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in category.GetInstanceNames())
        {
            if (name.Equals("_Total", StringComparison.OrdinalIgnoreCase)) continue;
            if (TryMapInstance(name, out var global) && global >= 0 && global < _topology.TotalCores)
                instanceToGlobal[name] = global;
        }

        for (var i = 0; i < _topology.TotalCores; i++)
        {
            var instance = instanceToGlobal.FirstOrDefault(kv => kv.Value == i).Key;
            if (instance is null)
                throw new InvalidOperationException($"No Processor counter instance found for core {i}.");
            _counters.Add(new CounterSet(instance));
        }

        // Prime the counters; the first NextValue of a rate counter is meaningless.
        foreach (var set in _counters) set.Read();
    }

    private bool TryMapInstance(string instance, out int globalIndex)
    {
        globalIndex = -1;
        var comma = instance.IndexOf(',');
        if (comma < 0)
        {
            if (!int.TryParse(instance, out var index)) return false;
            globalIndex = index;
            return true;
        }
        if (!ushort.TryParse(instance[..comma], out var group)) return false;
        if (!int.TryParse(instance[(comma + 1)..], out var inGroup)) return false;
        globalIndex = _topology.GlobalIndexOf(group, inGroup);
        return globalIndex >= 0;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(SampleIntervalMs));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            var samples = new CoreSample[_counters.Count];
            for (var i = 0; i < _counters.Count; i++)
                samples[i] = _counters[i].Read();

            lock (_historyLock)
            {
                for (var i = 0; i < samples.Length; i++)
                {
                    var queue = _history[i];
                    queue.Enqueue(samples[i]);
                    while (queue.Count > HistoryCapacity) queue.Dequeue();
                }
            }
            Sampled?.Invoke(samples);
        }
    }

    /// <summary>
    /// Ranks cores by combined interrupt+DPC load over the rolling window,
    /// lowest load first. Core 0 excluded unless requested.
    /// </summary>
    public List<(int GlobalIndex, double AverageLoadScore)> RankLeastLoadedCores(bool excludeCoreZero = true)
    {
        var ranking = new List<(int, double)>();
        lock (_historyLock)
        {
            for (var i = 0; i < _history.Length; i++)
            {
                if (excludeCoreZero && i == 0) continue;
                var queue = _history[i];
                if (queue.Count == 0) continue;
                ranking.Add((i, queue.Average(s => s.LoadScore)));
            }
        }
        return ranking.OrderBy(r => r.Item2).ToList();
    }

    /// <summary>Recent history (oldest first) for a core, for charting.</summary>
    public CoreSample[] GetHistory(int globalIndex)
    {
        lock (_historyLock)
        {
            return globalIndex >= 0 && globalIndex < _history.Length
                ? _history[globalIndex].ToArray()
                : Array.Empty<CoreSample>();
        }
    }

    public void Dispose()
    {
        Stop();
        foreach (var set in _counters) set.Dispose();
    }

    private sealed class CounterSet : IDisposable
    {
        private readonly PerformanceCounter _interruptTime;
        private readonly PerformanceCounter _dpcTime;
        private readonly PerformanceCounter _interruptsPerSec;
        private readonly PerformanceCounter _dpcsQueuedPerSec;

        public CounterSet(string instance)
        {
            const string category = "Processor";
            _interruptTime = new PerformanceCounter(category, "% Interrupt Time", instance, readOnly: true);
            _dpcTime = new PerformanceCounter(category, "% DPC Time", instance, readOnly: true);
            _interruptsPerSec = new PerformanceCounter(category, "Interrupts/sec", instance, readOnly: true);
            _dpcsQueuedPerSec = new PerformanceCounter(category, "DPCs Queued/sec", instance, readOnly: true);
        }

        public CoreSample Read()
        {
            try
            {
                return new CoreSample
                {
                    InterruptTimePercent = _interruptTime.NextValue(),
                    DpcTimePercent = _dpcTime.NextValue(),
                    InterruptsPerSec = _interruptsPerSec.NextValue(),
                    DpcsQueuedPerSec = _dpcsQueuedPerSec.NextValue(),
                };
            }
            catch (InvalidOperationException)
            {
                // Counter instance can vanish transiently (e.g. core parked); report zero.
                return CoreSample.Zero;
            }
        }

        public void Dispose()
        {
            _interruptTime.Dispose();
            _dpcTime.Dispose();
            _interruptsPerSec.Dispose();
            _dpcsQueuedPerSec.Dispose();
        }
    }
}
