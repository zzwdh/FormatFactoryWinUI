using System.ComponentModel;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

// Keep the existing binding type name while compiling its presentation logic outside WinUI.
namespace FormatFactoryWinUI.Views;

public sealed class ArchiveTaskItem : INotifyPropertyChanged, IDisposable
{
    private bool _busy;
    internal ArchiveTask Model { get; }
    internal ArchiveTaskItem(ArchiveTask model)
    { Model = model; Model.PropertyChanged += ModelChanged; }
    private void ModelChanged(object? sender, PropertyChangedEventArgs args) => Refresh();
    private bool _encrypted => Model.HasEncryption;
    private string? _errorKey => Model.ErrorKey;
    private string? _previousErrorKey => Model.PreviousErrorKey;
    private string? _phase => Model.Phase;
    public Guid Id => Model.Id;
    public string SourcePath => Model.SourcePath;
    public string FileName => Path.GetFileName(SourcePath);
    internal string OutputDirectory => Model.OutputDirectory;
    internal ArchiveConversionOptions Options => Model.Options;
    internal ConversionResultStatistics? ResultStatistics => Model.ResultStatistics;
    internal DateTimeOffset? CompletedAtUtc => Model.CompletedAtUtc;
    internal bool HistoryRecorded => Model.HistoryRecorded;
    internal bool HasEncryption => Model.HasEncryption;
    public ArchiveTaskState State => Model.State;
    public double Progress => Model.Progress;
    public string OutputPath => Model.OutputPath;
    public string ResultSummary => FormatFactoryWinUI.ViewModels.ConversionResultSummaryFormatter.Format(ResultStatistics);
    public string HistoryStatus => Model.HistorySaveFailed ? LocalizationService.T("ArchiveHistorySaveFailed") : string.Empty;
    public bool HasResultSummary => !string.IsNullOrEmpty(ResultSummary);
    public bool HasHistoryStatus => !string.IsNullOrEmpty(HistoryStatus);
    public bool HasPreviousFailure => !string.IsNullOrEmpty(PreviousFailure);
    public bool HasOutputPath => !string.IsNullOrEmpty(OutputPath);
    public bool CanEdit => !_busy && State != ArchiveTaskState.Succeeded;
    public bool CanRetry => !_busy && State is ArchiveTaskState.Failed or ArchiveTaskState.Cancelled;
    public bool CanChooseOutputFolder => !_busy && State == ArchiveTaskState.Failed &&
        _errorKey is "ArchiveErrorInsufficientSpace" or "ArchiveErrorOutputUnavailable";
    public string ChooseOutputFolderLabel => LocalizationService.T("ArchiveBrowse");
    public string PreviousFailure => State is ArchiveTaskState.Waiting or ArchiveTaskState.Running && _previousErrorKey is not null
        ? LocalizationService.T("ArchivePreviousFailure", LocalizationService.T(_previousErrorKey)) : string.Empty;
    public bool CanOpen => State == ArchiveTaskState.Succeeded && OutputPath.Length > 0;
    public string SettingsLabel => LocalizationService.T("ArchiveTaskSettings");
    public string RetryLabel => LocalizationService.T("ArchiveRetry");
    public string OpenLabel => LocalizationService.T("ArchiveOpenOutput");
    public string RemoveLabel => LocalizationService.T(State is ArchiveTaskState.Running or ArchiveTaskState.AwaitingPassword ? "ArchiveCancel" : "ArchiveRemove");
    public string SettingsSummary => LocalizationService.T("ArchiveTargetSummary", ArchiveFormats.DisplayName(Options.TargetFormat),
        LocalizationService.T(Options.StrictVerification ? "ArchiveVerificationStrict" : "ArchiveVerificationStandard"),
        LocalizationService.T(_encrypted ? "ArchiveEncrypted" : "ArchiveUnencrypted"));
    public string Status => LocalizationService.T(_errorKey ?? (State == ArchiveTaskState.Running && _phase is not null
        ? _phase switch
        {
            "inspecting" => "ArchivePhaseInspecting", "extracting" => "ArchivePhaseExtracting",
            "compressing" => "ArchivePhaseCompressing", "verifying" => "ArchivePhaseVerifying",
            "comparing" => "ArchivePhaseComparing", "completed" => "ArchivePhaseCompleted", _ => "ArchiveRunning"
        }
        : State switch
        {
            ArchiveTaskState.Waiting => "ArchiveWaiting", ArchiveTaskState.Running => "ArchiveRunning",
            ArchiveTaskState.AwaitingPassword => "ArchiveAwaitingPassword", ArchiveTaskState.Succeeded => "ArchiveSucceeded",
            ArchiveTaskState.Cancelled => "ArchiveCancelled", _ => "ArchiveFailed"
        }));

    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Refresh() => PropertyChanged?.Invoke(this, new(null));
    internal void SetOptions(ArchiveConversionOptions options) => Model.SetOptions(options);
    internal void PrepareRetryInFolder(string folder) => Model.PrepareRetryInFolder(folder);
    internal void SetBatchBusy(bool busy) { if (_busy == busy) return; _busy = busy; Refresh(); }
    public void Dispose() => Model.PropertyChanged -= ModelChanged;
}
