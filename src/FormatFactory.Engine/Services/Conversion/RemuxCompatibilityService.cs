using System.Globalization;
using System.Text.Json;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

/// <summary>Checks every stream that -map copies before a lossless media conversion starts.</summary>
internal static class RemuxCompatibilityService
{
    internal static bool IsRequested(MediaTask task) => task.ConversionMode == "lossless" &&
        task.Kind is MediaKind.Audio or MediaKind.Video;

    internal static async Task<IReadOnlyList<RemuxStream>> PrepareAsync(
        MediaTask task, string ffprobePath, CancellationToken cancellationToken)
    {
        try
        {
        ValidateTarget(task);
        var json = await ReadStreamsAsync(ffprobePath, task.InputPath, cancellationToken);
        var streams = ParseStreams(json);
        var copied = await ValidateAsync(task, streams, cancellationToken);
        // Compatibility is checked against the current file, not stale imported metadata.
        task.Probe = await MediaProbeParser.ParseAsync(json, task.InputPath, cancellationToken);
        return copied;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            FormatFailureDiagnostics.Record(error, Fact(task, FormatFailureStage.RemuxProbe, FormatFailureReason.ParseFailure));
            FormatFailureDiagnostics.WriteApp(error, task.Id);
            throw;
        }
    }

    internal static void ValidateTarget(MediaTask task)
    {
        if (FormatCatalog.IsVideoCodec(task.TargetFormatId) || FormatCatalog.IsFrameSequence(task.TargetFormatId))
            throw FormatFailureDiagnostics.Attach(new NotSupportedException(LocalizationService.T("ConversionUiLosslessRouteUnsupported")),
                Fact(task, FormatFailureStage.RemuxPolicy, FormatFailureReason.UnsupportedRoute));
        var target = FormatCatalog.FindById(task.TargetFormatId);
        if (target?.ContentKind is not (ContentKind.Audio or ContentKind.Video) || !target.CanExport)
            throw FormatFailureDiagnostics.Attach(Unsupported("media streams", task.TargetFormat),
                Fact(task, FormatFailureStage.RemuxPolicy, FormatFailureReason.UnsupportedRoute));
    }

    internal static IReadOnlyList<RemuxStream> Validate(MediaTask task, IReadOnlyList<RemuxStream> streams) =>
        ValidateAsync(task, streams, CancellationToken.None).GetAwaiter().GetResult();

    private static async Task<IReadOnlyList<RemuxStream>> ValidateAsync(MediaTask task, IReadOnlyList<RemuxStream> streams, CancellationToken token)
    {
        ValidateTarget(task);
        if (streams.Count == 0) throw FormatFailureDiagnostics.Attach(UnknownStreams(), Fact(task, FormatFailureStage.RemuxPolicy, FormatFailureReason.UnknownStreams));
        IReadOnlyList<RemuxStream> copied = streams;
        if (FormatCatalog.IsAudioFormat(task.TargetFormat))
        {
            var audio = streams.Where(stream => stream.Type == "audio").ToArray();
            var index = task.Kind == MediaKind.Video ? task.SelectedAudioTrack : 0;
            if (index < 0 || index >= audio.Length) throw FormatFailureDiagnostics.Attach(UnknownStreams(),
                Fact(task, FormatFailureStage.RemuxPolicy, FormatFailureReason.StreamSelection) with { Index = index, Count = audio.Length });
            copied = [audio[index]];
        }

        var container = FormatCatalog.IsAudioFormat(task.TargetFormat)
            ? task.TargetFormatId : FormatCatalog.ContainerForVideoTarget(task.TargetFormat).ToLowerInvariant();
        var verdict = await ConversionBackend.RequestAsync<JsonElement>("media-plan", new
        {
            action = "remux", container, streams = copied.Select(stream => new
            {
                type = stream.Type, codec = stream.Codec, attachedPicture = stream.AttachedPicture,
                channels = stream.Channels, sampleRate = stream.SampleRate
            })
        }, token).ConfigureAwait(false);
        switch (verdict[0].GetString())
        {
            case "ok": return copied;
            case "unknown":
                throw FormatFailureDiagnostics.Attach(UnknownStreams(), Fact(task, FormatFailureStage.RemuxPolicy, FormatFailureReason.UnknownStreams) with { Count = copied.Count });
            case "unsupported":
                var stream = copied[verdict[1].GetInt32()];
                throw FormatFailureDiagnostics.Attach(Unsupported($"{stream.Type}: {stream.Codec}", task.TargetFormat),
                    Fact(task, FormatFailureStage.RemuxPolicy, FormatFailureReason.UnsupportedStream) with
                    { Codec = stream.Codec, StreamType = stream.Type, Width = stream.Width, Height = stream.Height, Count = copied.Count });
            case "multiple":
                throw FormatFailureDiagnostics.Attach(Unsupported("multiple audio/video streams", task.TargetFormat),
                    Fact(task, FormatFailureStage.RemuxPolicy, FormatFailureReason.MultipleStreams) with { Count = copied.Count });
            default:
                throw UnknownStreams();
        }
    }

    internal static async Task<bool> ValidateOutputAsync(string ffprobePath, string outputPath,
        IReadOnlyList<RemuxStream> expected, CancellationToken cancellationToken)
    {
        try
        {
            var actual = ParseStreams(await ReadStreamsAsync(ffprobePath, outputPath, cancellationToken));
            var matches = await MatchesOutputAsync(expected, actual, cancellationToken);
            if (!matches)
            {
                var observation = new object();
                FormatFailureDiagnostics.Record(observation, new(FormatFailureStage.RemuxOutput, FormatFailureReason.OutputValidation)
                    { Count = actual.Count, Index = expected.Count });
                FormatFailureDiagnostics.WriteApp(observation);
            }
            return matches;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or TimeoutException)
        {
            FormatFailureDiagnostics.Record(exception, new(FormatFailureStage.RemuxOutput, FormatFailureReason.ParseFailure) { Count = expected.Count });
            FormatFailureDiagnostics.WriteApp(exception);
            return false;
        }
    }

    internal static bool MatchesOutput(IReadOnlyList<RemuxStream> expected, IReadOnlyList<RemuxStream> actual) =>
        MatchesOutputAsync(expected, actual, CancellationToken.None).GetAwaiter().GetResult();

    private static Task<bool> MatchesOutputAsync(IReadOnlyList<RemuxStream> expected, IReadOnlyList<RemuxStream> actual, CancellationToken token)
    {
        static object Describe(RemuxStream s) => new { type = s.Type, codec = s.Codec, width = s.Width, height = s.Height, sampleRate = s.SampleRate, channels = s.Channels };
        return ConversionBackend.RequestAsync<bool>("media-plan", new { action = "matchStreams", expected = expected.Select(Describe), actual = actual.Select(Describe) }, token);
    }

    internal static IReadOnlyList<RemuxStream> ParseStreams(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
            throw UnknownStreams();
        return streams.EnumerateArray().Select(stream => new RemuxStream(
            Text(stream, "codec_type"), Text(stream, "codec_name"),
            Number(stream, "width"), Number(stream, "height"), Number(stream, "sample_rate"), Number(stream, "channels"),
            stream.TryGetProperty("disposition", out var disposition) && Number(disposition, "attached_pic") != 0)).ToArray();
    }

    private static string Text(JsonElement stream, string key) => stream.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.String ? (value.GetString() ?? string.Empty).ToLowerInvariant() : string.Empty;
    private static int Number(JsonElement stream, string key) => stream.TryGetProperty(key, out var value) &&
        int.TryParse(value.ToString(), CultureInfo.InvariantCulture, out var number) ? number : 0;

    private static async Task<string> ReadStreamsAsync(string ffprobePath, string inputPath, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(ffprobePath,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", inputPath],
            TimeSpan.FromSeconds(30), cancellationToken);
        if (result.ExitCode != 0) throw UnknownStreams();
        return result.StandardOutput;
    }

    private static NotSupportedException Unsupported(string stream, string target) =>
        new(LocalizationService.T("RemuxUnsupportedStream", stream, target));
    private static InvalidDataException UnknownStreams() => new(LocalizationService.T("RemuxUnknownStreams"));
    private static FormatFailureFact Fact(MediaTask task, FormatFailureStage stage, FormatFailureReason reason) =>
        new(stage, reason) { Source = task.SourceFormatId, Target = task.TargetFormatId, Lossless = task.ConversionMode == "lossless" };
}

internal sealed record RemuxStream(string Type, string Codec, int Width = 0, int Height = 0,
    int SampleRate = 0, int Channels = 0, bool AttachedPicture = false);
