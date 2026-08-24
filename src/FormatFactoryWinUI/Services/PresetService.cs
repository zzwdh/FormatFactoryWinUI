using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

public enum EncoderBackend
{
    Nvidia,
    Intel,
    Amd,
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
        int cpuThreads = 0)
    {
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "warning", "-nostdin", "-y", "-stats_period", "0.5"
        };
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
                _ => []
            });
        }

        arguments.AddRange(["-i", task.InputPath]);
        if (task.ConversionMode == "无损")
        {
            BuildLossless(task, arguments);
        }
        else if (FormatCatalog.AudioFormats.Contains(task.TargetFormat, StringComparer.OrdinalIgnoreCase))
        {
            BuildAudio(task, arguments);
        }
        else if (task.Kind == MediaKind.Image)
        {
            BuildImage(task, arguments);
        }
        else
        {
            BuildVideo(task, arguments, backend, hardwareDecode);
        }

        if (backend == EncoderBackend.Cpu && cpuThreads > 0)
        {
            arguments.AddRange(["-threads", cpuThreads.ToString(), "-filter_threads", cpuThreads.ToString(), "-filter_complex_threads", cpuThreads.ToString()]);
        }
        arguments.AddRange(["-progress", "pipe:1", "-nostats", temporaryOutput]);
        return arguments;
    }

    public IReadOnlyList<string> BuildIcoFrameArguments(MediaTask task, int size, string outputPng)
    {
        var filter = $"scale={size}:{size}:force_original_aspect_ratio=decrease,pad={size}:{size}:(ow-iw)/2:(oh-ih)/2:color=0x00000000";
        return ["-hide_banner", "-y", "-i", task.InputPath, "-vf", filter, "-frames:v", "1", "-c:v", "png", "-progress", "pipe:1", "-nostats", outputPng];
    }

    public bool WillUseHardware(MediaTask task) =>
        task.Kind == MediaKind.Video &&
        task.TargetFormat is "MP4" or "MKV" or "MOV" or "FLV";

    public string GetPreservationWarning(MediaTask task)
    {
        if (task.Probe.IsHdr && task.TargetFormat is "AVI" or "FLV")
        {
            return "HDR 将转为 SDR。";
        }

        if (task.Probe.SubtitleCodecs.Count > 0 && task.TargetFormat is "AVI" or "FLV")
        {
            return "字幕轨已忽略。";
        }

        if (task.Probe.SubtitleCodecs.Any(codec => !TextSubtitleCodecs.Contains(codec)) && task.TargetFormat is "MP4" or "MOV")
        {
            return "图形字幕已忽略。";
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

        if (FormatCatalog.AudioFormats.Contains(task.TargetFormat, StringComparer.OrdinalIgnoreCase))
        {
            var audioIndex = task.Kind == MediaKind.Video ? task.SelectedAudioTrack : 0;
            arguments.AddRange(["-map", $"0:a:{audioIndex}", "-vn", "-c:a", "copy", "-map_metadata", "0"]);
            return;
        }

        arguments.AddRange(["-map", "0", "-c", "copy", "-map_metadata", "0", "-map_chapters", "0"]);
    }

    private static void BuildLosslessImage(MediaTask task, List<string> arguments)
    {
        arguments.AddRange(["-map", "0:v:0", "-frames:v", "1"]);
        switch (task.TargetFormat)
        {
            case "JPG": arguments.AddRange(["-c:v", "ljpeg"]); break;
            case "PNG": arguments.AddRange(["-c:v", "png"]); break;
            case "BMP": arguments.AddRange(["-c:v", "bmp"]); break;
            case "WEBP": arguments.AddRange(["-c:v", "libwebp", "-lossless", "1"]); break;
            case "AVIF": arguments.AddRange(["-c:v", "libaom-av1", "-still-picture", "1", "-crf", "0", "-cpu-used", "6"]); break;
        }
    }

    private static void BuildAudio(MediaTask task, List<string> arguments)
    {
        var audioIndex = task.Kind == MediaKind.Video ? task.SelectedAudioTrack : 0;
        var bitrate = task.ConversionMode switch
        {
            "快速" => "128k",
            "高质量" => "320k",
            _ => "192k"
        };
        arguments.AddRange(["-map", $"0:a:{audioIndex}", "-vn", "-map_metadata", "0"]);
        switch (task.TargetFormat)
        {
            case "MP3": arguments.AddRange(["-c:a", "libmp3lame", "-b:a", bitrate]); break;
            case "WMA": arguments.AddRange(["-c:a", "wmav2", "-b:a", bitrate]); break;
            case "M4A": arguments.AddRange(["-c:a", "aac", "-b:a", bitrate]); break;
            case "AAC": arguments.AddRange(["-c:a", "aac", "-b:a", bitrate]); break;
            case "FLAC": arguments.AddRange(["-c:a", "flac"]); break;
            case "WAV": arguments.AddRange(["-c:a", "pcm_s16le"]); break;
        }
    }

    private static void BuildImage(MediaTask task, List<string> arguments)
    {
        var flattenAlpha = task.TargetFormat is "JPG" or "BMP" && task.Probe.Width > 0 && task.Probe.Height > 0;
        var imageQuality = task.ConversionMode switch
        {
            "快速" => 5,
            "高质量" => 2,
            _ => 3
        };
        var webpQuality = task.ConversionMode switch
        {
            "快速" => "75",
            "高质量" => "95",
            _ => "85"
        };
        var avifQuality = task.ConversionMode switch
        {
            "快速" => "32",
            "高质量" => "20",
            _ => "28"
        };
        if (flattenAlpha)
        {
            var pixelFormat = task.TargetFormat == "JPG" ? "yuvj420p" : "rgb24";
            var filter = $"[0:v]format=rgba[fg];color=white:s={task.Probe.Width}x{task.Probe.Height}[bg];[bg][fg]overlay=shortest=1,format={pixelFormat}[outv]";
            arguments.AddRange(["-filter_complex", filter, "-map", "[outv]"]);
        }
        else
        {
            arguments.AddRange(["-map", "0:v:0"]);
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
            case "AVIF": arguments.AddRange(["-frames:v", "1", "-c:v", "libaom-av1", "-still-picture", "1", "-crf", avifQuality, "-cpu-used", task.ConversionMode == "高质量" ? "4" : "6"]); break;
        }
    }

    private static void BuildVideo(MediaTask task, List<string> arguments, EncoderBackend backend, bool hardwareDecode)
    {
        arguments.AddRange(["-map", "0:v:0", "-map", "0:a?", "-map_metadata", "0", "-map_chapters", "0"]);

        if (task.TargetFormat == "MKV")
        {
            arguments.AddRange(["-map", "0:s?", "-map", "0:t?", "-c:s", "copy", "-c:t", "copy"]);
        }
        else if (task.TargetFormat is "MP4" or "MOV" && task.Probe.SubtitleCodecs.Count > 0 && task.Probe.SubtitleCodecs.All(TextSubtitleCodecs.Contains))
        {
            arguments.AddRange(["-map", "0:s?", "-c:s", "mov_text"]);
        }

        if (task.Probe.IsHdr && task.TargetFormat is "AVI" or "FLV")
        {
            arguments.AddRange(["-vf", "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p"]);
        }

        if (task.TargetFormat == "AVI")
        {
            arguments.AddRange(["-c:v", "mpeg4", "-q:v", "4", "-c:a", "libmp3lame", "-b:a", "192k"]);
            return;
        }

        var hdrCapableTarget = task.Probe.IsHdr && task.TargetFormat is "MP4" or "MKV" or "MOV";
        AddVideoEncoder(arguments, backend, hdrCapableTarget, hardwareDecode, task.ConversionMode);
        AddColorMetadata(arguments, task.Probe);
        if (task.Probe.AudioTracks.Count > 0)
        {
            arguments.AddRange(["-c:a", "aac", "-b:a", "192k"]);
        }
        arguments.AddRange(["-max_muxing_queue_size", "4096", "-fps_mode", "passthrough"]);
        if (task.TargetFormat == "MP4")
        {
            arguments.AddRange(["-movflags", "+faststart"]);
        }
    }

    private static void AddVideoEncoder(List<string> arguments, EncoderBackend backend, bool hdr, bool hardwareDecode, string mode)
    {
        var quality = mode switch
        {
            "快速" => (Preset: "p1", Quality: "28"),
            "高质量" => (Preset: "p6", Quality: "18"),
            _ => (Preset: "p4", Quality: "23")
        };

        if (hdr)
        {
            switch (backend)
            {
                case EncoderBackend.Nvidia:
                    arguments.AddRange(["-c:v", "hevc_nvenc", "-preset", quality.Preset, "-tune", "hq", "-rc", "vbr", "-cq", quality.Quality, "-b:v", "0", "-multipass", "disabled", "-spatial-aq", "1", "-temporal-aq", "0", "-aq-strength", "8", "-rc-lookahead", "0", "-bf", "3", "-b_ref_mode", "middle", "-profile:v", "main10"]);
                    if (!hardwareDecode) arguments.AddRange(["-pix_fmt", "p010le"]);
                    break;
                case EncoderBackend.Intel:
                    arguments.AddRange(["-c:v", "hevc_qsv", "-preset", mode == "高质量" ? "slow" : mode == "快速" ? "veryfast" : "fast", "-global_quality", quality.Quality, "-async_depth", "8", "-adaptive_i", "1", "-adaptive_b", "1", "-profile:v", "main10"]);
                    if (!hardwareDecode) arguments.AddRange(["-pix_fmt", "p010le"]);
                    break;
                case EncoderBackend.Amd:
                    AddAmdEncoder(arguments, true, mode);
                    break;
                default:
                    arguments.AddRange(["-c:v", "libx265", "-preset", mode == "高质量" ? "slow" : mode == "快速" ? "veryfast" : "fast", "-crf", mode == "高质量" ? "20" : mode == "快速" ? "30" : "24", "-pix_fmt", "yuv420p10le"]);
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
                arguments.AddRange(["-c:v", "h264_qsv", "-preset", mode == "高质量" ? "slow" : mode == "快速" ? "veryfast" : "fast", "-global_quality", quality.Quality, "-async_depth", "8", "-adaptive_i", "1", "-adaptive_b", "1"]);
                if (!hardwareDecode) arguments.AddRange(["-pix_fmt", "nv12"]);
                break;
            case EncoderBackend.Amd:
                AddAmdEncoder(arguments, false, mode);
                break;
            default:
                arguments.AddRange(["-c:v", "libx264", "-preset", mode == "高质量" ? "slow" : mode == "快速" ? "veryfast" : "fast", "-crf", mode == "高质量" ? "18" : mode == "快速" ? "28" : "23", "-pix_fmt", "yuv420p"]);
                break;
        }
    }

    private static void AddAmdEncoder(List<string> arguments, bool hdr, string mode)
    {
        var quality = mode == "快速" ? "speed" : "quality";
        var qp = mode == "高质量" ? "20" : mode == "快速" ? "28" : "23";
        arguments.AddRange(["-c:v", hdr ? "hevc_amf" : "h264_amf", "-usage", "transcoding", "-quality", quality, "-rc", "cqp", "-qp_i", qp, "-qp_p", qp]);
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

}
