using CommunityToolkit.Mvvm.ComponentModel;
using CorePilot.Models;

namespace CorePilot.ViewModels;

/// <summary>Live state of one logical core, shared by the radar grid and the device CPU selector.</summary>
public sealed partial class CoreViewModel : ObservableObject
{
    public int GlobalIndex { get; }
    public ushort Group { get; }
    public int IndexInGroup { get; }

    /// <summary>"7" for single-group machines, "1.3" (group.core) when grouped.</summary>
    public string Label { get; }

    [ObservableProperty] private double _interruptTimePercent;
    [ObservableProperty] private double _dpcTimePercent;
    [ObservableProperty] private double _interruptsPerSec;
    [ObservableProperty] private double _dpcsQueuedPerSec;

    [ObservableProperty] private double _maxDpcMicroseconds;
    [ObservableProperty] private double _maxIsrMicroseconds;
    [ObservableProperty] private bool _hasEtwData;

    /// <summary>Highlighted by "recommend least-used cores".</summary>
    [ObservableProperty] private bool _isRecommended;

    /// <summary>Checked in the device affinity selector.</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>Checked in the process affinity selector (kept separate from device selection).</summary>
    [ObservableProperty] private bool _isProcessSelected;

    public double LoadScore => InterruptTimePercent + DpcTimePercent;

    public CoreViewModel(int globalIndex, ushort group, int indexInGroup, bool multipleGroups)
    {
        GlobalIndex = globalIndex;
        Group = group;
        IndexInGroup = indexInGroup;
        Label = multipleGroups ? $"{group}.{indexInGroup}" : indexInGroup.ToString();
    }

    public void Apply(CoreSample sample)
    {
        InterruptTimePercent = sample.InterruptTimePercent;
        DpcTimePercent = sample.DpcTimePercent;
        InterruptsPerSec = sample.InterruptsPerSec;
        DpcsQueuedPerSec = sample.DpcsQueuedPerSec;
        OnPropertyChanged(nameof(LoadScore));
    }

    public void Apply(CoreEtwResult etw)
    {
        MaxDpcMicroseconds = etw.MaxDpcMicroseconds;
        MaxIsrMicroseconds = etw.MaxIsrMicroseconds;
        HasEtwData = true;
    }
}
