using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FormatFactoryWinUI.Models;

/// <summary>
/// UI-agnostic conversion task. Presentation text and WinUI-specific state belong
/// in MediaTaskViewModel so conversion and persistence services can use this model.
/// </summary>
public sealed class MediaTask : INotifyPropertyChanged
{
    private ContentKind _contentKind;
    private ContentProbeResult _contentProbe = new MediaProbeResult();
    private string _sourceFormatId = string.Empty;
    private string _targetFormat = string.Empty;
    private string _targetFormatId = string.Empty;
    private string _engineId = "ffmpeg";
    private string _conversionMode = FormatCatalog.DefaultConversionMode;
    private int _parametersVersion = 1;
    private ConversionState _state = ConversionState.Waiting;
    private double _progress;
    private string _speed = "—";
    private string _remaining = "—";
    private string _message = string.Empty;
    private string _outputPath = string.Empty;
    private string? _recoverableOutputId;
    private string? _preservedOutputPath;
    private bool _isRecoveringOutput;
    private int _selectedAudioTrack;
    private string _lastError = string.Empty;
    private string _blockedReason = string.Empty;
    private long _fileSize;
    private ConversionResultStatistics? _resultStatistics;

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? OutputGroupId { get; set; }
    public Guid? OutputConfigurationId { get; set; }
    public string OutputLabel { get; set; } = string.Empty;
    public int OutputOrder { get; set; }
    public int OutputExpectedCount { get; set; }
    /// <summary>Set only after every output and its durable history/automation receipt are complete.</summary>
    public bool OutputGroupFinalized { get; set; }
    public bool OutputHistoryRecorded { get; set; }
    public AutomationFileFingerprint? OutputFingerprint { get; set; }
    public string InputPath { get; set; } = string.Empty;
    public string? InputAccessToken { get; set; }
    public string? SourceRoot { get; set; }
    public string? SourceRootAccessToken { get; set; }
    /// <summary>
    /// Ordered v2 inputs. Legacy single-file callers may continue to use
    /// <see cref="InputPath"/> and <see cref="InputAccessToken"/>.
    /// </summary>
    public ObservableCollection<ConversionInput> Inputs { get; } = [];
    public IReadOnlyList<string> InputPaths => Inputs.Count > 0
        ? Inputs.Select(input => input.Path).ToArray()
        : string.IsNullOrWhiteSpace(InputPath) ? [] : [InputPath];
    public ContentKind ContentKind
    {
        get => _contentKind;
        set
        {
            if (SetField(ref _contentKind, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Kind)));
            }
        }
    }

    public MediaKind Kind
    {
        get => ContentKind.ToMediaKind();
        set => ContentKind = value.ToContentKind();
    }

    public ContentProbeResult ContentProbe
    {
        get => _contentProbe;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (SetField(ref _contentProbe, value))
            {
                ContentKind = value.ContentKind;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Probe)));
            }
        }
    }

    /// <summary>
    /// Compatibility projection used by the current media UI. Document-aware UI
    /// should consume <see cref="ContentProbe"/> instead.
    /// </summary>
    public MediaProbeResult Probe
    {
        get => ContentProbe as MediaProbeResult ?? new MediaProbeResult
        {
            ContentKind = ContentKind,
            FormatId = ContentProbe.FormatId,
            FileSize = ContentProbe.FileSize,
            PageCount = ContentProbe.PageCount,
            Metadata = ContentProbe.Metadata
        };
        set => ContentProbe = value;
    }
    public long FileSize { get => _fileSize; set => SetField(ref _fileSize, value); }
    public ConversionResultStatistics? ResultStatistics { get => _resultStatistics; set => SetField(ref _resultStatistics, value); }
    public ObservableCollection<string> AvailableFormats { get; } = [];
    public ObservableCollection<AudioTrackInfo> AudioTracks { get; } = [];

    public string FileName
    {
        get
        {
            if (ContentKind == ContentKind.Disc &&
                ContentProbe.Metadata.TryGetValue("displayName", out var displayName) &&
                !string.IsNullOrWhiteSpace(displayName))
            {
                return displayName;
            }

            var name = Path.GetFileName(InputPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar));
            return string.IsNullOrWhiteSpace(name) ? InputPath : name;
        }
    }
    public string InputDirectory => Path.GetDirectoryName(InputPath) ?? string.Empty;

    public string SourceFormatId
    {
        get => _sourceFormatId;
        set => SetField(ref _sourceFormatId, FormatCatalog.NormalizeFormatId(value));
    }

    public string TargetFormat
    {
        get => _targetFormat;
        set
        {
            if (string.Equals(_targetFormat, value, StringComparison.Ordinal))
            {
                return;
            }

            SetTargetFormat(value, FormatCatalog.NormalizeFormatId(value));
        }
    }

    public string TargetFormatId
    {
        get => string.IsNullOrWhiteSpace(_targetFormatId)
            ? FormatCatalog.NormalizeFormatId(_targetFormat)
            : _targetFormatId;
        set
        {
            var normalizedId = FormatCatalog.NormalizeFormatId(value);
            if (string.Equals(_targetFormatId, normalizedId, StringComparison.Ordinal))
            {
                return;
            }

            var displayName = FormatCatalog.FindById(normalizedId)?.DisplayName;
            SetTargetFormat(string.IsNullOrWhiteSpace(displayName) ? _targetFormat : displayName, normalizedId);
        }
    }

    public string EngineId
    {
        get => _engineId;
        set => SetField(ref _engineId, FormatCatalog.NormalizeEngineId(value));
    }

    public string ConversionMode
    {
        get => _conversionMode;
        set => SetField(ref _conversionMode, string.IsNullOrWhiteSpace(value)
            ? FormatCatalog.DefaultConversionMode : value.Trim().ToLowerInvariant());
    }

    public int ParametersVersion
    {
        get => _parametersVersion;
        set => SetField(ref _parametersVersion, Math.Max(1, value));
    }

    public Dictionary<string, string> Options { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ConversionState State
    {
        get => _state;
        set => SetField(ref _state, value);
    }

    public double Progress { get => _progress; set => SetField(ref _progress, value); }
    public string Speed { get => _speed; set => SetField(ref _speed, value); }
    public string Remaining { get => _remaining; set => SetField(ref _remaining, value); }
    public string Message { get => _message; set => SetField(ref _message, value); }
    public string OutputPath { get => _outputPath; set => SetField(ref _outputPath, value); }
    public string? RecoverableOutputId { get => _recoverableOutputId; set => SetField(ref _recoverableOutputId, value); }
    public string? PreservedOutputPath { get => _preservedOutputPath; set => SetField(ref _preservedOutputPath, value); }
    public bool IsRecoveringOutput { get => _isRecoveringOutput; set => SetField(ref _isRecoveringOutput, value); }
    public int SelectedAudioTrack { get => _selectedAudioTrack; set => SetField(ref _selectedAudioTrack, Math.Max(0, value)); }
    public string LastError { get => _lastError; set => SetField(ref _lastError, value); }
    public string BlockedReason { get => _blockedReason; set => SetField(ref _blockedReason, value); }
    public DateTimeOffset? LastAttemptAtUtc { get; set; }
    public ConversionState? StateBeforeBlocked { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private void SetTargetFormat(string displayName, string targetFormatId)
    {
        var engineId = ContentKind == ContentKind.Disc
            ? "disc-pipeline"
            : FormatCatalog.FindById(targetFormatId)?.DefaultEngineId;
        engineId = string.IsNullOrWhiteSpace(engineId) ? _engineId : FormatCatalog.NormalizeEngineId(engineId);
        var formatChanged = _targetFormat != displayName;
        var idChanged = _targetFormatId != targetFormatId;
        var engineChanged = _engineId != engineId;

        // Observers may immediately validate the route or capture a queue snapshot.
        // Publish only after every part of that route has been updated.
        _targetFormat = displayName;
        _targetFormatId = targetFormatId;
        _engineId = engineId;
        if (formatChanged) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TargetFormat)));
        if (idChanged) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TargetFormatId)));
        if (engineChanged) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EngineId)));
    }
}
