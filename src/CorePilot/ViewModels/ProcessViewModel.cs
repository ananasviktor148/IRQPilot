using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CorePilot.Services;

namespace CorePilot.ViewModels;

/// <summary>A process row plus its live affinity state, relative to one processor group.</summary>
public sealed partial class ProcessViewModel : ObservableObject
{
    private readonly int _coresInGroup;
    private readonly bool _multipleGroups;

    public int Pid { get; }
    public string Name { get; }

    public string DisplayName =>
        Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Name : Name + ".exe";

    [ObservableProperty]
    private ushort _group;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CoreCount))]
    [NotifyPropertyChangedFor(nameof(CoreSummary))]
    [NotifyPropertyChangedFor(nameof(CurrentAffinityText))]
    private ulong _affinityMask;

    [ObservableProperty] private ulong _systemMask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditable))]
    [NotifyPropertyChangedFor(nameof(CoreSummary))]
    [NotifyPropertyChangedFor(nameof(CurrentAffinityText))]
    [NotifyPropertyChangedFor(nameof(WarningText))]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    private bool _canQuery;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditable))]
    [NotifyPropertyChangedFor(nameof(CoreSummary))]
    [NotifyPropertyChangedFor(nameof(WarningText))]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    private bool _canSet;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditable))]
    [NotifyPropertyChangedFor(nameof(CoreSummary))]
    [NotifyPropertyChangedFor(nameof(CurrentAffinityText))]
    [NotifyPropertyChangedFor(nameof(WarningText))]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    private bool _spansMultipleGroups;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentAffinityText))]
    [NotifyPropertyChangedFor(nameof(WarningText))]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    private string _accessError = "";

    public int CoreCount => BitOperations.PopCount(AffinityMask);

    public bool IsEditable => CanQuery && CanSet && !SpansMultipleGroups;

    public string CoreSummary =>
        !CanQuery ? "NO ACCESS" :
        !CanSet ? "PROTECTED" :
        SpansMultipleGroups ? "MULTI-GROUP" :
        CoreCount == 1 ? "1 CORE" : $"{CoreCount} CORES";

    public string CurrentAffinityText =>
        !CanQuery ? (string.IsNullOrWhiteSpace(AccessError) ? "Not available." : AccessError) :
        $"{CoreCount} of {_coresInGroup} cores · {AffinityMaskText.Format(AffinityMask)}";

    public string ProcessorGroupText => $"PROCESSOR GROUP {Group}";

    public string CoreGroupSummary =>
        _multipleGroups
            ? $"{_coresInGroup} logical processors in group {Group}"
            : $"{_coresInGroup} logical processors";

    public string WarningText =>
        !CanQuery
            ? (string.IsNullOrWhiteSpace(AccessError)
                ? "Affinity cannot be read for this process."
                : AccessError)
            : !CanSet
                ? "Windows does not allow changing affinity for this protected process."
                : SpansMultipleGroups
                    ? "This process spans multiple processor groups, so pinning is disabled here."
                    : "";

    public bool HasWarning => WarningText.Length > 0;

    public ProcessViewModel(ProcessAffinityInfo info, int coresInGroup, bool multipleGroups)
    {
        Pid = info.Pid;
        Name = info.Name;
        _coresInGroup = Math.Max(coresInGroup,
            info.CanQuery ? BitOperations.PopCount(info.SystemMask) : 0);
        _multipleGroups = multipleGroups;
        Apply(info);
    }

    public void Apply(ProcessAffinityInfo info)
    {
        Group = info.Group;
        AffinityMask = info.AffinityMask;
        SystemMask = info.SystemMask;
        CanQuery = info.CanQuery;
        CanSet = info.CanSet;
        SpansMultipleGroups = info.SpansMultipleGroups;
        AccessError = info.AccessError ?? "";
        OnPropertyChanged(nameof(ProcessorGroupText));
        OnPropertyChanged(nameof(CoreGroupSummary));
    }

    public void MarkApplied(ulong mask) => AffinityMask = mask;
}

internal static class AffinityMaskText
{
    public static string Format(ulong mask)
    {
        if (mask == 0) return "none";

        var parts = new List<string>();
        var index = 0;
        while (index < 64)
        {
            if ((mask & (1UL << index)) == 0)
            {
                index++;
                continue;
            }

            var start = index;
            while (index < 64 && (mask & (1UL << index)) != 0) index++;
            var end = index - 1;
            parts.Add(start == end ? start.ToString() : $"{start}-{end}");
        }
        return string.Join(", ", parts);
    }
}
