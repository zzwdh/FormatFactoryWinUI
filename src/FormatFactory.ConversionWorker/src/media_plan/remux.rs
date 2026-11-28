use serde::Deserialize;

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub(super) struct Stream {
    #[serde(rename = "type")]
    kind: String,
    codec: String,
    attached_picture: bool,
    channels: i32,
    sample_rate: i32,
}

fn little_pcm(codec: &str) -> bool {
    matches!(
        codec,
        "pcm_u8"
            | "pcm_s16le"
            | "pcm_s24le"
            | "pcm_s32le"
            | "pcm_f32le"
            | "pcm_f64le"
            | "pcm_alaw"
            | "pcm_mulaw"
    )
}

fn big_pcm(codec: &str) -> bool {
    matches!(
        codec,
        "pcm_s8" | "pcm_s16be" | "pcm_s24be" | "pcm_s32be" | "pcm_f32be" | "pcm_f64be"
    )
}

fn supported(container: &str, s: &Stream) -> bool {
    let c = s.codec.as_str();
    match (s.kind.as_str(), container) {
        ("video", "mp4") => {
            matches!(c, "h264" | "hevc" | "av1" | "vp9" | "mpeg4")
                || s.attached_picture && matches!(c, "mjpeg" | "png")
        }
        ("video", "mov") => matches!(
            c,
            "h264"
                | "hevc"
                | "av1"
                | "mpeg4"
                | "mpeg2video"
                | "mjpeg"
                | "prores"
                | "qtrle"
                | "dvvideo"
                | "png"
        ),
        ("video", "mkv") => matches!(
            c,
            "h264"
                | "hevc"
                | "av1"
                | "vp8"
                | "vp9"
                | "mpeg1video"
                | "mpeg2video"
                | "mpeg4"
                | "msmpeg4v3"
                | "wmv1"
                | "wmv2"
                | "wmv3"
                | "vc1"
                | "theora"
                | "ffv1"
                | "huffyuv"
                | "mjpeg"
                | "prores"
                | "dvvideo"
                | "png"
        ),
        ("video", "webm") => matches!(c, "vp8" | "vp9" | "av1"),
        ("video", "ts") => matches!(c, "mpeg1video" | "mpeg2video" | "h264" | "hevc" | "vc1"),
        ("video", "avi") => matches!(
            c,
            "h264"
                | "mpeg4"
                | "mjpeg"
                | "msmpeg4v3"
                | "wmv1"
                | "wmv2"
                | "ffv1"
                | "huffyuv"
                | "dvvideo"
        ),
        ("video", "flv") => matches!(c, "flv1" | "h264" | "vp6f" | "vp6a"),
        ("audio", "mp4") => matches!(c, "aac" | "alac" | "mp3" | "ac3" | "eac3"),
        ("audio", "m4a") => matches!(c, "aac" | "alac" | "ac3"),
        ("audio", "mov") => {
            matches!(c, "aac" | "alac" | "mp3" | "ac3" | "eac3") || little_pcm(c) || big_pcm(c)
        }
        ("audio", "mkv") => {
            matches!(
                c,
                "aac"
                    | "ac3"
                    | "eac3"
                    | "dts"
                    | "truehd"
                    | "mp1"
                    | "mp2"
                    | "mp3"
                    | "opus"
                    | "vorbis"
                    | "flac"
                    | "alac"
            ) || little_pcm(c)
                || big_pcm(c)
        }
        ("audio", "webm" | "ogg") => matches!(c, "opus" | "vorbis"),
        ("audio", "ts") => matches!(c, "mp2" | "mp3" | "aac" | "ac3" | "eac3" | "dts"),
        ("audio", "avi") => matches!(c, "mp2" | "mp3" | "ac3") || little_pcm(c),
        ("audio", "flv") => matches!(
            c,
            "aac" | "mp3" | "speex" | "pcm_s16le" | "pcm_u8" | "adpcm_swf" | "nellymoser"
        ),
        ("audio", "wav") => {
            little_pcm(c)
                || c == "adpcm_ima_wav" && matches!(s.channels, 1 | 2) && s.sample_rate > 0
        }
        ("audio", "aiff") => big_pcm(c),
        ("audio", "mp3" | "aac" | "opus" | "vorbis" | "flac" | "alac" | "ac3" | "eac3") => {
            c == container
        }
        ("audio", "wma") => matches!(c, "wmav1" | "wmav2" | "wmapro" | "wmavoice" | "wmalossless"),
        ("subtitle", "mp4" | "mov") => c == "mov_text",
        ("subtitle", "mkv") => matches!(
            c,
            "subrip"
                | "ass"
                | "ssa"
                | "webvtt"
                | "dvd_subtitle"
                | "hdmv_pgs_subtitle"
                | "dvb_subtitle"
        ),
        ("subtitle", "webm") => c == "webvtt",
        ("subtitle", "ts") => c == "dvb_subtitle",
        ("attachment", "mkv") => true,
        ("data", "ts") => matches!(c, "bin_data" | "klv"),
        _ => false,
    }
}

// First incompatible source stream is returned so the host can retain its existing
// localized message and diagnostic tags for that exact stream.
pub(super) fn check(container: &str, streams: &[Stream]) -> (&'static str, usize) {
    let mut seen_audio = 0;
    let mut seen_video = 0;
    for (index, s) in streams.iter().enumerate() {
        if s.kind.trim().is_empty() || s.codec.trim().is_empty() && s.kind != "attachment" {
            return ("unknown", index);
        }
        if !supported(container, s) {
            return ("unsupported", index);
        }
        seen_audio += usize::from(s.kind == "audio");
        seen_video += usize::from(s.kind == "video");
    }
    if container == "flv" && (seen_audio > 1 || seen_video > 1) {
        return ("multiple", 0);
    }
    ("ok", 0)
}
