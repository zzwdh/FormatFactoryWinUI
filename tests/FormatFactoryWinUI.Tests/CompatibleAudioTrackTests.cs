using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.ViewModels;

namespace FormatFactoryWinUI.Tests;

[Collection(nameof(LocalizationServiceCollection))]
public sealed class CompatibleAudioTrackTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("true")]
    [InlineData("automatic")]
    public void DefaultAndUnrecognizedOptions_KeepEveryVideoAudioTrack(string? value)
    {
        var task = CreateTask();
        task.SelectedAudioTrack = 2;
        if (value is not null) task.Options[CompatibleAudioTrackPolicy.OptionKey] = value;

        Assert.False(CompatibleAudioTrackPolicy.IsRequested(task));
        Assert.Equal(new[] { 3, 7, 9 }, CompatibleAudioTrackPolicy.GetSelectedAudioTracks(task, task.Probe)
            .Select(track => track.StreamIndex));
        Assert.Equal(new[] { "0:0", "0:3", "0:7", "0:9" },
            MediaInputPreflightService.RequiredStreams(task, task.Probe).Select(stream => stream.Map));
        Assert.Contains("0:a?", Maps(new PresetService().BuildArguments(task, "output.mp4", EncoderBackend.Cpu)));
    }

    [Theory]
    [InlineData("apple_apac", "aac", 2, true)]
    [InlineData("apac", "aac", 2, false)]
    [InlineData("", "aac", 2, false)]
    [InlineData("apple_apac", "aac", 6, false)]
    [InlineData("apple_apac", "aac", 1, false)]
    [InlineData("apple_apac", "aac", 0, false)]
    [InlineData("apple_apac", "ac3", 2, false)]
    public void Availability_RequiresExactApacCodecAndStereoAac(string codec, string alternative, int channels, bool expected)
    {
        var probe = CreateTask().Probe;
        probe.AudioTracks =
        [
            new() { Codec = codec, CodecTag = "apac", StreamIndex = 3, Channels = 6 },
            new() { Codec = alternative, StreamIndex = 7, Channels = channels }
        ];
        Assert.Equal(expected, CompatibleAudioTrackPolicy.HasCompatibleTracks(probe));
    }

    [Theory]
    [InlineData(MediaKind.Video, "mp4")]
    [InlineData(MediaKind.Video, "mkv")]
    [InlineData(MediaKind.Video, "mp3")]
    [InlineData(MediaKind.Audio, "mp3")]
    [InlineData(MediaKind.Audio, "aac")]
    public void ExplicitChoice_UsesFirstStereoAacInsteadOfExistingSelection(MediaKind sourceKind, string target)
    {
        var task = CreateTask(target, sourceKind);
        task.SelectedAudioTrack = 2;
        Enable(task);
        Assert.True(CompatibleAudioTrackPolicy.IsRequested(task));
        Assert.True(CompatibleAudioTrackPolicy.IsSupportedOutput(task));
        Assert.True(CompatibleAudioTrackPolicy.CanSelect(task, task.Probe));
        Assert.Null(CompatibleAudioTrackPolicy.GetValidationError(task, task.Probe));
        var selected = Assert.Single(CompatibleAudioTrackPolicy.GetSelectedAudioTracks(task, task.Probe));
        Assert.Equal(7, selected.StreamIndex);
        Assert.Equal("en", selected.Language);
    }

    [Theory]
    [InlineData("png")]
    [InlineData("gif")]
    [InlineData("frames")]
    public void ImagesAndFrames_IgnoreInactiveAudioPolicy(string target)
    {
        var task = CreateTask(target);
        Enable(task);
        Assert.False(CompatibleAudioTrackPolicy.IsSupportedOutput(task));
        Assert.False(CompatibleAudioTrackPolicy.CanSelect(task, task.Probe));
        Assert.Null(CompatibleAudioTrackPolicy.GetValidationError(task, task.Probe));
        Assert.Equal(new[] { "0:0" },
            MediaInputPreflightService.RequiredStreams(task, task.Probe).Select(stream => stream.Map));
        var arguments = new PresetService().BuildArguments(task, "output.tmp", EncoderBackend.Cpu);
        Assert.DoesNotContain(Maps(arguments), map => map is "0:3" or "0:7" or "0:9" or "0:a?");
    }

    [Theory]
    [InlineData("mp4")]
    [InlineData("mp3")]
    public void LosslessChoice_IsRetainedButRejectedBeforeDroppingAnyTrack(string target)
    {
        var task = CreateTask(target);
        Enable(task);
        task.ConversionMode = "lossless";
        Assert.True(CompatibleAudioTrackPolicy.IsRequested(task));
        Assert.False(CompatibleAudioTrackPolicy.CanSelect(task, task.Probe));
        Assert.False(string.IsNullOrWhiteSpace(CompatibleAudioTrackPolicy.GetValidationError(task, task.Probe)));
        Assert.Throws<NotSupportedException>(() =>
            new PresetService().BuildArguments(task, "output.tmp", EncoderBackend.Cpu));
        Assert.True(CompatibleAudioTrackPolicy.IsRequested(task));
        Assert.Equal(3, task.Probe.AudioTracks.Count);
    }

    [Theory]
    [InlineData("mp4")]
    [InlineData("mp3")]
    public async Task Preflight_DefaultStillRejectsUnavailableApacDecoder(string target)
    {
        var task = CreateTask(target);
        var fixture = new PreflightFixture(task.Probe);
        var result = await fixture.Service.SelectAsync(fixture.Primary, null, task);
        Assert.False(result.Success);
        Assert.Equal(MediaInputFailureKind.UnsupportedDecoder, result.FailureKind);
        Assert.False(fixture.Decoded);
        Assert.False(fixture.FallbackValidated);
    }

    [Theory]
    [InlineData(MediaKind.Video, "mp4", new[] { "0:0", "0:7" })]
    [InlineData(MediaKind.Video, "mp3", new[] { "0:7" })]
    [InlineData(MediaKind.Audio, "mp3", new[] { "0:7" })]
    public async Task Preflight_OnlyChecksAndDecodesTheExplicitCompatibleTrack(MediaKind sourceKind, string target, string[] expectedMaps)
    {
        var task = CreateTask(target, sourceKind);
        Enable(task);
        var fixture = new PreflightFixture(task.Probe);
        var result = await fixture.Service.SelectAsync(fixture.Primary, fixture.Fallback, task);
        Assert.True(result.Success, result.Detail);
        Assert.False(result.UsedFallback);
        Assert.True(fixture.Decoded);
        Assert.False(fixture.FallbackValidated);
        Assert.Equal(expectedMaps, fixture.DecodedMaps);
    }

    [Theory]
    [InlineData("no-aac")]
    [InlineData("no-apac")]
    [InlineData("surround-aac")]
    [InlineData("lossless")]
    public async Task InvalidOrNoLongerAvailableChoice_FailsBeforeDecodeOrFallback(string change)
    {
        var task = CreateTask();
        Enable(task);
        var actual = CreateTask().Probe;
        if (change == "no-aac") actual.AudioTracks = [actual.AudioTracks[0]];
        else if (change == "no-apac") actual.AudioTracks = [actual.AudioTracks[1]];
        else if (change == "surround-aac")
            actual.AudioTracks = [actual.AudioTracks[0], new() { Codec = "aac", Channels = 6, StreamIndex = 7 }];
        else task.ConversionMode = "lossless";
        var fixture = new PreflightFixture(actual);
        var result = await fixture.Service.SelectAsync(fixture.Primary, fixture.Fallback, task);
        Assert.False(result.Success);
        Assert.Equal(MediaInputFailureKind.AudioSelectionUnavailable, result.FailureKind);
        Assert.False(fixture.Decoded);
        Assert.False(fixture.FallbackValidated);
        Assert.True(CompatibleAudioTrackPolicy.IsRequested(task));
    }

    [Fact]
    public async Task Preflight_ReprobeResolvesCurrentStereoAacStreamWithoutReusingOldIndex()
    {
        var task = CreateTask();
        Enable(task);
        var actual = CreateTask().Probe;
        actual.AudioTracks =
        [
            new() { Codec = "aac", Channels = 2, StreamIndex = 13, RelativeIndex = 0 },
            new() { Codec = "apple_apac", Channels = 6, StreamIndex = 17, RelativeIndex = 1 }
        ];
        var fixture = new PreflightFixture(actual);
        var result = await fixture.Service.SelectAsync(fixture.Primary, null, task);
        Assert.True(result.Success, result.Detail);
        Assert.Same(actual, result.Probe);
        Assert.Equal(new[] { "0:0", "0:13" }, fixture.DecodedMaps);
        Assert.Equal(7, task.Probe.AudioTracks[1].StreamIndex);
    }

    [Theory]
    [InlineData("MP4", EncoderBackend.Cpu, false)]
    [InlineData("MKV", EncoderBackend.Cpu, false)]
    [InlineData("WEBM", EncoderBackend.Cpu, false)]
    [InlineData("VP9", EncoderBackend.Cpu, false)]
    [InlineData("AV1", EncoderBackend.Cpu, false)]
    [InlineData("MP4", EncoderBackend.Nvidia, true)]
    [InlineData("MP4", EncoderBackend.Intel, true)]
    [InlineData("MP4", EncoderBackend.Amd, false)]
    [InlineData("MP4", EncoderBackend.MediaFoundation, true)]
    [InlineData("MP3", EncoderBackend.Cpu, false)]
    [InlineData("OPUS", EncoderBackend.Cpu, false)]
    public void FinalArguments_MapOnlyTheExplicitStereoAac(string target, EncoderBackend backend, bool hardwareDecode)
    {
        var task = CreateTask(target);
        task.SelectedAudioTrack = 2;
        Enable(task);
        var arguments = new PresetService().BuildArguments(task, "output.tmp", backend, hardwareDecode);
        var maps = Maps(arguments);
        Assert.Contains("0:7", maps);
        Assert.DoesNotContain("0:a?", maps);
        Assert.DoesNotContain("0:3", maps);
        Assert.DoesNotContain("0:9", maps);
        Assert.DoesNotContain("0:a:0", maps);
        Assert.DoesNotContain("0:a:2", maps);
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("-filter:a:", StringComparison.Ordinal));
        if (!FormatCatalog.IsAudioFormat(task.TargetFormat)) Assert.Contains("0:V:0", maps);
    }

    [Fact]
    public void NonApacVideoWithoutChoice_RetainsAllAudioMappingsAndOriginalOpusFilters()
    {
        var task = CreateTask("WEBM");
        task.Probe.AudioTracks =
        [
            new() { Codec = "aac", Channels = 2, StreamIndex = 3 },
            new() { Codec = "ac3", Channels = 6, ChannelLayout = "5.1(side)", StreamIndex = 7 }
        ];
        Assert.False(CompatibleAudioTrackPolicy.HasCompatibleTracks(task.Probe));
        var arguments = new PresetService().BuildArguments(task, "output.webm", EncoderBackend.Cpu);
        Assert.Contains("0:a?", Maps(arguments));
        Assert.Contains("-filter:a:1", arguments);
        Assert.DoesNotContain("-filter:a:0", arguments);
    }

    [Fact]
    public void CompatibleIntermediate_MapsSelectedStereoAacAndValidatesOnePcmTrack()
    {
        var task = CreateTask();
        Enable(task);
        var arguments = MediaCompatibilityDecodeService.BuildArguments(task, "decoded.mkv");
        Assert.Equal(new[] { "0:0", "0:7", "0:s?", "0:t?" }, Maps(arguments));
        Assert.Contains("pcm_s32le", arguments);
        var intermediate = Intermediate();
        MediaCompatibilityDecodeService.ValidateIntermediate(task, intermediate);
        intermediate.AudioTracks = [intermediate.AudioTracks[0], new() { Codec = "pcm_s32le", Channels = 2 }];
        Assert.Throws<InvalidDataException>(() => MediaCompatibilityDecodeService.ValidateIntermediate(task, intermediate));
    }

    [Fact]
    public async Task CompatibleIntermediate_UsesProductionPrepareAndCleansOnlyPrivateOutput()
    {
        using var directory = new TemporaryDirectory();
        var task = CreateTask();
        task.InputPath = Path.Combine(directory.Path, "source.mp4");
        await File.WriteAllTextAsync(task.InputPath, "Synthetic source bytes.");
        Enable(task);
        string[]? executedMaps = null;
        var service = new MediaCompatibilityDecodeService(
            async (_, arguments, _, _) =>
            {
                executedMaps = Maps(arguments);
                await File.WriteAllTextAsync(arguments[^1], "Synthetic intermediate bytes.");
                return new ProcessExecutionResult(0, "", "");
            },
            (_, _, _) => Task.FromResult(Intermediate()));
        var work = Path.Combine(directory.Path, "work");
        string preparedPath;
        using (var prepared = await service.PrepareAsync(Path.Combine(directory.Path, "fallback", "ffmpeg.exe"),
            Path.Combine(directory.Path, "primary", "ffmpeg.exe"), task, work))
        {
            preparedPath = prepared.Path;
            Assert.Single(prepared.Probe.AudioTracks);
            Assert.Equal("pcm_s32le", prepared.Probe.AudioTracks[0].Codec);
            Assert.True(File.Exists(preparedPath));
        }
        Assert.Equal(new[] { "0:0", "0:7", "0:s?", "0:t?" }, executedMaps);
        Assert.False(File.Exists(preparedPath));
        Assert.Empty(Directory.GetFileSystemEntries(work));
        Assert.Equal("Synthetic source bytes.", await File.ReadAllTextAsync(task.InputPath));
        Assert.True(CompatibleAudioTrackPolicy.IsRequested(task));
    }

    [Fact]
    public void RequestSnapshot_CapturesTheChoiceAndProbeIndependently()
    {
        var task = CreateTask();
        Enable(task);
        var request = ConversionRequestFactory.Capture(task, new AppSettings());
        task.Options.Clear();
        task.Probe.AudioTracks[1].Codec = "apple_apac";
        var captured = ConversionRequestFactory.CreateMediaTask(request, "ffmpeg");
        Assert.True(CompatibleAudioTrackPolicy.IsRequested(captured));
        Assert.Equal("aac", captured.Probe.AudioTracks[1].Codec);
        Assert.NotSame(task.Probe.AudioTracks[1], captured.Probe.AudioTracks[1]);
        Assert.Equal(7, Assert.Single(CompatibleAudioTrackPolicy.GetSelectedAudioTracks(captured, captured.Probe)).StreamIndex);
    }

    [Theory]
    [InlineData("balanced")]
    [InlineData("lossless")]
    public async Task QueueRoundTripAndFreshImport_PreserveExplicitChoiceWithoutEnablingLegacyRecords(string mode)
    {
        using var directory = new TemporaryDirectory();
        var task = CreateTask();
        task.InputPath = Path.Combine(directory.Path, "source.mp4");
        task.ConversionMode = mode;
        await File.WriteAllTextAsync(task.InputPath, "Synthetic source bytes.");
        Enable(task);
        var path = Path.Combine(directory.Path, "queue.json");
        await new QueueStore(path).SaveAsync([task]);
        task.Options.Clear();
        var persisted = Assert.Single(await new QueueStore(path).LoadAsync());
        Assert.Equal(CompatibleAudioTrackPolicy.CompatibleValue, persisted.Options[CompatibleAudioTrackPolicy.OptionKey]);
        var importer = new MediaImportCoordinator((_, _, _) => Task.FromResult(CreateTask().Probe));
        var restored = (await importer.ImportPersistedAsync("synthetic-ffprobe.exe", persisted)).Task;
        Assert.NotNull(restored);
        Assert.True(CompatibleAudioTrackPolicy.IsRequested(restored));
        Assert.Equal(mode, restored.ConversionMode);
        if (mode == "lossless") Assert.False(CompatibleAudioTrackPolicy.CanSelect(restored, restored.Probe));
        else Assert.True(CompatibleAudioTrackPolicy.CanSelect(restored, restored.Probe));

        persisted.TaskId = Guid.NewGuid();
        persisted.Options.Clear();
        var legacy = (await new MediaImportCoordinator((_, _, _) => Task.FromResult(CreateTask().Probe))
            .ImportPersistedAsync("synthetic-ffprobe.exe", persisted)).Task;
        Assert.NotNull(legacy);
        Assert.False(CompatibleAudioTrackPolicy.IsRequested(legacy));
    }

    [Fact]
    public async Task CompletedHistoryRoundTripAndReplay_PreserveChoiceButExcludeCredentials()
    {
        using var directory = new TemporaryDirectory();
        var task = CreateTask();
        task.InputPath = Path.Combine(directory.Path, "source.mp4");
        task.OutputPath = Path.Combine(directory.Path, "output.mp4");
        task.State = ConversionState.Completed;
        await File.WriteAllTextAsync(task.InputPath, "Synthetic source bytes.");
        await File.WriteAllTextAsync(task.OutputPath, "Synthetic output bytes.");
        Enable(task);
        task.Options["password"] = "synthetic-private-password";
        task.Options["apiToken"] = "synthetic-private-token";
        var path = Path.Combine(directory.Path, "history.json");
        var store = new ConversionHistoryStore(path);
        await store.RecordCompletedAsync(task);
        var item = Assert.Single(await store.LoadAsync());
        Assert.Equal(CompatibleAudioTrackPolicy.CompatibleValue, item.Options[CompatibleAudioTrackPolicy.OptionKey]);
        Assert.DoesNotContain("synthetic-private-", await File.ReadAllTextAsync(path));
        task.Options.Clear();
        var imported = await HistoryReplayService.ImportAsync(item, "synthetic-ffprobe.exe",
            new MediaImportCoordinator((_, _, _) => Task.FromResult(CreateTask().Probe)));
        Assert.NotNull(imported.Task);
        Assert.True(CompatibleAudioTrackPolicy.IsRequested(imported.Task));
        Assert.True(CompatibleAudioTrackPolicy.CanSelect(imported.Task, imported.Task.Probe));
    }

    [Fact]
    public void ViewModel_ChoiceIsExplicitAndRetainedWhenLosslessBlocksIt()
    {
        var task = CreateTask();
        using var viewModel = new MediaTaskViewModel(task);
        Assert.True(viewModel.IsCompatibleAudioTrackVisible);
        Assert.True(viewModel.CanUseCompatibleAudioTrack);
        Assert.False(viewModel.UseCompatibleAudioTrack);
        viewModel.UseCompatibleAudioTrack = true;
        Assert.True(CompatibleAudioTrackPolicy.IsRequested(task));
        viewModel.ConversionMode = "lossless";
        Assert.True(viewModel.UseCompatibleAudioTrack);
        Assert.True(viewModel.CanUseCompatibleAudioTrack);
        Assert.False(string.IsNullOrWhiteSpace(CompatibleAudioTrackPolicy.GetValidationError(task, task.Probe)));
        viewModel.ConversionMode = "balanced";
        Assert.True(viewModel.CanUseCompatibleAudioTrack);
        Assert.True(viewModel.UseCompatibleAudioTrack);
        viewModel.UseCompatibleAudioTrack = false;
        Assert.False(CompatibleAudioTrackPolicy.IsRequested(task));
    }

    [Theory]
    [InlineData(ConversionState.Probing)]
    [InlineData(ConversionState.Running)]
    [InlineData(ConversionState.Paused)]
    public void ViewModel_ActiveConversionCannotChangeItsAudioChoice(ConversionState state)
    {
        var task = CreateTask();
        Enable(task);
        using var viewModel = new MediaTaskViewModel(task);
        task.State = state;
        viewModel.UseCompatibleAudioTrack = false;
        Assert.True(viewModel.UseCompatibleAudioTrack);
        Assert.True(CompatibleAudioTrackPolicy.IsRequested(task));
        Assert.False(viewModel.CanUseCompatibleAudioTrack);
    }

    [Fact]
    public void ViewModel_ProbeReplacementUpdatesAvailabilityWithoutSilentlyClearingChoice()
    {
        var task = CreateTask();
        Enable(task);
        using var viewModel = new MediaTaskViewModel(task);
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        var missingAlternative = CreateTask().Probe;
        missingAlternative.AudioTracks = [missingAlternative.AudioTracks[0]];
        task.Probe = missingAlternative;
        Assert.True(viewModel.CanUseCompatibleAudioTrack);
        Assert.True(viewModel.UseCompatibleAudioTrack);
        Assert.Contains(nameof(MediaTaskViewModel.CanUseCompatibleAudioTrack), changed);
        Assert.False(string.IsNullOrWhiteSpace(CompatibleAudioTrackPolicy.GetValidationError(task, task.Probe)));
        viewModel.UseCompatibleAudioTrack = false;
        Assert.False(viewModel.UseCompatibleAudioTrack);
        Assert.False(viewModel.CanUseCompatibleAudioTrack);
    }

    [Fact]
    public void CompatibleSelection_SkipsSurroundAacAndKeepsOnlyFirstStereoAlternative()
    {
        var task = CreateTask();
        task.Probe.AudioTracks[1].Channels = 6;
        Enable(task);
        var selected = Assert.Single(CompatibleAudioTrackPolicy.GetSelectedAudioTracks(task, task.Probe));
        Assert.Equal(9, selected.StreamIndex);
        Assert.Equal(2, selected.Channels);
    }

    [Theory]
    [InlineData("mp4", "aac", 2, 1, true, true)]
    [InlineData("mp3", "mp3", 2, 1, false, true)]
    [InlineData("mp4", "aac", 2, 0, true, false)]
    [InlineData("mp4", "aac", 2, 2, true, false)]
    [InlineData("mp4", "aac", 1, 1, true, false)]
    [InlineData("mp4", "aac", 0, 1, true, false)]
    [InlineData("mp4", "aac", 6, 1, true, false)]
    [InlineData("mp4", "apple_apac", 2, 1, true, false)]
    [InlineData("mp4", "aac", 2, 1, false, false)]
    [InlineData("mp3", "aac", 2, 1, true, false)]
    public void OutputValidation_RejectsMissingExtraApacOrUnexpectedMedia(string target, string codec,
        int channels, int count, bool hasVideo, bool expected)
    {
        var task = CreateTask(target);
        Enable(task);
        var output = new MediaProbeResult
        {
            Kind = hasVideo ? MediaKind.Video : MediaKind.Audio,
            VideoStreamIndex = hasVideo ? 0 : -1,
            VideoCodec = hasVideo ? "h264" : "",
            AudioTracks = Enumerable.Range(0, count).Select(index => new AudioTrackInfo
            {
                Codec = codec, Channels = channels, StreamIndex = index + 1
            }).ToArray()
        };
        Assert.Equal(expected, MediaProbeService.ValidateCompatibleAudioOutput(task, output));
    }

    [Theory]
    [InlineData("mp4", 1, 1, 1, "aac", true)]
    [InlineData("mp3", 1, 1, 1, "mp3", true)]
    [InlineData("mp4", 1, 2, 1, "aac", false)]
    [InlineData("mp3", 1, 2, 1, "mp3", false)]
    [InlineData("mp4", 2, 1, 1, "aac", false)]
    [InlineData("mp4", 2, 2, 1, "aac", true)]
    [InlineData("mp4", 1, 1, 2, "aac", false)]
    [InlineData("mp4", 1, 1, 1, "apple_apac", false)]
    public void OutputValidation_RespectsExplicitChannelsWithoutAllowingExtraOrApacTracks(
        string target, int requestedChannels, int actualChannels, int count, string codec, bool expected)
    {
        var task = CreateTask(target);
        Enable(task);
        new MediaConversionOptions { Channels = requestedChannels }.ApplyTo(task.Options);
        var hasVideo = !FormatCatalog.IsAudioFormat(task.TargetFormat);
        var output = new MediaProbeResult
        {
            Kind = hasVideo ? MediaKind.Video : MediaKind.Audio,
            VideoStreamIndex = hasVideo ? 0 : -1,
            VideoCodec = hasVideo ? "h264" : "",
            AudioTracks = Enumerable.Range(0, count).Select(index => new AudioTrackInfo
            {
                Codec = codec, Channels = actualChannels, StreamIndex = index + 1
            }).ToArray()
        };
        Assert.Equal(expected, MediaProbeService.ValidateCompatibleAudioOutput(task, output));
    }

    [Fact]
    public void ViewModel_CannotEnableCompatibilityFromAnUncheckedLosslessMode()
    {
        var task = CreateTask();
        task.ConversionMode = "lossless";
        using var viewModel = new MediaTaskViewModel(task);
        Assert.False(viewModel.CanUseCompatibleAudioTrack);
        viewModel.UseCompatibleAudioTrack = true;
        Assert.False(viewModel.UseCompatibleAudioTrack);
        Assert.False(CompatibleAudioTrackPolicy.IsRequested(task));
    }

    [Theory]
    [InlineData("mp4", 1)]
    [InlineData("mp4", 6)]
    [InlineData("mp3", 1)]
    [InlineData("mp3", 6)]
    public void CompatibleIntermediate_RejectsMonoOrSurroundPcmInsteadOfReplacingStereo(string target, int channels)
    {
        var task = CreateTask(target);
        Enable(task);
        var intermediate = Intermediate();
        intermediate.AudioTracks[0].Channels = channels;
        if (FormatCatalog.IsAudioFormat(task.TargetFormat))
        {
            intermediate.VideoStreamIndex = -1;
            intermediate.VideoCodec = "";
            intermediate.Kind = MediaKind.Audio;
        }
        Assert.Throws<InvalidDataException>(() =>
            MediaCompatibilityDecodeService.ValidateIntermediate(task, intermediate));
    }

    [Theory]
    [InlineData("mp4")]
    [InlineData("mp3")]
    public void CompatibleIntermediate_AcceptsExactlyOneStereoPcmTrack(string target)
    {
        var task = CreateTask(target);
        Enable(task);
        var intermediate = Intermediate();
        if (FormatCatalog.IsAudioFormat(task.TargetFormat))
        {
            intermediate.VideoStreamIndex = -1;
            intermediate.VideoCodec = "";
            intermediate.Kind = MediaKind.Audio;
        }
        MediaCompatibilityDecodeService.ValidateIntermediate(task, intermediate);
    }

    [Fact]
    public void DefaultIntermediate_StillAcceptsOriginalSurroundTrack()
    {
        var task = CreateTask("mp3");
        task.SelectedAudioTrack = 0;
        var intermediate = Intermediate();
        intermediate.AudioTracks[0].Channels = 6;
        intermediate.VideoStreamIndex = -1;
        intermediate.VideoCodec = "";
        intermediate.Kind = MediaKind.Audio;
        MediaCompatibilityDecodeService.ValidateIntermediate(task, intermediate);
        Assert.False(CompatibleAudioTrackPolicy.IsRequested(task));
    }

    private static void Enable(MediaTask task) =>
        task.Options[CompatibleAudioTrackPolicy.OptionKey] = CompatibleAudioTrackPolicy.CompatibleValue;

    private static MediaTask CreateTask(string target = "mp4", MediaKind sourceKind = MediaKind.Video)
    {
        var probe = new MediaProbeResult
        {
            Kind = sourceKind, FormatId = "mp4", Width = 64, Height = 64, Duration = TimeSpan.FromSeconds(1),
            VideoCodec = sourceKind == MediaKind.Video ? "h264" : "",
            VideoStreamIndex = sourceKind == MediaKind.Video ? 0 : -1,
            AudioTracks =
            [
                new() { Codec = "apple_apac", CodecTag = "apac", StreamIndex = 3, RelativeIndex = 0, Channels = 6 },
                new() { Codec = "aac", StreamIndex = 7, RelativeIndex = 1, Channels = 2, Language = "en" },
                new() { Codec = "aac", StreamIndex = 9, RelativeIndex = 2, Channels = 2, Language = "fr" }
            ]
        };
        var task = new MediaTask { InputPath = "synthetic.mp4", SourceFormatId = "mp4", TargetFormatId = target, Probe = probe };
        foreach (var track in probe.AudioTracks) task.AudioTracks.Add(track);
        return task;
    }

    private static MediaProbeResult Intermediate() => new()
    {
        Kind = MediaKind.Video, VideoStreamIndex = 0, VideoCodec = "ffv1", Width = 64, Height = 64,
        Duration = TimeSpan.FromSeconds(1), AudioTracks = [new() { Codec = "pcm_s32le", Channels = 2, StreamIndex = 1 }]
    };

    private static string[] Maps(IReadOnlyList<string> arguments) => arguments
        .Select((argument, index) => (argument, index))
        .Where(item => item.argument == "-map")
        .Select(item => arguments[item.index + 1]).ToArray();

    private sealed class PreflightFixture(MediaProbeResult actual)
    {
        public string Primary { get; } = Path.GetFullPath("synthetic-engines/primary/ffmpeg.exe");
        public string Fallback { get; } = Path.GetFullPath("synthetic-engines/fallback/ffmpeg.exe");
        public bool Decoded { get; private set; }
        public bool FallbackValidated { get; private set; }
        public string[]? DecodedMaps { get; private set; }
        public MediaInputPreflightService Service => new(
            (_, _, _) => Task.FromResult(actual),
            (_, _) => Task.FromResult(new FfmpegCapabilitySnapshot(new HashSet<string> { "mp4", "mp3", "aac", "matroska" })
            {
                Decoders = new HashSet<string> { "h264", "aac" },
                Encoders = new HashSet<string> { "libx264", "aac", "libmp3lame", "ffv1", "pcm_s32le" }
            }),
            (_, task, probe, _) =>
            {
                Decoded = true;
                DecodedMaps = Maps(MediaInputPreflightService.BuildDecodeArguments(task, probe));
                return Task.FromResult(new MediaDecodeCheckResult(true, ""));
            },
            (_, _) =>
            {
                FallbackValidated = true;
                return Task.FromResult(true);
            });
    }
}
