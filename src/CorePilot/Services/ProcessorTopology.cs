using CorePilot.Interop;

namespace CorePilot.Services;

/// <summary>Static snapshot of processor groups and logical core counts.</summary>
public sealed class ProcessorTopology
{
    /// <summary>(group, indexInGroup) for each logical core, in global order.</summary>
    public IReadOnlyList<(ushort Group, int IndexInGroup)> Cores { get; }

    public IReadOnlyList<ushort> Groups { get; }

    public int TotalCores => Cores.Count;

    public bool HasMultipleGroups => Groups.Count > 1;

    private ProcessorTopology(List<(ushort, int)> cores, List<ushort> groups)
    {
        Cores = cores;
        Groups = groups;
    }

    public static ProcessorTopology Query()
    {
        var groupCount = NativeMethods.GetActiveProcessorGroupCount();
        var cores = new List<(ushort, int)>();
        var groups = new List<ushort>();
        for (ushort g = 0; g < groupCount; g++)
        {
            var count = (int)NativeMethods.GetActiveProcessorCount(g);
            if (count == 0) continue;
            groups.Add(g);
            for (var i = 0; i < count; i++)
                cores.Add((g, i));
        }
        if (cores.Count == 0)
        {
            // Should not happen, but never return an empty topology.
            for (var i = 0; i < Environment.ProcessorCount; i++)
                cores.Add((0, i));
            groups.Add(0);
        }
        return new ProcessorTopology(cores, groups);
    }

    /// <summary>Group sizes in group order (for perf counter instance mapping).</summary>
    public int CountInGroup(ushort group) => Cores.Count(c => c.Group == group);

    public int GlobalIndexOf(ushort group, int indexInGroup)
    {
        var global = 0;
        foreach (var g in Groups)
        {
            if (g == group) return global + indexInGroup;
            global += CountInGroup(g);
        }
        return -1;
    }
}
