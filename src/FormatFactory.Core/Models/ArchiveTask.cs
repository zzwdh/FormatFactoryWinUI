using System.ComponentModel;

namespace FormatFactoryWinUI.Models;

/// <summary>Live archive state. Checkpoints deliberately omit all in-memory passwords.</summary>
public sealed class ArchiveTask : INotifyPropertyChanged
{
    public ArchiveTask(string sourcePath, ArchiveConversionOptions options)
    { SourcePath = sourcePath; Options = options; HasEncryption = !string.IsNullOrEmpty(options.OutputPassword); }

    public Guid Id { get; internal set; } = Guid.NewGuid();
    public string SourcePath { get; }
    public ArchiveConversionOptions Options { get; private set; }
    public bool HasEncryption { get; private set; }
    public ArchiveTaskState State { get; private set; }
    public double Progress { get; private set; }
    public string? Phase { get; private set; }
    public string? ErrorKey { get; private set; }
    public string? PreviousErrorKey { get; private set; }
    public string OutputDirectory { get; internal set; } = string.Empty;
    public string OutputPath { get; internal set; } = string.Empty;
    public ConversionResultStatistics? ResultStatistics { get; internal set; }
    public DateTimeOffset? CompletedAtUtc { get; internal set; }
    public bool HistoryRecorded { get; internal set; }
    public bool HistorySaveFailed { get; internal set; }
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Refresh() => PropertyChanged?.Invoke(this, new(null));
    internal void SetState(ArchiveTaskState state, string? errorKey = null)
    {
        State = state; ErrorKey = errorKey; Phase = null;
        if (state == ArchiveTaskState.Succeeded) { Progress = 100; PreviousErrorKey = null; }
        Refresh();
    }

    internal void SetProgress(double progress, string phase)
    { Progress = Math.Clamp(progress, 0, 100); Phase = phase; Refresh(); }

    internal void SetOptions(ArchiveConversionOptions options)
    {
        PreviousErrorKey = ErrorKey ?? PreviousErrorKey;
        Options = options; HasEncryption = !string.IsNullOrEmpty(options.OutputPassword);
        Progress = 0; OutputPath = string.Empty; ResultStatistics = null; CompletedAtUtc = null;
        HistoryRecorded = false; HistorySaveFailed = false; SetState(ArchiveTaskState.Waiting);
    }

    public ArchiveTaskCheckpoint Snapshot() => new()
    {
        Id = Id, SourcePath = SourcePath, OutputDirectory = OutputDirectory, TargetFormat = Options.TargetFormat,
        StrictVerification = Options.StrictVerification, EncryptOutput = HasEncryption, State = State, ErrorKey = ErrorKey,
        PreviousErrorKey = PreviousErrorKey, OutputPath = OutputPath, ResultStatistics = ResultStatistics,
        CompletedAtUtc = CompletedAtUtc, HistoryRecorded = HistoryRecorded
    };

    internal static ArchiveTask Restore(ArchiveTaskCheckpoint saved)
    {
        var restored = saved.ForRestore();
        var task = new ArchiveTask(restored.SourcePath, new(restored.TargetFormat, restored.StrictVerification))
        {
            Id = restored.Id, HasEncryption = restored.EncryptOutput, PreviousErrorKey = restored.PreviousErrorKey,
            OutputDirectory = restored.OutputDirectory, OutputPath = restored.OutputPath,
            ResultStatistics = restored.ResultStatistics, CompletedAtUtc = restored.CompletedAtUtc,
            HistoryRecorded = restored.HistoryRecorded
        };
        task.SetState(restored.State, restored.ErrorKey);
        return task;
    }

    internal void RequireOutputPassword() { HasEncryption = true; Refresh(); }
    internal void PrepareRetryInFolder(string folder)
    { PreviousErrorKey = ErrorKey; OutputDirectory = folder; Progress = 0; SetState(ArchiveTaskState.Waiting); }
    internal void ClearPassword() => Options = Options with { OutputPassword = null };
}
