namespace FormatFactoryWinUI.Models;

public enum ArchiveTaskState { Waiting, Running, AwaitingPassword, Succeeded, Failed, Cancelled }

/// <summary>A durable task description deliberately has no password or arbitrary option dictionary.</summary>
public sealed record ArchiveTaskCheckpoint
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string SourcePath { get; init; } = string.Empty;
    public string OutputDirectory { get; init; } = string.Empty;
    public ArchiveFormat TargetFormat { get; init; }
    public bool StrictVerification { get; init; }
    public bool EncryptOutput { get; init; }
    public ArchiveTaskState State { get; init; }
    public string? ErrorKey { get; init; }
    public string? PreviousErrorKey { get; init; }
    public string OutputPath { get; init; } = string.Empty;
    public ConversionResultStatistics? ResultStatistics { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public bool HistoryRecorded { get; init; }

    public ArchiveTaskCheckpoint ForRestore() => State is ArchiveTaskState.Running or ArchiveTaskState.AwaitingPassword
        ? this with { State = ArchiveTaskState.Waiting, ErrorKey = null, OutputPath = string.Empty,
            ResultStatistics = null, CompletedAtUtc = null, HistoryRecorded = false }
        : this;
}

public sealed record ArchiveTaskSnapshot
{
    public int Version { get; init; } = 1;
    public string OutputDirectory { get; init; } = string.Empty;
    public ArchiveFormat DefaultTargetFormat { get; init; }
    public bool DefaultStrictVerification { get; init; }
    public bool DefaultEncryptOutput { get; init; }
    public IReadOnlyList<ArchiveTaskCheckpoint> Tasks { get; init; } = [];
}
