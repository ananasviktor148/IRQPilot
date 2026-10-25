namespace CorePilot.Models;

/// <summary>One 500 ms performance-counter sample for a single logical core.</summary>
public sealed class CoreSample
{
    public double InterruptTimePercent { get; init; }
    public double DpcTimePercent { get; init; }
    public double InterruptsPerSec { get; init; }
    public double DpcsQueuedPerSec { get; init; }

    /// <summary>Combined load metric used for heatmap coloring and ranking.</summary>
    public double LoadScore => InterruptTimePercent + DpcTimePercent;

    public static CoreSample Zero { get; } = new();
}

/// <summary>Per-core result of one ETW capture window.</summary>
public sealed class CoreEtwResult
{
    public int GlobalProcessorIndex { get; init; }
    public double MaxDpcMicroseconds { get; set; }
    public double MaxIsrMicroseconds { get; set; }
    public int DpcCount { get; set; }
    public int IsrCount { get; set; }
}

/// <summary>Per-driver aggregate of one ETW capture window.</summary>
public sealed class DriverLatencyEntry
{
    public required string DriverName { get; init; }
    public string? DriverPath { get; init; }
    public double MaxDpcMicroseconds { get; set; }
    public double MaxIsrMicroseconds { get; set; }
    public double TotalDpcMicroseconds { get; set; }
    public double TotalIsrMicroseconds { get; set; }
    public int ExecutionCount { get; set; }

    public double MaxMicroseconds => Math.Max(MaxDpcMicroseconds, MaxIsrMicroseconds);
}

public sealed class EtwWindowReport
{
    public DateTime WindowStartLocal { get; init; }
    public double WindowSeconds { get; init; }
    public List<CoreEtwResult> PerCore { get; } = new();
    public List<DriverLatencyEntry> TopDrivers { get; } = new();
    public string? Note { get; set; }
}
