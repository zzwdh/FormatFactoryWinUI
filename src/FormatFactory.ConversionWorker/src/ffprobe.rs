use crate::{Failure, Result};
use serde::Serialize;
use serde_json::Value;
use std::io::{self, Read};
use std::str::FromStr;

#[derive(Default, Serialize)]
#[serde(rename_all = "PascalCase")]
struct Probe<'a> {
    width: i32,
    height: i32,
    video_stream_index: i32,
    video_codec: &'a str,
    video_codec_tag: &'a str,
    pixel_format: &'a str,
    color_space: &'a str,
    color_transfer: &'a str,
    color_primaries: &'a str,
    color_range: &'a str,
    is_hdr: bool,
    is_animated: bool,
    bitrate: u32,
    format_name: &'a str,
    audio_tracks: Vec<Audio<'a>>,
    subtitle_codecs: Vec<&'a str>,
}

#[derive(Serialize)]
#[serde(rename_all = "PascalCase")]
struct Audio<'a> {
    relative_index: usize,
    codec: &'a str,
    codec_tag: &'a str,
    stream_index: i32,
    channels: i32,
    channel_layout: &'a str,
    language: Option<&'a str>,
    title: &'a str,
}

#[derive(Default, Serialize)]
#[serde(rename_all = "PascalCase")]
struct Response<'a> {
    error: Option<&'static str>,
    probe: Option<Probe<'a>>,
    has_video: bool,
    duration_ticks: i64,
}

fn text<'a>(v: &'a Value, key: &str) -> &'a str {
    v.get(key).and_then(Value::as_str).unwrap_or("")
}
fn number<T: FromStr>(v: &Value, key: &str) -> Option<T> {
    match v.get(key)? {
        Value::String(s) => s.trim().parse().ok(),
        Value::Number(n) => n.to_string().parse().ok(),
        _ => None,
    }
}

fn duration(v: &Value) -> i64 {
    let seconds = text(v, "duration").trim().parse::<f64>().unwrap_or(0.0);
    // TimeSpan.FromSeconds(double) truncates fractional ticks and excludes infinities.
    if seconds > 0.0 && seconds < i64::MAX as f64 / 10_000_000.0 {
        (seconds * 10_000_000.0) as i64
    } else {
        0
    }
}

fn invalid(key: &'static str) -> Response<'static> {
    Response {
        error: Some(key),
        ..Default::default()
    }
}

fn parse(root: &Value) -> Response<'_> {
    if !root.is_object() {
        return invalid("root");
    }
    let Some(streams) = root
        .get("streams")
        .and_then(Value::as_array)
        .filter(|s| !s.is_empty())
    else {
        return invalid("empty");
    };
    let mut probe = Probe {
        video_stream_index: -1,
        ..Default::default()
    };
    let mut has_video = false;
    let mut ticks = 0;
    for (i, stream) in streams.iter().enumerate() {
        match text(stream, "codec_type") {
            "video" => {
                let d = &stream["disposition"];
                if has_video
                    || number::<i32>(d, "attached_pic").unwrap_or(0) != 0
                    || number::<i32>(d, "timed_thumbnails").unwrap_or(0) != 0
                {
                    continue;
                }
                has_video = true;
                probe.width = number(stream, "width").unwrap_or(0);
                probe.height = number(stream, "height").unwrap_or(0);
                probe.video_codec = text(stream, "codec_name");
                probe.video_codec_tag = text(stream, "codec_tag_string");
                probe.video_stream_index = number(stream, "index").unwrap_or(i as i32);
                probe.pixel_format = text(stream, "pix_fmt");
                probe.color_transfer = text(stream, "color_transfer");
                probe.color_primaries = text(stream, "color_primaries");
                probe.color_space = text(stream, "color_space");
                probe.color_range = text(stream, "color_range");
                probe.is_hdr = matches!(probe.color_transfer, "smpte2084" | "arib-std-b67");
                probe.is_animated = number::<i64>(stream, "nb_frames").unwrap_or(0) > 1;
            }
            "audio" => {
                let tags = &stream["tags"];
                probe.audio_tracks.push(Audio {
                    relative_index: probe.audio_tracks.len(),
                    codec: text(stream, "codec_name"),
                    codec_tag: text(stream, "codec_tag_string"),
                    stream_index: number(stream, "index").unwrap_or(i as i32),
                    channels: number(stream, "channels").unwrap_or(0),
                    channel_layout: text(stream, "channel_layout"),
                    language: tags.get("language").and_then(Value::as_str),
                    title: text(tags, "title"),
                });
            }
            "subtitle" => {
                probe.subtitle_codecs.push(text(stream, "codec_name"));
                continue;
            }
            _ => continue,
        }
        ticks = ticks.max(duration(stream));
        probe.bitrate = probe.bitrate.max(number(stream, "bit_rate").unwrap_or(0));
    }
    if !has_video && probe.audio_tracks.is_empty() {
        return invalid("kind");
    }
    if let Some(format) = root.get("format") {
        probe.format_name = text(format, "format_name");
        ticks = ticks.max(duration(format));
        if let Some(rate) = number::<u32>(format, "bit_rate").filter(|&v| v > 0) {
            probe.bitrate = rate;
        }
    }
    Response {
        error: None,
        probe: Some(probe),
        has_video,
        duration_ticks: ticks,
    }
}

fn within_depth(root: &Value, parents: u32) -> bool {
    match root {
        Value::Array(items) => parents < 64 && items.iter().all(|v| within_depth(v, parents + 1)),
        Value::Object(items) => {
            parents < 64 && items.values().all(|v| within_depth(v, parents + 1))
        }
        _ => true,
    }
}

pub(crate) fn run() -> Result<()> {
    // Same parsed-output cap as ProcessRunner. Raw input avoids double encoding ffprobe's JSON.
    const MAX: u64 = 16 * 1024 * 1024;
    let mut bytes = Vec::new();
    io::stdin().lock().take(MAX + 1).read_to_end(&mut bytes)?;
    let json = if bytes.len() as u64 <= MAX {
        serde_json::from_slice::<Value>(&bytes).ok()
    } else {
        None
    };
    let response = match &json {
        Some(root) if within_depth(root, 0) => parse(root),
        _ => invalid("json"),
    };
    serde_json::to_writer(io::stdout().lock(), &response).map_err(|_| Failure::from("probe-output"))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn ignores_timed_thumbnails_and_keeps_absolute_audio_indices() {
        let root = serde_json::json!({"streams":[{"codec_type":"video","index":1,"disposition":{"timed_thumbnails":"1"}},
            {"codec_type":"audio","index":3,"duration":"0.00000099","bit_rate":"4294967296"}]});
        let result = parse(&root);
        assert!(!result.has_video);
        assert_eq!(result.duration_ticks, 9);
        let probe = result.probe.unwrap();
        assert_eq!(probe.bitrate, 0);
        assert_eq!(probe.audio_tracks[0].stream_index, 3);
    }
}
