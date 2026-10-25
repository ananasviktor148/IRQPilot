using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CorePilot.Models;
using CorePilot.Services;

namespace CorePilot.ViewModels;

/// <summary>One chartable metric with a rolling per-core history snapshot.</summary>
public sealed partial class MetricSeries : ObservableObject
{
    public string Title { get; }
    public string Unit { get; }
    public Func<CoreSample, double> ValueOf { get; }

    [ObservableProperty] private IReadOnlyList<double[]> _series = Array.Empty<double[]>();
    [ObservableProperty] private IReadOnlyList<string> _labels = Array.Empty<string>();

    public MetricSeries(string title, string unit, Func<CoreSample, double> valueOf)
    {
        Title = title;
        Unit = unit;
        ValueOf = valueOf;
    }

    public void Update(Func<int, CoreSample[]> historyForCore, IReadOnlyList<CoreViewModel> cores)
    {
        var series = new double[cores.Count][];
        for (var i = 0; i < cores.Count; i++)
            series[i] = historyForCore(i).Select(ValueOf).ToArray();
        Series = series;
        Labels = cores.Select(c => c.Label).ToArray();
    }
}

public sealed partial class CoreRadarViewModel : ObservableObject
{
    private readonly PerformanceMonitorService _monitor;
    private readonly EtwLatencyService _etw;
    private readonly ProcessorTopology _topology;

    public ObservableCollection<CoreViewModel> Cores { get; }

    public ObservableCollection<DriverLatencyEntry> TopDrivers { get; } = new();

    public IReadOnlyList<MetricSeries> Metrics { get; }

    public IReadOnlyList<ushort> Groups => _topology.Groups;
    public bool HasMultipleGroups => _topology.HasMultipleGroups;

    [ObservableProperty] private string _statusText = "Sampling every 500 ms";
    [ObservableProperty] private bool _etwEnabled;
    [ObservableProperty] private string _etwStatus = "ETW off";
    [ObservableProperty] private string _recommendationText = "";
    [ObservableProperty] private bool _excludeCoreZero = true;
    [ObservableProperty] private int _recommendedCount = 2;

    /// <summary>Raised when the user clicks "assign recommended cores to the selected device".</summary>
    public event Action<IReadOnlyList<CoreViewModel>>? AssignRecommendedRequested;

    public CoreRadarViewModel(
        ProcessorTopology topology,
        PerformanceMonitorService monitor,
        EtwLatencyService etw)
    {
        _topology = topology;
        _monitor = monitor;
        _etw = etw;

        Cores = new ObservableCollection<CoreViewModel>(
            topology.Cores.Select((c, i) =>
                new CoreViewModel(i, c.Group, c.IndexInGroup, topology.HasMultipleGroups)));

        Metrics = new[]
        {
            new MetricSeries("% Interrupt Time", "%", s => s.InterruptTimePercent),
            new MetricSeries("% DPC Time", "%", s => s.DpcTimePercent),
            new MetricSeries("Interrupts/sec", "/s", s => s.InterruptsPerSec),
            new MetricSeries("DPCs Queued/sec", "/s", s => s.DpcsQueuedPerSec),
        };

        _monitor.Sampled += OnSampled;
        _monitor.Failed += msg => UiDispatch.Run(() => StatusText = msg);
        _etw.ReportReady += OnEtwReport;
        _etw.ErrorOccurred += msg => UiDispatch.Run(() =>
        {
            EtwStatus = msg;
            EtwEnabled = false;
        });
    }

    public void Start() => _monitor.Start();

    public void Stop()
    {
        _monitor.Stop();
        _ = _etw.StopAsync();
    }

    private void OnSampled(CoreSample[] samples)
    {
        UiDispatch.Run(() =>
        {
            for (var i = 0; i < samples.Length && i < Cores.Count; i++)
                Cores[i].Apply(samples[i]);
            foreach (var metric in Metrics)
                metric.Update(_monitor.GetHistory, Cores);
        });
    }

    private void OnEtwReport(EtwWindowReport report)
    {
        UiDispatch.Run(() =>
        {
            foreach (var coreResult in report.PerCore)
            {
                if (coreResult.GlobalProcessorIndex < Cores.Count)
                    Cores[coreResult.GlobalProcessorIndex].Apply(coreResult);
            }
            TopDrivers.Clear();
            foreach (var driver in report.TopDrivers)
                TopDrivers.Add(driver);
            EtwStatus = report.Note
                ?? $"Last window {report.WindowStartLocal:HH:mm:ss} · {report.PerCore.Count} active cores";
        });
    }

    async partial void OnEtwEnabledChanged(bool value)
    {
        if (value)
        {
            EtwStatus = $"Capturing {EtwLatencyService.WindowSeconds} s window…";
            _etw.Start();
        }
        else
        {
            await _etw.StopAsync();
            EtwStatus = "ETW off";
        }
    }

    [RelayCommand]
    private void RecommendLeastUsed()
    {
        var ranking = _monitor.RankLeastLoadedCores(ExcludeCoreZero);
        if (ranking.Count == 0)
        {
            RecommendationText = "Not enough samples yet — wait a few seconds.";
            return;
        }

        foreach (var core in Cores) core.IsRecommended = false;

        var count = Math.Clamp(RecommendedCount, 1, ranking.Count);
        var chosen = ranking.Take(count).Select(r => r.GlobalIndex).ToList();
        foreach (var index in chosen)
            Cores[index].IsRecommended = true;

        RecommendationText =
            "Least interrupt load (60 s avg): " +
            string.Join(", ", chosen.Select(i => $"{Cores[i].Label} ({ranking.First(r => r.GlobalIndex == i).AverageLoadScore:F1}%)"));
    }

    [RelayCommand]
    private void AssignRecommendedToDevice()
    {
        var recommended = Cores.Where(c => c.IsRecommended).ToList();
        if (recommended.Count > 0)
            AssignRecommendedRequested?.Invoke(recommended);
    }
}
