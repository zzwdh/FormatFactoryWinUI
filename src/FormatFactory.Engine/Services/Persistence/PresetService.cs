using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

public enum EncoderBackend
{
    Nvidia,
    Intel,
    Amd,
    MediaFoundation,
    Cpu
}

public sealed class PresetService
{
    private static readonly HashSet<string> TextSubtitleCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "subrip", "srt", "ass", "ssa", "webvtt", "mov_text", "text"
    };

    public IReadOnlyList<string> BuildArguments(
        MediaTask task,
        string temporaryOutput,
        EncoderBackend backend,
        bool hardwareDecode = false,
        int cpuThreads = 0) => BuildArguments(task, temporaryOutput, backend, hardwareDecode, cpuThreads, CancellationToken.None);

    internal IReadOnlyList<string> BuildArguments(MediaTask task, string temporaryOutput, EncoderBackend backend,
        bool hardwareDecode, int cpuThreads, CancellationToken cancellationToken, MediaConversionOptions? executionOptions = null)
    {
        var requestedOptions = MediaConversionOptions.FromTask(task);
        requestedOptions.Validate(task);
        var options = executionOptions ?? MediaSizeBudget.Initial(task, requestedOptions);
        if (requestedOptions.TargetSizeBytes.HasValue)
        {
            backend = EncoderBackend.Cpu;
            hardwareDecode = false;
        }
        if (options.HasVideoFilters || RequiresYcgcoConversion(task)) hardwareDecode = false;
        if (CompatibleAudioTrackPolicy.GetValidationError(task, task.Probe) is { } selectionError)
            throw new NotSupportedException(selectionError);
        var animation = ImagePreservationPolicy.ValidateFfmpegInput(task, cancellationToken);
        if (RemuxCompatibilityService.IsRequested(task)) RemuxCompatibilityService.ValidateTarget(task);
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "warning", "-nostdin", "-y", "-stats_period", "0.5"
        };
        if (requestedOptions.TargetSizeBytes.HasValue) arguments.AddRange(["-xerror", "-err_detect", "explode"]);
        if (Path.GetExtension(task.InputPath).Equals(".m4s", StringComparison.OrdinalIgnoreCase))
        {
            arguments.AddRange(["-fflags", "+genpts"]);
        }

        if (hardwareDecode)
        {
            arguments.AddRange(backend switch
            {
                EncoderBackend.Nvidia => ["-hwaccel", "cuda", "-hwaccel_output_format", "cuda", "-extra_hw_frames", "8"],
                EncoderBackend.Intel => ["-hwaccel", "qsv", "-hwaccel_output_format", "qsv", "-extra_hw_frames", "8"],
                EncoderBackend.MediaFoundation => ["-hwaccel", "d3d11va", "-hwaccel_output_format", "d3d11", "-extra_hw_frames", "8"],
                _ => []
            });
        }

        arguments.AddRange(["-i", task.InputPath]);
        if (animation is { IsAnimated: true })
        {
            BuildPreservedAnimation(task, animation, arguments);
        }
        else if (task.ConversionMode == "lossless" && !FormatCatalog.IsVideoCodec(task.TargetFormat) && !FormatCatalog.IsFrameSequence(task.TargetFormat))
        {
            BuildLossless(task, arguments);
        }
        else if (FormatCatalog.IsFrameSequence(task.TargetFormat))
        {
            BuildFrameSequence(task, arguments);
        }
        else if (FormatCatalog.IsAudioFormat(task.TargetFormat))
        {
            BuildAudio(task, arguments);
        }
        else if (task.Kind == MediaKind.Image || FormatCatalog.IsImageFormat(task.TargetFormat))
        {
            if (task.Kind == MediaKind.Video && task.TargetFormat == "GIF")
            {
                BuildAnimatedGif(task, arguments);
            }
            else
            {
                BuildImage(task, arguments);
            }
        }
        else
        {
            BuildVideo(task, arguments, backend, hardwareDecode, options.HasDimensions);
            if (RequiresYcgcoConversion(task))
            {
                // swscale cannot convert YCoCg. Transform the samples before the
                // existing resize/padding/tone-map filters, then label that matrix.
                // Full-chroma 16-bit output preserves precision and allows odd sizes
                // to reach the existing padding before encoder subsampling.
                const string matrixFilter = "zscale=matrixin=ycgco:matrix=bt709,format=yuv444p16le";
                var filterIndex = arguments.IndexOf("-vf");
                if (filterIndex >= 0) arguments[filterIndex + 1] = matrixFilter + "," + arguments[filterIndex + 1];
                else arguments.AddRange(["-vf", matrixFilter]);
                SetOption(arguments, "-colorspace", "bt709");
            }
        }

        ApplyCustomOptions(task, arguments, options, backend);

        if (backend == EncoderBackend.Cpu && cpuThreads > 0)
        {
            arguments.AddRange(["-threads", cpuThreads.ToString(), "-filter_threads", cpuThreads.ToString(), "-filter_complex_threads", cpuThreads.ToString()]);
        }
        var outputPath = FormatCatalog.IsFrameSequence(task.TargetFormat)
            ? Path.Combine(temporaryOutput, "frame-%06d.png")
            : temporaryOutput;
        arguments.AddRange(["-progress", "pipe:1", "-nostats", outputPath]);
        return arguments;
    }

    internal static bool RequiresYcgcoConversion(MediaTask task) =>
        task.Kind == MediaKind.Video && task.Probe.ColorSpace == "ycgco";

    public IReadOnlyList<string> BuildIcoFrameArguments(MediaTask task, int size, string outputPng)
    {
        var filter = $"scale={size}:{size}:force_original_aspect_ratio=decrease,pad={size}:{size}:(ow-iw)/2:(oh-ih)/2:color=0x00000000";
        return ["-hide_banner", "-y", "-i", task.InputPath, "-vf", filter, "-frames:v", "1", "-c:v", "png", "-progress", "pipe:1", "-nostats", outputPng];
    }

    public bool WillUseHardware(MediaTask task) =>
        task.Kind == MediaKind.Video &&
        FormatCatalog.VideoCodecForTarget(task.TargetFormat) is not ("vp9" or "av1") &&
        FormatCatalog.ContainerForVideoTarget(task.TargetFormat) is "MP4" or "MKV" or "MOV" or "TS" or "FLV";

    public string GetPreservationWarning(MediaTask task)
    {
        if (CompatibleAudioTrackPolicy.IsRequested(task) && CompatibleAudioTrackPolicy.IsSupportedOutput(task))
            return LocalizationService.T("MediaCompatibilityAacWarning");

        if (task.Probe.IsHdr && task.TargetFormat is "AVI" or "FLV")
        {
            return LocalizationService.T("HdrToSdr");
        }

        if (task.Probe.SubtitleCodecs.Count > 0 && task.TargetFormat is "AVI" or "FLV")
        {
            return LocalizationService.T("SubtitleIgnored");
        }

        if (task.Probe.SubtitleCodecs.Any(codec => !TextSubtitleCodecs.Contains(codec)) && task.TargetFormat is "MP4" or "MOV")
        {
            return LocalizationService.T("GraphicSubtitleIgnored");
        }

        return string.Empty;
    }

    private static void BuildLossless(MediaTask task, List<string> arguments)
    {
        if (task.Kind == MediaKind.Image)
        {
            BuildLosslessImage(task, arguments);
            return;
        }

        if (FormatCatalog.IsAudioFormat(task.TargetFormat))
        {
            var audioIndex = ResolveAudioTrackIndex(task);
            arguments.AddRange(["-map", $"0:a:{audioIndex}", "-vn", "-c:a", "copy", "-map_metadata", "0"]);
            return;
        }

        arguments.AddRange(["-map", "0", "-c", "copy", "-map_metadata", "0", "-map_chapters", "0"]);
    }

    private static void BuildPreservedAnimation(MediaTask task, ImageAnimationInfo animation, List<string> arguments)
    {
        arguments.AddRange(["-map", "0:V:0", "-an", "-fps_mode", "passthrough"]);
        if (task.TargetFormatId == "webp")
        {
            arguments.AddRange(["-c:v", "libwebp_anim", "-lossless", task.ConversionMode == "lossless" ? "1" : "0",
                "-quality", task.ConversionMode == "quality" ? "95" : task.ConversionMode == "fast" ? "75" : "85",
                "-loop", animation.LoopCount.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }
        else
        {
            var repetitions = animation.LoopCount == 0 ? 0 : animation.LoopCount == 1 ? -1 : animation.LoopCount - 1;
            arguments.AddRange(["-c:v", "gif", "-loop", repetitions.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }
    }

    private static void BuildLosslessImage(MediaTask task, List<string> arguments)
    {
        arguments.AddRange(["-map", "0:V:0", "-frames:v", "1"]);
        switch (task.TargetFormat)
        {
            case "JPG": arguments.AddRange(["-c:v", "ljpeg"]); break;
            case "PNG": arguments.AddRange(["-c:v", "png"]); break;
            case "BMP": arguments.AddRange(["-c:v", "bmp"]); break;
            case "WEBP": arguments.AddRange(["-c:v", "libwebp", "-lossless", "1"]); break;
            case "AVIF": arguments.AddRange(["-c:v", "libaom-av1", "-still-picture", "1", "-crf", "0", "-cpu-used", "6"]); break;
            case "GIF": arguments.AddRange(["-c:v", "gif"]); break;
            case "TIFF": arguments.AddRange(["-c:v", "tiff"]); break;
            case "TGA": arguments.AddRange(["-c:v", "targa"]); break;
        }
    }

    private static void BuildAudio(MediaTask task, List<string> arguments)
    {
        var audioIndex = ResolveAudioTrackIndex(task);
        var compatibleTrack = CompatibleAudioTrackPolicy.IsRequested(task)
            ? CompatibleAudioTrackPolicy.GetSelectedAudioTracks(task, task.Probe)[0] : null;
        var audioMap = compatibleTrack is null ? $"0:a:{audioIndex}" : CompatibleAudioTrackPolicy.GetStreamMap(compatibleTrack);
        var bitrate = task.ConversionMode switch
        {
            "fast" => "128k",
            "quality" => "320k",
            _ => "192k"
        };
        arguments.AddRange(["-map", audioMap, "-vn", "-map_metadata", "0"]);
        switch (task.TargetFormat)
        {
            case "MP3": arguments.AddRange(["-c:a", "libmp3lame", "-b:a", bitrate]); break;
            case "WMA": arguments.AddRange(["-c:a", "wmav2", "-b:a", bitrate]); break;
            case "M4A": arguments.AddRange(["-c:a", "aac", "-b:a", bitrate]); break;
            case "AAC": arguments.AddRange(["-c:a", "aac", "-b:a", bitrate]); break;
            case "FLAC": arguments.AddRange(["-c:a", "flac"]); break;
            case "WAV": arguments.AddRange(["-c:a", "pcm_s16le"]); break;
            case "AIFF": arguments.AddRange(["-c:a", "pcm_s16be"]); break;
            case "OPUS":
                arguments.AddRange(["-c:a", "libopus", "-b:a", task.ConversionMode == "quality" ? "160k" : "128k"]);
                AddOpusChannelLayout(arguments, compatibleTrack ?? task.Probe.AudioTracks.ElementAtOrDefault(audioIndex), 0);
                break;
            case "VORBIS":
            case "OGG": arguments.AddRange(["-c:a", "libvorbis", "-q:a", task.ConversionMode == "quality" ? "6" : task.ConversionMode == "fast" ? "3" : "5"]); break;
            case "ALAC": arguments.AddRange(["-c:a", "alac"]); break;
            case "AC3": arguments.AddRange(["-c:a", "ac3", "-b:a", bitrate]); break;
            case "EAC3": arguments.AddRange(["-c:a", "eac3", "-b:a", task.ConversionMode == "quality" ? "640k" : "384k"]); break;
        }
    }

    private static void BuildFrameSequence(MediaTask task, List<string> arguments)
    {
        var frameRate = task.ConversionMode switch
        {
            "fast" => "8",
            "quality" => "30",
            _ => "15"
        };
        arguments.AddRange([
            "-map", "0:V:0", "-an",
            "-vf", $"fps={frameRate}",
            "-fps_mode", "passthrough", "-start_number", "1",
            "-c:v", "png"
        ]);
    }

    private static int ResolveAudioTrackIndex(MediaTask task) =>
        // A cleared selector can report -1. The default audio track remains the
        // first track; a negative FFmpeg stream specifier is invalid syntax.
        task.Kind == MediaKind.Video ? Math.Max(0, task.SelectedAudioTrack) : 0;
    private static void BuildAnimatedGif(MediaTask task, List<string> arguments)
    {
        var frameRate = task.ConversionMode switch
        {
            "fast" => "8",
            "quality" => "15",
            _ => "12"
        };
        arguments.AddRange([
            "-map", "0:V:0", "-an",
            "-vf", $"fps={frameRate},scale=720:-1:flags=lanczos",
            "-c:v", "gif", "-loop", "0"
        ]);
    }
    private static void BuildImage(MediaTask task, List<string> arguments)
    {
        var flattenAlpha = task.TargetFormat is "JPG" or "BMP" && task.Probe.Width > 0 && task.Probe.Height > 0;
        var imageQuality = task.ConversionMode switch
        {
            "fast" => 5,
            "quality" => 2,
            _ => 3
        };
        var webpQuality = task.ConversionMode switch
        {
            "fast" => "75",
            "quality" => "95",
            _ => "85"
        };
        var avifQuality = task.ConversionMode switch
        {
            "fast" => "32",
            "quality" => "20",
            _ => "28"
        };
        if (flattenAlpha)
        {
            var pixelFormat = task.TargetFormat == "JPG" ? "yuvj420p" : "rgb24";
            var filter = $"[0:V:0]format=rgba[fg];color=white:s={task.Probe.Width}x{task.Probe.Height}[bg];[bg][fg]overlay=shortest=1,format={pixelFormat}[outv]";
            arguments.AddRange(["-filter_complex", filter, "-map", "[outv]"]);
        }
        else
        {
            arguments.AddRange(["-map", "0:V:0"]);
        }

        switch (task.TargetFormat)
        {
            case "JPG": arguments.AddRange(["-frames:v", "1", "-c:v", "mjpeg", "-q:v", imageQuality.ToString()]); break;
            case "PNG": arguments.AddRange(["-frames:v", "1", "-c:v", "png"]); break;
            case "BMP": arguments.AddRange(["-frames:v", "1", "-c:v", "bmp"]); break;
            case "WEBP" when task.Probe.IsAnimated:
                arguments.AddRange(["-c:v", "libwebp_anim", "-quality", "85", "-loop", "0"]);
                break;
            case "WEBP": arguments.AddRange(["-frames:v", "1", "-c:v", "libwebp", "-quality", webpQuality]); break;
            case "AVIF": arguments.AddRange(["-frames:v", "1", "-c:v", "libaom-av1", "-still-picture", "1", "-crf", avifQuality, "-cpu-used", task.ConversionMode == "quality" ? "4" : "6"]); break;
            case "GIF" when task.Probe.IsAnimated:
                arguments.AddRange(["-c:v", "gif", "-loop", "0"]);
                break;
            case "GIF":
                arguments.AddRange(["-frames:v", "1", "-c:v", "gif"]);
                break;
            case "TIFF": arguments.AddRange(["-frames:v", "1", "-c:v", "tiff"]); break;
            case "TGA": arguments.AddRange(["-frames:v", "1", "-c:v", "targa"]); break;
        }
    }

    private static void BuildVideo(MediaTask task, List<string> arguments, EncoderBackend backend, bool hardwareDecode, bool hasCustomDimensions)
    {
        var container = FormatCatalog.ContainerForVideoTarget(task.TargetFormat);
        var requestedCodec = FormatCatalog.VideoCodecForTarget(task.TargetFormat);
        var audioTracks = CompatibleAudioTrackPolicy.GetSelectedAudioTracks(task, task.Probe);
        arguments.AddRange(["-map", "0:V:0"]);
        if (CompatibleAudioTrackPolicy.IsRequested(task) && CompatibleAudioTrackPolicy.IsSupportedOutput(task))
        {
            foreach (var track in audioTracks) arguments.AddRange(["-map", CompatibleAudioTrackPolicy.GetStreamMap(track)]);
        }
        else arguments.AddRange(["-map", "0:a?"]);
        arguments.AddRange(["-map_metadata", "0", "-map_chapters", "0"]);

        if (backend == EncoderBackend.MediaFoundation && hardwareDecode)
        {
            arguments.AddRange(["-vf", "scale_d3d11=iw:ih"]);
        }

        if (container == "MKV")
        {
            arguments.AddRange(["-map", "0:s?", "-map", "0:t?", "-c:s", "copy", "-c:t", "copy"]);
        }
        else if (container is "MP4" or "MOV" && task.Probe.SubtitleCodecs.Count > 0 && task.Probe.SubtitleCodecs.All(TextSubtitleCodecs.Contains))
        {
            arguments.AddRange(["-map", "0:s?", "-c:s", "mov_text"]);
        }

        if (task.Probe.IsHdr && container is "AVI" or "FLV")
        {
            arguments.AddRange(["-vf", "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p"]);
        }

        if (requestedCodec is "vp9" or "av1" || (requestedCodec is null && container == "WEBM"))
        {
            BuildModernVideo(arguments, requestedCodec ?? "vp9", task, audioTracks);
            return;
        }

        if (container == "AVI")
        {
            arguments.AddRange(["-c:v", "mpeg4", "-q:v", "4", "-c:a", "libmp3lame", "-b:a", "192k"]);
            return;
        }

        var hdr = task.Probe.IsHdr && container is "MP4" or "MKV" or "MOV";
        var hevc = requestedCodec == "hevc" || (requestedCodec is null && hdr);
        AddVideoEncoder(arguments, backend, hevc, hdr, hardwareDecode, task.ConversionMode);
        if (backend == EncoderBackend.Cpu && !hevc && !hasCustomDimensions) AddCpuH264Padding(arguments, task.Probe);
        if (task.Probe.IsHdr && container == "FLV")
        {
            arguments.AddRange(["-color_trc", "bt709", "-color_primaries", "bt709", "-colorspace", "bt709", "-color_range", "tv"]);
        }
        else
        {
            AddColorMetadata(arguments, task.Probe);
        }
        if (audioTracks.Count > 0)
        {
            arguments.AddRange(["-c:a", "aac", "-b:a", "192k"]);
        }
        arguments.AddRange(["-max_muxing_queue_size", "4096", "-fps_mode", "passthrough"]);
        if (container == "MP4")
        {
            arguments.AddRange(["-movflags", "+faststart"]);
        }
    }

    private static void AddCpuH264Padding(List<string> arguments, MediaProbeResult probe)
    {
        if (probe.Width <= 0 || probe.Height <= 0 || (probe.Width % 2 == 0 && probe.Height % 2 == 0)) return;
        // 4:2:0 H.264 needs even dimensions. Keep the entire source rectangle and
        // add at most one pixel on the right/bottom before any HDR tone mapping.
        const string padding = "pad=ceil(iw/2)*2:ceil(ih/2)*2:0:0";
        var filterIndex = arguments.IndexOf("-vf");
        if (filterIndex >= 0) arguments[filterIndex + 1] = padding + "," + arguments[filterIndex + 1];
        else arguments.AddRange(["-vf", padding]);
    }

    private static void BuildModernVideo(List<string> arguments, string codec, MediaTask task, IReadOnlyList<AudioTrackInfo> audioTracks)
    {
        var mode = task.ConversionMode;
        var crf = mode switch
        {
            "fast" => codec == "av1" ? "38" : "34",
            "quality" => "24",
            _ => codec == "av1" ? "32" : "30"
        };
        if (codec == "av1")
        {
            arguments.AddRange(["-c:v", "libaom-av1", "-crf", crf, "-b:v", "0", "-cpu-used", mode == "quality" ? "4" : "6"]);
        }
        else
        {
            arguments.AddRange(["-c:v", "libvpx-vp9", "-crf", crf, "-b:v", "0", "-row-mt", "1"]);
        }
        if (audioTracks.Count > 0)
        {
            arguments.AddRange(["-c:a", "libopus", "-b:a", mode == "quality" ? "160k" : "128k"]);
            for (var audioIndex = 0; audioIndex < audioTracks.Count; audioIndex++)
                AddOpusChannelLayout(arguments, audioTracks[audioIndex], audioIndex);
        }
        arguments.AddRange(["-max_muxing_queue_size", "4096", "-fps_mode", "passthrough"]);
    }

    private static void AddOpusChannelLayout(List<string> arguments, AudioTrackInfo? track, int outputAudioIndex)
    {
        if (track is { Channels: 6 } && string.Equals(track.ChannelLayout, "5.1(side)", StringComparison.OrdinalIgnoreCase))
        {
            // Opus accepts the standard 5.1 rear-surround labels. Relabel the side pair
            // without mixing or reordering samples, and leave other output tracks alone.
            arguments.AddRange([$"-filter:a:{outputAudioIndex}", "channelmap=map=0|1|2|3|4|5:channel_layout=5.1"]);
        }
    }

    private static void AddVideoEncoder(List<string> arguments, EncoderBackend backend, bool hevc, bool hdr, bool hardwareDecode, string mode)
    {
        var quality = mode switch
        {
            "fast" => (Preset: "p1", Quality: "28"),
            "quality" => (Preset: "p6", Quality: "18"),
            _ => (Preset: "p4", Quality: "23")
        };

        if (hevc)
        {
            switch (backend)
            {
                case EncoderBackend.Nvidia:
                    arguments.AddRange(["-c:v", "hevc_nvenc", "-preset", quality.Preset, "-tune", "hq", "-rc", "vbr", "-cq", quality.Quality, "-b:v", "0", "-multipass", "disabled", "-spatial-aq", "1", "-temporal-aq", "0", "-aq-strength", "8", "-rc-lookahead", "0", "-bf", "3", "-b_ref_mode", "middle", "-profile:v", hdr ? "main10" : "main"]);
                    if (!hardwareDecode) arguments.AddRange(["-pix_fmt", hdr ? "p010le" : "yuv420p"]);
                    break;
                case EncoderBackend.Intel:
                    arguments.AddRange(["-c:v", "hevc_qsv", "-preset", mode == "quality" ? "slow" : mode == "fast" ? "veryfast" : "fast", "-global_quality", quality.Quality, "-async_depth", "8", "-adaptive_i", "1", "-adaptive_b", "1", "-profile:v", hdr ? "main10" : "main"]);
                    if (!hardwareDecode) arguments.AddRange(["-pix_fmt", hdr ? "p010le" : "nv12"]);
                    break;
                case EncoderBackend.Amd:
                    AddAmdEncoder(arguments, true, hdr, mode);
                    break;
                case EncoderBackend.MediaFoundation:
                    AddMediaFoundationEncoder(arguments, true, hdr, mode, hardwareDecode);
                    break;
                default:
                    arguments.AddRange(["-c:v", "libx265", "-preset", mode == "quality" ? "slow" : mode == "fast" ? "veryfast" : "fast", "-crf", mode == "quality" ? "20" : mode == "fast" ? "30" : "24", "-pix_fmt", hdr ? "yuv420p10le" : "yuv420p"]);
                    break;
            }
            return;
        }

        switch (backend)
        {
            case EncoderBackend.Nvidia:
                arguments.AddRange(["-c:v", "h264_nvenc", "-preset", quality.Preset, "-tune", "hq", "-rc", "vbr", "-cq", quality.Quality, "-b:v", "0", "-multipass", "disabled", "-spatial-aq", "1", "-temporal-aq", "0", "-aq-strength", "8", "-rc-lookahead", "0", "-bf", "3", "-b_ref_mode", "middle"]);
                if (!hardwareDecode) arguments.AddRange(["-pix_fmt", "yuv420p"]);
                break;
            case EncoderBackend.Intel:
                arguments.AddRange(["-c:v", "h264_qsv", "-preset", mode == "quality" ? "slow" : mode == "fast" ? "veryfast" : "fast", "-global_quality", quality.Quality, "-async_depth", "8", "-adaptive_i", "1", "-adaptive_b", "1"]);
                if (!hardwareDecode) arguments.AddRange(["-pix_fmt", "nv12"]);
                break;
            case EncoderBackend.Amd:
                AddAmdEncoder(arguments, false, false, mode);
                break;
            case EncoderBackend.MediaFoundation:
                AddMediaFoundationEncoder(arguments, false, false, mode, hardwareDecode);
                break;
            default:
                arguments.AddRange(["-c:v", "libx264", "-preset", mode == "quality" ? "slow" : mode == "fast" ? "veryfast" : "fast", "-crf", mode == "quality" ? "18" : mode == "fast" ? "28" : "23", "-pix_fmt", "yuv420p"]);
                break;
        }
    }
    private static void AddMediaFoundationEncoder(List<string> arguments, bool hevc, bool hdr, string mode, bool hardwareDecode)
    {
        var quality = mode switch
        {
            "fast" => "65",
            "quality" => "90",
            _ => "80"
        };
        arguments.AddRange(["-c:v", hevc ? "hevc_mf" : "h264_mf", "-hw_encoding", "1", "-rate_control", "quality", "-quality", quality]);
        if (hdr)
        {
            arguments.AddRange(["-profile:v", "main10"]);
            if (!hardwareDecode) arguments.AddRange(["-pix_fmt", "p010le"]);
        }
        else if (!hardwareDecode)
        {
            arguments.AddRange(["-pix_fmt", hevc ? "yuv420p" : "nv12"]);
        }
    }

    private static void AddAmdEncoder(List<string> arguments, bool hevc, bool hdr, string mode)
    {
        var quality = mode == "fast" ? "speed" : "quality";
        var qp = mode == "quality" ? "20" : mode == "fast" ? "28" : "23";
        arguments.AddRange(["-c:v", hevc ? "hevc_amf" : "h264_amf", "-usage", "transcoding", "-quality", quality, "-rc", "cqp", "-qp_i", qp, "-qp_p", qp]);
        if (hdr)
        {
            arguments.AddRange(["-profile:v", "main10", "-pix_fmt", "p010le"]);
        }
        else
        {
            arguments.AddRange(["-pix_fmt", "yuv420p"]);
        }
    }
    private static void AddColorMetadata(List<string> arguments, MediaProbeResult probe)
    {
        AddKnownColor(arguments, "-color_trc", probe.ColorTransfer);
        AddKnownColor(arguments, "-color_primaries", probe.ColorPrimaries);
        // RGB's identity matrix describes the input pixels. The video encoders
        // above convert to YUV; copying that matrix would mislabel their output.
        if (probe.ColorSpace is not ("gbr" or "rgb"))
            AddKnownColor(arguments, "-colorspace", probe.ColorSpace);
        AddKnownColor(arguments, "-color_range", probe.ColorRange);
    }

    private static void AddKnownColor(List<string> arguments, string option, string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && value is not ("unknown" or "unspecified" or "reserved"))
        {
            arguments.AddRange([option, value]);
        }
    }

    private static void ApplyCustomOptions(MediaTask task, List<string> arguments, MediaConversionOptions options, EncoderBackend backend)
    {
        if (!options.HasAny) return;
        if (options.VideoBitrateKbps is { } videoBitrate)
        {
            foreach (var key in new[] { "-crf", "-q:v", "-cq", "-global_quality", "-qp_i", "-qp_p" }) RemoveOption(arguments, key);
            SetOption(arguments, "-b:v", $"{videoBitrate}k");
            switch (backend)
            {
                case EncoderBackend.Nvidia: SetOption(arguments, "-rc", "vbr"); break;
                case EncoderBackend.Amd: SetOption(arguments, "-rc", "vbr_peak"); break;
                case EncoderBackend.MediaFoundation:
                    RemoveOption(arguments, "-quality");
                    SetOption(arguments, "-rate_control", "cbr");
                    break;
            }
        }
        if (options.AudioBitrateKbps is { } audioBitrate)
        {
            RemoveOption(arguments, "-q:a");
            SetOption(arguments, "-b:a", $"{audioBitrate}k");
        }
        if (options.SampleRate is { } sampleRate) SetOption(arguments, "-ar", sampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (options.Channels is { } channels) SetOption(arguments, "-ac", channels.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (options.ImageQuality is { } imageQuality)
        {
            switch (task.TargetFormatId)
            {
                case "jpg": SetOption(arguments, "-q:v", Math.Clamp(31 - (int)Math.Round(imageQuality * 29d / 100), 2, 31).ToString()); break;
                case "webp": SetOption(arguments, "-quality", imageQuality.ToString()); break;
                case "avif": SetOption(arguments, "-crf", Math.Clamp((int)Math.Round((100 - imageQuality) * 63d / 100), 0, 63).ToString()); break;
            }
        }
        if (options.HasDimensions)
        {
            var video = FormatCatalog.FindById(task.TargetFormatId)?.ContentKind == ContentKind.Video;
            string scale;
            if (video)
            {
                // FFmpeg applies display rotation before this filter. Its current dimensions,
                // rather than the coded dimensions in FFprobe, describe the visible rectangle.
                var width = options.MaxWidth?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "iw";
                var height = options.MaxHeight?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "ih";
                scale = $"scale=w='min(iw,{width})':h='min(ih,{height})':force_original_aspect_ratio=decrease:force_divisible_by=2:flags=lanczos";
            }
            else
            {
                var (width, height) = options.ResolveDimensions(task.Probe.Width, task.Probe.Height);
                scale = $"scale={width}:{height}:flags=lanczos";
            }
            var complex = arguments.IndexOf("-filter_complex");
            if (complex >= 0)
            {
                // The original image complex filter composites alpha onto white. Resize its completed result.
                arguments[complex + 1] = arguments[complex + 1].Replace("[outv]", $"[unscaled];[unscaled]{scale}[outv]", StringComparison.Ordinal);
            }
            else AppendVideoFilter(arguments, scale);
        }
        if (options.FrameRate is { } frameRate)
        {
            AppendVideoFilter(arguments, "fps=" + frameRate.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture));
            SetOption(arguments, "-fps_mode", "cfr");
        }
    }

    private static void AppendVideoFilter(List<string> arguments, string filter)
    {
        var index = arguments.IndexOf("-vf");
        if (index >= 0) arguments[index + 1] += "," + filter;
        else arguments.AddRange(["-vf", filter]);
    }

    private static void RemoveOption(List<string> arguments, string option)
    {
        for (var index = arguments.Count - 2; index >= 0; index--)
            if (arguments[index] == option) arguments.RemoveRange(index, 2);
    }

    private static void SetOption(List<string> arguments, string option, string value)
    {
        RemoveOption(arguments, option);
        arguments.AddRange([option, value]);
    }

}
