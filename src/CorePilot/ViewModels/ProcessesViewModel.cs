using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CorePilot.Services;

namespace CorePilot.ViewModels;

public sealed partial class ProcessesViewModel : ObservableObject
{
    private readonly ProcessAffinityService _service;
    private readonly CoreRadarViewModel _radar;
    private readonly List<ProcessViewModel> _allProcesses = new();

    public ObservableCollection<ProcessViewModel> FilteredProcesses { get; } = new();

    /// <summary>Cores shown for the selected process; all share live radar load data.</summary>
    public ObservableCollection<CoreViewModel> ProcessCores { get; } = new();

    [ObservableProperty] private string _searchText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedProcess))]
    [NotifyPropertyChangedFor(nameof(HasEditableProcess))]
    [NotifyPropertyChangedFor(nameof(CanEditSelected))]
    [NotifyPropertyChangedFor(nameof(CanApplySelection))]
    [NotifyPropertyChangedFor(nameof(SelectedCoreCount))]
    [NotifyPropertyChangedFor(nameof(SelectionText))]
    private ProcessViewModel? _selectedProcess;

    [ObservableProperty] private string _listStatus = "Enumerating processes…";
    [ObservableProperty] private string _detailStatus = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditSelected))]
    [NotifyPropertyChangedFor(nameof(CanApplySelection))]
    private bool _isBusy;

    public bool HasSelectedProcess => SelectedProcess is not null;
    public bool HasEditableProcess => SelectedProcess?.IsEditable == true;
    public bool CanEditSelected => HasEditableProcess && !IsBusy;
    public bool CanApplySelection => CanEditSelected && SelectedCoreCount > 0;

    public int SelectedCoreCount => ProcessCores.Count(c => c.IsProcessSelected);

    public string SelectionText
    {
        get
        {
            if (SelectedProcess is null) return "";
            var selected = ProcessCores.Where(c => c.IsProcessSelected).ToList();
            if (selected.Count == 0) return "No cores selected";

            ulong mask = 0;
            foreach (var core in selected) mask |= 1UL << core.IndexInGroup;
            return $"{selected.Count} selected · {AffinityMaskText.Format(mask)}";
        }
    }

    public ProcessesViewModel(ProcessAffinityService service, CoreRadarViewModel radar)
    {
        _service = service;
        _radar = radar;
        foreach (var core in radar.Cores)
            core.PropertyChanged += OnCorePropertyChanged;
    }

    public async Task LoadAsync()
    {
        if (IsBusy) return;

        var previousPid = SelectedProcess?.Pid;
        IsBusy = true;
        ListStatus = "Enumerating processes…";
        try
        {
            var infos = await _service.EnumerateAsync();
            _allProcesses.Clear();
            foreach (var info in infos)
            {
                var coresInGroup = _radar.Cores.Count(c => c.Group == info.Group);
                _allProcesses.Add(new ProcessViewModel(info, coresInGroup, _radar.HasMultipleGroups));
            }

            ApplyFilter(previousPid);
            var accessible = _allProcesses.Count(p => p.CanQuery);
            ListStatus = $"{_allProcesses.Count} processes · {accessible} accessible";
        }
        catch (Exception ex)
        {
            ListStatus = "Enumeration failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter(int? preferredPid = null)
    {
        var pid = preferredPid ?? SelectedProcess?.Pid;
        var query = SearchText.Trim();

        FilteredProcesses.Clear();
        foreach (var process in _allProcesses)
        {
            if (query.Length == 0
                || process.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || process.Pid.ToString().Contains(query, StringComparison.Ordinal))
            {
                FilteredProcesses.Add(process);
            }
        }

        var restored = pid is int wanted
            ? FilteredProcesses.FirstOrDefault(p => p.Pid == wanted)
            : null;
        SelectedProcess = restored
            ?? FilteredProcesses.FirstOrDefault(p => p.CanQuery)
            ?? FilteredProcesses.FirstOrDefault();
    }

    partial void OnSelectedProcessChanged(ProcessViewModel? value)
    {
        SyncProcessCores(value);
        DetailStatus = "";
    }

    private void SyncProcessCores(ProcessViewModel? process)
    {
        foreach (var core in _radar.Cores) core.IsProcessSelected = false;
        ProcessCores.Clear();

        if (process is not null && process.CanQuery)
        {
            foreach (var core in _radar.Cores.Where(c => c.Group == process.Group))
                ProcessCores.Add(core);
        }

        OnPropertyChanged(nameof(SelectedCoreCount));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(CanApplySelection));
    }

    private void OnCorePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CoreViewModel.IsProcessSelected)) return;
        OnPropertyChanged(nameof(SelectedCoreCount));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(CanApplySelection));
    }

    [RelayCommand]
    private async Task ApplyAffinityAsync()
    {
        var process = SelectedProcess;
        if (process is null || IsBusy) return;
        if (!process.IsEditable)
        {
            DetailStatus = process.WarningText;
            return;
        }

        ulong mask = 0;
        foreach (var core in ProcessCores.Where(c => c.IsProcessSelected))
            mask |= 1UL << core.IndexInGroup;

        if (mask == 0)
        {
            DetailStatus = "Select at least one core before applying.";
            return;
        }

        IsBusy = true;
        DetailStatus = $"Applying affinity to {process.DisplayName}…";
        try
        {
            await Task.Run(() => _service.SetAffinity(process.Pid, mask));
            process.MarkApplied(mask);
            var coreWord = process.CoreCount == 1 ? "core" : "cores";
            DetailStatus =
                $"Pinned {process.DisplayName} (PID {process.Pid}) to {process.CoreCount} {coreWord}. " +
                "The change is immediate and lasts until the process exits.";
        }
        catch (ProcessAffinityException ex)
        {
            DetailStatus = $"Could not change {process.DisplayName}: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void SelectAllCores()
    {
        var process = SelectedProcess;
        if (process is null || !CanEditSelected) return;

        foreach (var core in ProcessCores)
            core.IsProcessSelected =
                (process.SystemMask & (1UL << core.IndexInGroup)) != 0;

        DetailStatus = "All cores in this processor group selected. Click Apply affinity to pin.";
    }

    [RelayCommand]
    private void ClearCoreSelection()
    {
        if (!CanEditSelected) return;

        foreach (var core in ProcessCores) core.IsProcessSelected = false;
        DetailStatus = "Selection cleared. Pick the cores to pin, then apply.";
    }

    [RelayCommand]
    private async Task RefreshProcessesAsync() => await LoadAsync();
}
