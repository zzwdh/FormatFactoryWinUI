using System.Collections.ObjectModel;
using System.ComponentModel;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.ViewModels;

/// <summary>One source header over independently scheduled output tasks.</summary>
public sealed class OutputTaskGroupViewModel : ObservableCollection<MediaTaskViewModel>, IDisposable
{
    private bool _expanded = true;
    private bool _disposed;

    public OutputTaskGroupViewModel(Guid id, bool grouped) { Id = id; IsGrouped = grouped; }
    public Guid Id { get; }
    public bool IsGrouped { get; }
    public string Title => IsGrouped ? this.FirstOrDefault()?.FileName ?? string.Empty : string.Empty;
    public string ToggleLabel => IsGrouped ? LocalizationService.T(IsExpanded ? "MultiOutputCollapse" : "MultiOutputExpand") : string.Empty;
    public string Summary => IsGrouped ? LocalizationService.T("MultiOutputProgress", this.Count(task => task.State == ConversionState.Completed), Count) : string.Empty;
    public double Progress => !IsGrouped || Count == 0 ? 0 : this.Average(task => task.State == ConversionState.Completed ? 100 : task.Progress);
    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (!IsGrouped || _expanded == value) return;
            _expanded = value;
            foreach (var task in this) task.OutputGroupExpanded = value;
            Notify();
        }
    }

    protected override void InsertItem(int index, MediaTaskViewModel item)
    {
        if (IsGrouped) item.PropertyChanged += TaskChanged;
        item.OutputGroupExpanded = IsExpanded;
        base.InsertItem(index, item);
        Notify();
    }
    protected override void RemoveItem(int index)
    {
        this[index].PropertyChanged -= TaskChanged;
        this[index].OutputGroupExpanded = true;
        base.RemoveItem(index);
        Notify();
    }
    protected override void ClearItems()
    {
        foreach (var task in this) { task.PropertyChanged -= TaskChanged; task.OutputGroupExpanded = true; }
        base.ClearItems();
        Notify();
    }
    private void TaskChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MediaTaskViewModel.Progress) or nameof(MediaTaskViewModel.State) or
            nameof(MediaTaskViewModel.StateText) or nameof(MediaTaskViewModel.FileName)) Notify();
    }
    public void RefreshLocalization() => Notify();
    private void Notify()
    {
        // Hidden segment headers still have live XAML bindings. Ordinary rows need
        // collection notifications only; aggregating them on every update is quadratic.
        if (!IsGrouped) return;
        OnPropertyChanged(new(nameof(Title)));
        OnPropertyChanged(new(nameof(Summary)));
        OnPropertyChanged(new(nameof(Progress)));
        OnPropertyChanged(new(nameof(IsExpanded)));
        OnPropertyChanged(new(nameof(ToggleLabel)));
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clear();
    }
}
