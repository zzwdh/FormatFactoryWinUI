using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.ViewModels;

public sealed class AudioTrackOptionViewModel
{
    public AudioTrackOptionViewModel(string displayName) => DisplayName = displayName;

    public string DisplayName { get; set; }
}

/// <summary>
/// Presentation adapter for a UI-agnostic MediaTask.
/// </summary>
public sealed class MediaTaskViewModel : INotifyPropertyChanged, IDisposable
{
    private bool _disposed;
    private bool _isTargetAvailable = true;
    private string _engineRequirement = string.Empty;
    private string? _localizedMessageOverride;
    private bool _refreshingLocalizedOptions;
    private readonly SynchronizationContext? _synchronizationContext;
    private bool _targetSelectionRefreshPending;
    private bool _outputGroupExpanded = true;
    private bool _isDetailsExpanded;
    private FailureResolutionAction _failureAction;

    public MediaTaskViewModel(MediaTask model)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _synchronizationContext = SynchronizationContext.Current;
        Model.PropertyChanged += Model_PropertyChanged;
        Model.Inputs.CollectionChanged += Inputs_CollectionChanged;
        Model.AudioTracks.CollectionChanged += AudioTracks_CollectionChanged;
        Model.AvailableFormats.CollectionChanged += AvailableFormats_CollectionChanged;
        RefreshLocalization();
    }

    public MediaTask Model { get; }
    public Guid Id => Model.Id;
    public bool OutputGroupExpanded
    {
        get => _outputGroupExpanded;
        set { if (_outputGroupExpanded == value) return; _outputGroupExpanded = value; OnPropertyChanged(); }
    }
    /// <summary>Presentation-only expansion, independent of batch selection and queue persistence.</summary>
    public bool IsDetailsExpanded
    {
        get => _isDetailsExpanded;
        set
        {
            if (_isDetailsExpanded == value) return;
            _isDetailsExpanded = value;
            OnPropertyChanged();
            RaiseDetailsAvailabilityProperties();
        }
    }
    public bool CanShowDetailsEditor => IsDetailsExpanded && CanEditConversionOptions;
    public bool CanShowReadOnlyDetails => IsDetailsExpanded && !CanEditConversionOptions;
    public string DetailsToggleLabel => LocalizationService.T("SettingsTitle");
    public string CompactParametersSummary
    {
        get
        {
            var preset = LocalizationService.ModeLabel(ConversionMode);
            var selectedTrack = SelectedAudioTrack;
            if (IsAudioTrackSelectionVisible && selectedTrack >= 0 && selectedTrack < AudioTrackOptions.Count)
                return $"{preset} · {AudioTrackOptions[selectedTrack].DisplayName}";
            return preset;
        }
    }
    public string StatusSummary
    {
        get
        {
            var metrics = StatusMetricsSummary;
            return string.IsNullOrEmpty(metrics) ? StateText : $"{StateText} · {metrics}";
        }
    }
    public string StatusMetricsSummary
    {
        get
        {
            var parts = new List<string>();
            if (State == ConversionState.Running && HasLiveMetric(Speed)) parts.Add($"{SpeedLabel} {Speed}");
            if (State is ConversionState.Running or ConversionState.Paused && HasLiveMetric(Remaining))
                parts.Add($"{RemainingLabel} {Remaining}");
            return string.Join(" · ", parts);
        }
    }
    public string DetailMessage
    {
        get
        {
            var message = Message?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(message)) return string.Empty;
            if (State is ConversionState.Failed or ConversionState.Blocked ||
                State == ConversionState.Waiting && !_isTargetAvailable && !string.IsNullOrWhiteSpace(_engineRequirement))
                return message;
            // Probe replacement can reveal or clear an HDR warning while the
            // waiting state's generic override is still in place.
            if (State == ConversionState.Waiting && _localizedMessageOverride is not null)
                message = CreateLocalizedStateMessage()?.Trim() ?? message;
            var genericKey = State switch
            {
                ConversionState.Waiting => "WaitingStart",
                ConversionState.Probing => "StateProbing",
                ConversionState.Running => "Converting",
                ConversionState.Paused => "PausedMessage",
                ConversionState.Completed => "ConversionFinished",
                ConversionState.Cancelled => "Cancelled",
                _ => null
            };
            return string.Equals(message, StateText, StringComparison.Ordinal) ||
                genericKey is not null && string.Equals(message, LocalizationService.T(genericKey), StringComparison.Ordinal)
                    ? string.Empty : message;
        }
    }
    public bool HasDetailMessage => !string.IsNullOrEmpty(DetailMessage);
    public FailureResolutionAction FailureAction => _failureAction;
    public bool HasFailureAction => FailureAction != FailureResolutionAction.None;
    public string FailureActionLabel => FailureAction == FailureResolutionAction.ChooseOutputDirectory
        ? $"{LocalizationService.T("ChooseFolder")} · {LocalizationService.T("StartRetry")}"
        : HasFailureAction ? LocalizationService.T(FailureResolutionService.LabelKey(FailureAction)) : string.Empty;
    public string OutputDirectoryOverride => Model.Options.GetValueOrDefault(OutputPathService.DirectoryOverrideKey, string.Empty);
    public bool HasOutputDirectoryOverride => !string.IsNullOrWhiteSpace(OutputDirectoryOverride);
    public string OutputDescription => string.IsNullOrWhiteSpace(Model.OutputLabel) ? string.Empty
        : $"{Model.OutputLabel} · {Model.TargetFormat}";
    public bool HasOutputDescription => !string.IsNullOrEmpty(OutputDescription);
    public bool HasConversionSettingsSummary => !string.IsNullOrEmpty(ConversionSettingsSummary);
    public string ResultSummary => Model.State == ConversionState.Completed
        ? ConversionResultSummaryFormatter.Format(Model.ResultStatistics) : string.Empty;
    public bool HasResultSummary => !string.IsNullOrEmpty(ResultSummary);
    public string ConversionSettingsSummary
    {
        get
        {
            try
            {
                var options = MediaConversionOptions.FromTask(Model);
                var parts = new List<string>();
                if (options.TargetSizeBytes is { } size)
                    parts.Add(string.Create(LocalizationService.CurrentCulture, $"≤ {size / 1048576d:0.##} MB"));
                void Add(string key, object? value)
                {
                    if (value is not null) parts.Add($"{LocalizationService.T(key)}: {Convert.ToString(value, LocalizationService.CurrentCulture)}");
                }
                Add("MediaMaxWidth", options.MaxWidth); Add("MediaMaxHeight", options.MaxHeight);
                Add("MediaFrameRate", options.FrameRate); Add("MediaVideoBitrate", options.VideoBitrateKbps);
                Add("MediaAudioBitrate", options.AudioBitrateKbps); Add("MediaSampleRate", options.SampleRate);
                Add("MediaChannels", options.Channels); Add("MediaImageQuality", options.ImageQuality);
                return string.Join(" · ", parts);
            }
            catch (InvalidDataException) { return LocalizationService.T("AutomationValidationOptions"); }
        }
    }
    public string CustomSettingsTooltip => LocalizationService.T("MediaCustomSettings");
    public string MultipleOutputsTooltip => LocalizationService.T("MultiOutputSettings");
    public string SaveOutputGroupTooltip => LocalizationService.T("MultiOutputSavePreset");
    public string PrioritizeTooltip => LocalizationService.T("QueuePriorityAction");
    public string PrioritizeHint => LocalizationService.T("QueuePriorityHint");
    public string RelocateSourceTooltip => LocalizationService.T("SourceRelocationAction");
    public string RelocateSourceFolderTooltip => LocalizationService.T("SourceRelocationFolderAction");
    public bool CanPrioritizeTask => Model.State == ConversionState.Waiting && !Model.IsRecoveringOutput;
    public bool CanRelocateSource => !Model.IsRecoveringOutput &&
        Model.State is ConversionState.Waiting or ConversionState.Blocked or ConversionState.Failed or ConversionState.Cancelled &&
        SourceRelocationService.IsManualTask(Model) && SourceRelocationService.HasMissingInputs(Model);

    public void RefreshQueueActions()
    {
        RaiseFailureActionProperties();
        OnPropertyChanged(nameof(PrioritizeTooltip));
        OnPropertyChanged(nameof(PrioritizeHint));
        OnPropertyChanged(nameof(RelocateSourceTooltip));
        OnPropertyChanged(nameof(RelocateSourceFolderTooltip));
        OnPropertyChanged(nameof(CanPrioritizeTask));
        OnPropertyChanged(nameof(CanRelocateSource));
    }
    public string InputPath => Model.InputPath;
    public MediaKind Kind => Model.Kind;
    public MediaProbeResult Probe => Model.Probe;
    public ObservableCollection<string> AvailableFormats => Model.AvailableFormats;
    public ObservableCollection<LocalizedOption> ModeOptions { get; } = [];
    public ObservableCollection<AudioTrackOptionViewModel> AudioTrackOptions { get; } = [];

    public string FileName => Model.InputPaths.Count > 1
        ? LocalizationService.T("ImageCollectionName", Path.GetFileName(Model.InputPath), Model.InputPaths.Count - 1)
        : Model.FileName;
    public string KindText => Model.Kind switch
    {
        MediaKind.Video => LocalizationService.T("KindVideo"),
        MediaKind.Audio => LocalizationService.T("KindAudio"),
        MediaKind.Image => LocalizationService.T("KindImage"),
        MediaKind.Document => LocalizationService.T("KindDocument"),
        MediaKind.Ebook => LocalizationService.T("KindEbook"),
        MediaKind.Disc => LocalizationService.T("KindDisc"),
        _ => LocalizationService.T("Unknown")
    };
    public string FileSizeText => Model.Kind == MediaKind.Disc && Model.FileSize <= 0
        ? LocalizationService.T("DiscCapacityUnknown")
        : Model.FileSize switch
    {
        >= 1_073_741_824 => string.Create(LocalizationService.CurrentCulture, $"{Model.FileSize / 1_073_741_824d:F2} GB"),
        >= 1_048_576 => string.Create(LocalizationService.CurrentCulture, $"{Model.FileSize / 1_048_576d:F1} MB"),
        >= 1024 => string.Create(LocalizationService.CurrentCulture, $"{Model.FileSize / 1024d:F1} KB"),
        _ => string.Create(LocalizationService.CurrentCulture, $"{Model.FileSize} B")
    };
    public string MediaInfo => Model.Kind switch
    {
        MediaKind.Video => $"{Model.Probe.Width}×{Model.Probe.Height} · {FormatDuration(Model.Probe.Duration)}{FormatBitrate(Model.Probe.Bitrate)}{(Model.Probe.IsHdr ? " · HDR" : string.Empty)}",
        MediaKind.Audio => $"{FormatDuration(Model.Probe.Duration)}{FormatBitrate(Model.Probe.Bitrate)} · {Model.Probe.AudioTracks.FirstOrDefault()?.Codec.ToUpperInvariant() ?? LocalizationService.T("UnknownCodec")}",
        MediaKind.Image when Model.InputPaths.Count > 1 => LocalizationService.T("ImageCollectionCount", Model.InputPaths.Count),
        MediaKind.Image => $"{Model.Probe.Width}×{Model.Probe.Height}{(Model.Probe.IsAnimated ? $" · {LocalizationService.T("Animated")}" : string.Empty)}",
        MediaKind.Document or MediaKind.Ebook => Model.ContentProbe.PageCount > 0
            ? LocalizationService.T("PageCount", Model.ContentProbe.PageCount)
            : FormatCatalog.FindById(Model.SourceFormatId)?.DisplayName ?? LocalizationService.T("Unknown"),
        MediaKind.Disc => Model.ContentProbe.Metadata.TryGetValue("mediaType", out var mediaType)
            ? mediaType
            : FormatCatalog.FindById(Model.SourceFormatId)?.DisplayName ?? Model.SourceFormatId.ToUpperInvariant(),
        _ => LocalizationService.T("Unknown")
    };

    public bool CanEditConversionOptions =>
        !Model.IsRecoveringOutput && Model.State is not (ConversionState.Running or ConversionState.Paused or ConversionState.Probing);

    public string TargetFormat
    {
        get => Model.TargetFormat;
        set
        {
            // A selector can temporarily clear its selection while rows or items
            // are rebound. That is not a request to erase the conversion route.
            if (CanEditConversionOptions && !string.IsNullOrWhiteSpace(value)) Model.TargetFormat = value;
        }
    }
    public string ConversionMode
    {
        get => Model.ConversionMode;
        set
        {
            if (CanEditConversionOptions && !_refreshingLocalizedOptions)
            {
                Model.ConversionMode = value;
            }
        }
    }
    public ConversionState State { get => Model.State; set => Model.State = value; }

    public string StateText => Model.State switch
    {
        ConversionState.Waiting => LocalizationService.T("StateWaiting"),
        ConversionState.Probing => LocalizationService.T("StateProbing"),
        ConversionState.Running => LocalizationService.T("StateRunning"),
        ConversionState.Paused => LocalizationService.T("StatePaused"),
        ConversionState.Completed => LocalizationService.T("StateCompleted"),
        ConversionState.Failed => LocalizationService.T("StateFailed"),
        ConversionState.Cancelled => LocalizationService.T("StateCancelled"),
        ConversionState.Blocked => LocalizationService.T("ConversionUiStateBlocked"),
        _ => LocalizationService.T("StateUnknown")
    };

    public double Progress { get => Model.Progress; set => Model.Progress = value; }
    public string Speed { get => Model.Speed; set => Model.Speed = value; }
    public string Remaining { get => Model.Remaining; set => Model.Remaining = value; }
    public string Message
    {
        get => Model.State == ConversionState.Waiting &&
               !_isTargetAvailable &&
               !string.IsNullOrWhiteSpace(_engineRequirement)
            ? _engineRequirement
            : _localizedMessageOverride ?? Model.Message;
        set
        {
            var hadOverride = _localizedMessageOverride is not null;
            _localizedMessageOverride = null;
            Model.Message = value;
            if (hadOverride)
            {
                OnPropertyChanged();
                RaiseDetailMessageProperties();
            }
        }
    }
    public string OutputPath { get => Model.OutputPath; set => Model.OutputPath = value; }
    public int SelectedAudioTrack
    {
        // Restoring a probe rebuilds options one track at a time. Expose no UI
        // selection until the retained index exists, while keeping the model choice.
        get => Model.SelectedAudioTrack >= 0 && Model.SelectedAudioTrack < AudioTrackOptions.Count
            ? Model.SelectedAudioTrack
            : -1;
        set
        {
            // ComboBox clears SelectedIndex while rebuilding or recycling its items.
            if (value >= 0 && CanEditConversionOptions && !_refreshingLocalizedOptions)
            {
                Model.SelectedAudioTrack = value;
            }
        }
    }
    public bool UseCompatibleAudioTrack
    {
        get => CompatibleAudioTrackPolicy.IsRequested(Model);
        set
        {
            if (!CanEditConversionOptions || value == UseCompatibleAudioTrack) return;
            // An existing choice must remain reversible after its source or mode changes.
            if (value && (Model.ConversionMode == "lossless" ||
                          !CompatibleAudioTrackPolicy.CanSelect(Model, Model.Probe))) return;
            if (value)
                Model.Options[CompatibleAudioTrackPolicy.OptionKey] = CompatibleAudioTrackPolicy.CompatibleValue;
            else
                Model.Options.Remove(CompatibleAudioTrackPolicy.OptionKey);
            OnPropertyChanged();
            RaiseCompatibleAudioTrackProperties();
            OnPropertyChanged(nameof(CompactParametersSummary));
        }
    }

    public bool IsCompatibleAudioTrackVisible => UseCompatibleAudioTrack ||
        (CompatibleAudioTrackPolicy.HasCompatibleTracks(Model.Probe) &&
         CompatibleAudioTrackPolicy.IsSupportedOutput(Model));
    public bool ShowCompatibilityWarning => UseCompatibleAudioTrack ||
        (CanShowDetailsEditor && IsCompatibleAudioTrackVisible);
    public bool CanUseCompatibleAudioTrack => CanEditConversionOptions &&
        (UseCompatibleAudioTrack || (Model.ConversionMode != "lossless" &&
                                   CompatibleAudioTrackPolicy.CanSelect(Model, Model.Probe)));
    public string CompatibleAudioTrackHeader => LocalizationService.T("MediaCompatibilityAacOption");
    public string CompatibleAudioTrackWarning => LocalizationService.T("MediaCompatibilityAacWarning");
    public string CompatibleAudioTrackTooltip => Model.ConversionMode == "lossless"
        ? LocalizationService.T("MediaCompatibilityAacLossless")
        : CompatibleAudioTrackWarning;

    public bool IsTargetAvailable => _isTargetAvailable;
    public string EngineRequirement => _engineRequirement;

    public bool IsAudioTrackSelectionVisible =>
        Model.Kind == MediaKind.Video &&
        FormatCatalog.AudioFormats.Contains(Model.TargetFormat, StringComparer.OrdinalIgnoreCase);
    public bool IsPdfPasswordVisible =>
        string.Equals(Model.SourceFormatId, "pdf", StringComparison.OrdinalIgnoreCase);
    public bool IsPdfDocxNoticeVisible => IsPdfPasswordVisible && Model.TargetFormatId == "docx";
    public string PdfDocxNotice => LocalizationService.T("PdfDocxNotice");

    public string MediaInfoTooltip => LocalizationService.T("MediaInfoButton");
    public string SavePresetTooltip => LocalizationService.T("AutomationSaveTaskPreset");
    public string FormatHeader => LocalizationService.T("FormatHeader");
    public string PresetHeader => LocalizationService.T("PresetHeader");
    public string AudioTrackHeader => LocalizationService.T("AudioTrackHeader");
    public string StartRetryTooltip => _isTargetAvailable
        ? LocalizationService.T("StartRetry")
        : _engineRequirement;
    public string PauseResumeTooltip => LocalizationService.T("PauseResume");
    public string OpenOutputTooltip => LocalizationService.T("OpenOutput");
    public string ShareOutputTooltip => LocalizationService.T("ShareOutput");
    public string PreviewTooltip => LocalizationService.T("DesktopPreviewTitle");
    public string OpenFileTooltip => LocalizationService.T("DesktopOpenDefault");
    public string CopyOutputTooltip => LocalizationService.T("DesktopCopyOutput");
    public string ImageColorTooltip => LocalizationService.T("NativeImageColorTitle");
    public bool CanEditImageColor => CanEditConversionOptions && Kind == MediaKind.Image &&
        ((Model.TargetFormatId is "png" or "jpg" or "tiff" && !Model.Probe.IsAnimated) ||
         string.Equals(Model.Options.GetValueOrDefault("imageColorPolicy"), "preserve", StringComparison.OrdinalIgnoreCase));
    public bool IsOutputReady => State == ConversionState.Completed && !string.IsNullOrWhiteSpace(OutputPath);
    public bool CanRecoverOutput => !Model.IsRecoveringOutput && (Model.State is ConversionState.Failed or ConversionState.Blocked) && !string.IsNullOrWhiteSpace(Model.RecoverableOutputId);
    public string RecoverOutputTooltip => LocalizationService.T("OutputRecoverySaveAs");
    public string CopyStatusErrorTooltip => LocalizationService.T("CopyStatusError");
    public string CancelRemoveTooltip => LocalizationService.T("CancelRemove");
    public string MoreActionsTooltip => LocalizationService.T("ConversionUiLayoutMoreActions");
    public string SpeedLabel => LocalizationService.T("Speed");
    public string RemainingLabel => LocalizationService.T("Remaining");
    public string PdfPasswordHeader => LocalizationService.T("PdfPasswordHeader");

    public void RefreshLocalization()
    {
        _localizedMessageOverride = CreateLocalizedStateMessage();
        _refreshingLocalizedOptions = true;
        try
        {
            ModeOptions.Clear();
            foreach (var mode in FormatCatalog.ConversionModes)
            {
                ModeOptions.Add(new LocalizedOption(mode, LocalizationService.ModeLabel(mode)));
            }

            RefreshAudioTrackOptions();

            OnPropertyChanged(nameof(ConversionMode));
            OnPropertyChanged(nameof(SelectedAudioTrack));
        }
        finally
        {
            _refreshingLocalizedOptions = false;
        }

        RaisePresentationProperties();
    }

    public void SetEngineAvailability(bool isAvailable, string? requirement = null)
    {
        var normalizedRequirement = isAvailable ? string.Empty : requirement?.Trim() ?? string.Empty;
        if (_isTargetAvailable == isAvailable &&
            string.Equals(_engineRequirement, normalizedRequirement, StringComparison.Ordinal))
        {
            return;
        }

        _isTargetAvailable = isAvailable;
        _engineRequirement = normalizedRequirement;
        OnPropertyChanged(nameof(IsTargetAvailable));
        OnPropertyChanged(nameof(EngineRequirement));
        OnPropertyChanged(nameof(StartRetryTooltip));
        OnPropertyChanged(nameof(Message));
        RaiseDetailMessageProperties();
        RaiseFailureActionProperties();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Model.PropertyChanged -= Model_PropertyChanged;
        Model.Inputs.CollectionChanged -= Inputs_CollectionChanged;
        Model.AudioTracks.CollectionChanged -= AudioTracks_CollectionChanged;
        Model.AvailableFormats.CollectionChanged -= AvailableFormats_CollectionChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Inputs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        RaiseFailureActionProperties();
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(MediaInfo));
    }

    private void AudioTracks_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        RefreshAudioTrackOptions();
        OnPropertyChanged(nameof(MediaInfo));
        RaiseCompatibleAudioTrackProperties();
        OnPropertyChanged(nameof(CompactParametersSummary));
    }

    private void AvailableFormats_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        // Wait until the selector has processed the collection event before
        // reapplying its retained selection. A synchronous refresh sees stale items.
        if (args.NewItems?.Contains(Model.TargetFormat) != true ||
            _targetSelectionRefreshPending || _synchronizationContext is null) return;
        _targetSelectionRefreshPending = true;
        _synchronizationContext.Post(_ =>
        {
            _targetSelectionRefreshPending = false;
            if (!_disposed && Model.AvailableFormats.Contains(Model.TargetFormat))
                OnPropertyChanged(nameof(TargetFormat));
        }, null);
    }

    private void RefreshAudioTrackOptions()
    {
        var wasRefreshing = _refreshingLocalizedOptions;
        _refreshingLocalizedOptions = true;
        try
        {
            AudioTrackOptions.Clear();
            foreach (var track in Model.AudioTracks)
                AudioTrackOptions.Add(new AudioTrackOptionViewModel(LocalizationService.T("AudioTrack",
                    track.RelativeIndex + 1, track.Language, track.Codec.ToUpperInvariant(), track.Channels)));
            OnPropertyChanged(nameof(SelectedAudioTrack));
        }
        finally { _refreshingLocalizedOptions = wasRefreshing; }
    }

    private void Model_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MediaTask.Message))
        {
            _localizedMessageOverride = null;
        }
        else if (e.PropertyName == nameof(MediaTask.State))
        {
            _localizedMessageOverride = CreateLocalizedStateMessage();
        }

        OnPropertyChanged(e.PropertyName);
        RaiseCardModelProperties(e.PropertyName);
        if (e.PropertyName is nameof(MediaTask.SourceFormatId) or nameof(MediaTask.TargetFormatId) or nameof(MediaTask.TargetFormat))
            OnPropertyChanged(nameof(IsPdfDocxNoticeVisible));
        if (e.PropertyName is nameof(MediaTask.TargetFormat) or nameof(MediaTask.TargetFormatId))
        {
            OnPropertyChanged(nameof(OutputDescription));
            OnPropertyChanged(nameof(HasOutputDescription));
        }
        if (e.PropertyName is nameof(MediaTask.Probe) or nameof(MediaTask.ContentProbe) or nameof(MediaTask.Kind) or nameof(MediaTask.SourceFormatId))
        {
            OnPropertyChanged(nameof(MediaInfo));
            OnPropertyChanged(nameof(FileName));
            OnPropertyChanged(nameof(KindText));
            OnPropertyChanged(nameof(IsAudioTrackSelectionVisible));
            OnPropertyChanged(nameof(IsPdfPasswordVisible));
        }
        if (e.PropertyName is nameof(MediaTask.Probe) or nameof(MediaTask.ContentProbe) or
            nameof(MediaTask.Kind) or nameof(MediaTask.SourceFormatId) or nameof(MediaTask.TargetFormat) or
            nameof(MediaTask.TargetFormatId) or nameof(MediaTask.ConversionMode) or
            nameof(MediaTask.SelectedAudioTrack) or nameof(MediaTask.State) or nameof(MediaTask.IsRecoveringOutput))
            RaiseCompatibleAudioTrackProperties();
        if (e.PropertyName == nameof(MediaTask.FileSize)) OnPropertyChanged(nameof(FileSizeText));
        if (e.PropertyName is nameof(MediaTask.ResultStatistics) or nameof(MediaTask.State))
        {
            OnPropertyChanged(nameof(ResultSummary));
            OnPropertyChanged(nameof(HasResultSummary));
        }
        if (e.PropertyName is nameof(MediaTask.State) or nameof(MediaTask.IsRecoveringOutput)) RefreshQueueActions();
        if (e.PropertyName is nameof(MediaTask.State) or nameof(MediaTask.OutputPath))
            OnPropertyChanged(nameof(IsOutputReady));
        if (e.PropertyName is nameof(MediaTask.State) or nameof(MediaTask.RecoverableOutputId))
            OnPropertyChanged(nameof(CanRecoverOutput));
        if (e.PropertyName == nameof(MediaTask.IsRecoveringOutput))
        {
            OnPropertyChanged(nameof(CanRecoverOutput));
            OnPropertyChanged(nameof(CanEditConversionOptions));
        }
        if (e.PropertyName is nameof(MediaTask.TargetFormatId) or nameof(MediaTask.Probe) or nameof(MediaTask.Kind))
            OnPropertyChanged(nameof(CanEditImageColor));
        if (e.PropertyName == nameof(MediaTask.TargetFormat))
        {
            OnPropertyChanged(nameof(IsAudioTrackSelectionVisible));
            OnPropertyChanged(nameof(CanEditImageColor));
        }
        else if (e.PropertyName == nameof(MediaTask.State))
        {
            OnPropertyChanged(nameof(CanEditConversionOptions));
            OnPropertyChanged(nameof(CanEditImageColor));
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(Message));
        }
    }

    private void RaiseCompatibleAudioTrackProperties()
    {
        OnPropertyChanged(nameof(IsCompatibleAudioTrackVisible));
        OnPropertyChanged(nameof(ShowCompatibilityWarning));
        OnPropertyChanged(nameof(CanUseCompatibleAudioTrack));
        OnPropertyChanged(nameof(CompatibleAudioTrackHeader));
        OnPropertyChanged(nameof(CompatibleAudioTrackWarning));
        OnPropertyChanged(nameof(CompatibleAudioTrackTooltip));
    }

    private void RaiseCardModelProperties(string? propertyName)
    {
        if (propertyName is nameof(MediaTask.State) or nameof(MediaTask.Message) or nameof(MediaTask.LastError) or
            nameof(MediaTask.BlockedReason) or nameof(MediaTask.RecoverableOutputId) or nameof(MediaTask.IsRecoveringOutput) or
            nameof(MediaTask.InputPath) or nameof(MediaTask.ParametersVersion)) RaiseFailureActionProperties();
        if (propertyName is nameof(MediaTask.TargetFormat) or nameof(MediaTask.TargetFormatId) or
            nameof(MediaTask.ConversionMode) or nameof(MediaTask.SelectedAudioTrack) or
            nameof(MediaTask.Probe) or nameof(MediaTask.ContentProbe) or nameof(MediaTask.Kind) or
            nameof(MediaTask.SourceFormatId) or nameof(MediaTask.ParametersVersion))
            OnPropertyChanged(nameof(CompactParametersSummary));
        if (propertyName is nameof(MediaTask.State) or nameof(MediaTask.IsRecoveringOutput))
            RaiseDetailsAvailabilityProperties();
        if (propertyName is nameof(MediaTask.State) or nameof(MediaTask.Speed) or nameof(MediaTask.Remaining))
        {
            OnPropertyChanged(nameof(StatusSummary));
            OnPropertyChanged(nameof(StatusMetricsSummary));
        }
        if (propertyName is nameof(MediaTask.State) or nameof(MediaTask.Message)) RaiseDetailMessageProperties();
        if (propertyName is nameof(MediaTask.Probe) or nameof(MediaTask.ContentProbe)) RaiseDetailMessageProperties();
        if (propertyName == nameof(MediaTask.ParametersVersion))
        {
            OnPropertyChanged(nameof(ConversionSettingsSummary));
            OnPropertyChanged(nameof(HasConversionSettingsSummary));
        }
    }

    private void RaiseDetailsAvailabilityProperties()
    {
        OnPropertyChanged(nameof(CanShowDetailsEditor));
        OnPropertyChanged(nameof(CanShowReadOnlyDetails));
        OnPropertyChanged(nameof(ShowCompatibilityWarning));
    }

    private void RaiseDetailMessageProperties()
    {
        OnPropertyChanged(nameof(DetailMessage));
        OnPropertyChanged(nameof(HasDetailMessage));
    }

    private void RaiseFailureActionProperties()
    {
        var action = FailureResolutionService.ForTask(Model);
        _failureAction = action == FailureResolutionAction.None && !IsTargetAvailable &&
            Model.State is ConversionState.Failed or ConversionState.Blocked && !Model.IsRecoveringOutput
                ? FailureResolutionAction.OpenEngineSettings : action;
        OnPropertyChanged(nameof(FailureAction));
        OnPropertyChanged(nameof(HasFailureAction));
        OnPropertyChanged(nameof(FailureActionLabel));
        OnPropertyChanged(nameof(OutputDirectoryOverride));
        OnPropertyChanged(nameof(HasOutputDirectoryOverride));
    }

    private static bool HasLiveMetric(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim() is not ("—" or "–" or "-");

    private void RaisePresentationProperties()
    {
        OnPropertyChanged(nameof(PdfDocxNotice));
        OnPropertyChanged(nameof(IsPdfDocxNoticeVisible));
        RaiseFailureActionProperties();
        OnPropertyChanged(nameof(CompactParametersSummary));
        OnPropertyChanged(nameof(StatusSummary));
        OnPropertyChanged(nameof(StatusMetricsSummary));
        OnPropertyChanged(nameof(DetailsToggleLabel));
        RaiseDetailsAvailabilityProperties();
        RaiseDetailMessageProperties();
        RefreshQueueActions();
        OnPropertyChanged(nameof(OutputDescription));
        OnPropertyChanged(nameof(HasOutputDescription));
        OnPropertyChanged(nameof(ConversionSettingsSummary));
        OnPropertyChanged(nameof(HasConversionSettingsSummary));
        OnPropertyChanged(nameof(ResultSummary));
        OnPropertyChanged(nameof(HasResultSummary));
        OnPropertyChanged(nameof(CustomSettingsTooltip));
        OnPropertyChanged(nameof(MultipleOutputsTooltip));
        OnPropertyChanged(nameof(SaveOutputGroupTooltip));
        RaiseCompatibleAudioTrackProperties();
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(KindText));
        OnPropertyChanged(nameof(MediaInfo));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(MediaInfoTooltip));
        OnPropertyChanged(nameof(SavePresetTooltip));
        OnPropertyChanged(nameof(FormatHeader));
        OnPropertyChanged(nameof(PresetHeader));
        OnPropertyChanged(nameof(AudioTrackHeader));
        OnPropertyChanged(nameof(StartRetryTooltip));
        OnPropertyChanged(nameof(PauseResumeTooltip));
        OnPropertyChanged(nameof(OpenOutputTooltip));
        OnPropertyChanged(nameof(ShareOutputTooltip));
        OnPropertyChanged(nameof(RecoverOutputTooltip));
        OnPropertyChanged(nameof(CanRecoverOutput));
        OnPropertyChanged(nameof(PreviewTooltip));
        OnPropertyChanged(nameof(OpenFileTooltip));
        OnPropertyChanged(nameof(CopyOutputTooltip));
        OnPropertyChanged(nameof(ImageColorTooltip));
        OnPropertyChanged(nameof(CopyStatusErrorTooltip));
        OnPropertyChanged(nameof(CancelRemoveTooltip));
        OnPropertyChanged(nameof(MoreActionsTooltip));
        OnPropertyChanged(nameof(SpeedLabel));
        OnPropertyChanged(nameof(RemainingLabel));
        OnPropertyChanged(nameof(PdfPasswordHeader));
        OnPropertyChanged(nameof(Message));
    }

    private string? CreateLocalizedStateMessage() => Model.State switch
    {
        ConversionState.Waiting => Model.Probe.IsHdr
            ? LocalizationService.T("HdrAuto")
            : LocalizationService.T("WaitingStart"),
        ConversionState.Probing => LocalizationService.T("StateProbing"),
        ConversionState.Running => LocalizationService.T("Converting"),
        ConversionState.Paused => LocalizationService.T("PausedMessage"),
        ConversionState.Completed => LocalizationService.T("ConversionFinished"),
        ConversionState.Cancelled => LocalizationService.T("Cancelled"),
        ConversionState.Failed => null,
        _ => null
    };

    private static string FormatBitrate(uint bitrate) => bitrate == 0 ? string.Empty : $" · {bitrate / 1000} kbps";

    private static string FormatDuration(TimeSpan duration) => duration <= TimeSpan.Zero
        ? LocalizationService.T("Static")
        : duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
