use crate::{Failure, Result};
use serde::{Deserialize, Serialize};
use std::io::{self, Read, Write};

mod remux;
mod session;
pub(crate) use session::session;
mod verification;
mod video;

#[derive(Deserialize)]
#[serde(
    tag = "action",
    rename_all = "camelCase",
    rename_all_fields = "camelCase"
)]
enum Request {
    Encode(Box<Encode>),
    Icon {
        input: String,
        output: String,
        size: i32,
    },
    Compatibility {
        input: String,
        output: String,
        maps: Vec<String>,
        audio_output: bool,
        image_output: bool,
    },
    Budget(verification::Budget),
    Validate(verification::Validation),
    Remux {
        container: String,
        streams: Vec<remux::Stream>,
    },
    MatchStreams {
        expected: Vec<verification::Stream>,
        actual: Vec<verification::Stream>,
    },
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Encode {
    input: String,
    output: String,
    source_kind: String,
    target: String,
    target_kind: String,
    container: String,
    codec: Option<String>,
    mode: String,
    backend: String,
    hardware_decode: bool,
    threads: i32,
    width: i32,
    height: i32,
    hdr: bool,
    animated: bool,
    animation_loops: Option<i32>,
    color_space: String,
    color_transfer: String,
    color_primaries: String,
    color_range: String,
    subtitles: Vec<String>,
    compatible_audio: bool,
    selected_audio: i32,
    audio_tracks: Vec<Track>,
    options: Options,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Track {
    map: String,
    channels: i32,
    layout: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Options {
    size_limit: bool,
    video_bitrate: Option<i32>,
    audio_bitrate: Option<i32>,
    channels: Option<i32>,
    sample_rate: Option<i32>,
    image_quality: Option<i32>,
    max_width: Option<i32>,
    max_height: Option<i32>,
    frame_rate: Option<String>,
}

#[derive(Default, Serialize)]
#[serde(transparent)]
struct Args(Vec<String>);

impl Args {
    fn add<const N: usize>(&mut self, args: [&str; N]) {
        self.0.extend(args.map(str::to_owned));
    }

    // Options are only rewritten in the output section. A source filename is opaque,
    // even if it has the same spelling as an option.
    fn output_offset(&self) -> usize {
        self.0.iter().position(|a| a == "-i").map_or(0, |i| i + 2)
    }
    fn position(&self, key: &str) -> Option<usize> {
        let start = self.output_offset();
        self.0[start..]
            .iter()
            .position(|v| v == key)
            .map(|i| start + i)
    }
    fn remove(&mut self, key: &str) {
        while let Some(i) = self.position(key) {
            self.0.drain(i..i + 2);
        }
    }
    fn set(&mut self, key: &str, value: &str) {
        self.remove(key);
        self.add([key, value]);
    }
    fn filter(&mut self, filter: &str, prepend: bool) {
        if let Some(i) = self.position("-vf") {
            let old = &self.0[i + 1];
            self.0[i + 1] = if prepend {
                format!("{filter},{old}")
            } else {
                format!("{old},{filter}")
            };
        } else {
            self.add(["-vf", filter]);
        }
    }
}

impl Encode {
    fn quality<'a>(&self, fast: &'a str, balanced: &'a str, quality: &'a str) -> &'a str {
        match self.mode.as_str() {
            "fast" => fast,
            "quality" => quality,
            _ => balanced,
        }
    }

    fn args(&mut self) -> Args {
        let mut args = Args::default();
        let ycgco = self.source_kind == "Video" && self.color_space == "ycgco";
        if self.options.size_limit {
            self.backend = "Cpu".into();
            self.hardware_decode = false;
        }
        if ycgco || self.has_dimensions() || self.options.frame_rate.is_some() {
            self.hardware_decode = false;
        }
        args.add([
            "-hide_banner",
            "-loglevel",
            "warning",
            "-nostdin",
            "-y",
            "-stats_period",
            "0.5",
        ]);
        if self.options.size_limit {
            args.add(["-xerror", "-err_detect", "explode"]);
        }
        if self.input.to_ascii_lowercase().ends_with(".m4s") {
            args.add(["-fflags", "+genpts"]);
        }
        if self.hardware_decode {
            let formats = match self.backend.as_str() {
                "Nvidia" => Some(("cuda", "cuda")),
                "Intel" => Some(("qsv", "qsv")),
                "MediaFoundation" => Some(("d3d11va", "d3d11")),
                _ => None,
            };
            if let Some((device, pixel)) = formats {
                args.add([
                    "-hwaccel",
                    device,
                    "-hwaccel_output_format",
                    pixel,
                    "-extra_hw_frames",
                    "8",
                ]);
            }
        }
        args.add(["-i", &self.input]);
        if let Some(loops) = self.animation_loops {
            args.add(["-map", "0:V:0", "-an", "-fps_mode", "passthrough"]);
            if self.target == "webp" {
                args.add([
                    "-c:v",
                    "libwebp_anim",
                    "-lossless",
                    if self.mode == "lossless" { "1" } else { "0" },
                    "-quality",
                    self.quality("75", "85", "95"),
                    "-loop",
                    &loops.to_string(),
                ]);
            } else {
                let repeat = match loops {
                    0 => 0,
                    1 => -1,
                    n => n - 1,
                };
                args.add(["-c:v", "gif", "-loop", &repeat.to_string()]);
            }
        } else if self.mode == "lossless" && self.codec.is_none() && self.target != "frames" {
            self.lossless(&mut args);
        } else if self.target == "frames" {
            args.add([
                "-map",
                "0:V:0",
                "-an",
                "-vf",
                &format!("fps={}", self.quality("8", "15", "30")),
                "-fps_mode",
                "passthrough",
                "-start_number",
                "1",
                "-c:v",
                "png",
            ]);
        } else if self.target_kind == "Audio" {
            self.audio(&mut args);
        } else if self.source_kind == "Image" || self.target_kind == "Image" {
            if self.source_kind == "Video" && self.target == "gif" {
                args.add([
                    "-map",
                    "0:V:0",
                    "-an",
                    "-vf",
                    &format!(
                        "fps={},scale=720:-1:flags=lanczos",
                        self.quality("8", "12", "15")
                    ),
                    "-c:v",
                    "gif",
                    "-loop",
                    "0",
                ]);
            } else {
                self.image(&mut args);
            }
        } else {
            self.video(&mut args);
            if ycgco {
                args.filter(
                    "zscale=matrixin=ycgco:matrix=bt709,format=yuv444p16le",
                    true,
                );
                args.set("-colorspace", "bt709");
            }
        }
        self.custom(&mut args);
        if self.backend == "Cpu" && self.threads > 0 {
            let n = self.threads.to_string();
            args.add([
                "-threads",
                &n,
                "-filter_threads",
                &n,
                "-filter_complex_threads",
                &n,
            ]);
        }
        args.add(["-progress", "pipe:1", "-nostats", &self.output]);
        args
    }

    fn lossless(&self, a: &mut Args) {
        if self.source_kind == "Image" {
            a.add(["-map", "0:V:0", "-frames:v", "1"]);
            match self.target.as_str() {
                "jpg" => a.add(["-c:v", "ljpeg"]),
                "png" => a.add(["-c:v", "png"]),
                "bmp" => a.add(["-c:v", "bmp"]),
                "webp" => a.add(["-c:v", "libwebp", "-lossless", "1"]),
                "avif" => a.add([
                    "-c:v",
                    "libaom-av1",
                    "-still-picture",
                    "1",
                    "-crf",
                    "0",
                    "-cpu-used",
                    "6",
                ]),
                "gif" => a.add(["-c:v", "gif"]),
                "tiff" => a.add(["-c:v", "tiff"]),
                "tga" => a.add(["-c:v", "targa"]),
                _ => {}
            }
        } else if self.target_kind == "Audio" {
            a.add([
                "-map",
                &format!("0:a:{}", self.selected_audio),
                "-vn",
                "-c:a",
                "copy",
                "-map_metadata",
                "0",
            ]);
        } else {
            a.add([
                "-map",
                "0",
                "-c",
                "copy",
                "-map_metadata",
                "0",
                "-map_chapters",
                "0",
            ]);
        }
    }

    fn audio(&self, a: &mut Args) {
        let index_map = format!("0:a:{}", self.selected_audio);
        let track = if self.compatible_audio {
            self.audio_tracks.first()
        } else {
            self.audio_tracks.get(self.selected_audio as usize)
        };
        let map = if self.compatible_audio {
            track.map_or(index_map.as_str(), |t| t.map.as_str())
        } else {
            &index_map
        };
        a.add(["-map", map, "-vn", "-map_metadata", "0"]);
        let bitrate = self.quality("128k", "192k", "320k");
        match self.target.as_str() {
            "mp3" => a.add(["-c:a", "libmp3lame", "-b:a", bitrate]),
            "wma" => a.add(["-c:a", "wmav2", "-b:a", bitrate]),
            "m4a" | "aac" => a.add(["-c:a", "aac", "-b:a", bitrate]),
            "flac" => a.add(["-c:a", "flac"]),
            "wav" => a.add(["-c:a", "pcm_s16le"]),
            "aiff" => a.add(["-c:a", "pcm_s16be"]),
            "alac" => a.add(["-c:a", "alac"]),
            "opus" => {
                a.add([
                    "-c:a",
                    "libopus",
                    "-b:a",
                    self.quality("128k", "128k", "160k"),
                ]);
                if let Some(t) = track {
                    t.opus_layout(a, 0);
                }
            }
            "vorbis" | "ogg" => a.add(["-c:a", "libvorbis", "-q:a", self.quality("3", "5", "6")]),
            "ac3" => a.add(["-c:a", "ac3", "-b:a", bitrate]),
            "eac3" => a.add(["-c:a", "eac3", "-b:a", self.quality("384k", "384k", "640k")]),
            _ => {}
        }
    }

    fn image(&self, a: &mut Args) {
        if matches!(self.target.as_str(), "jpg" | "bmp") && self.width > 0 && self.height > 0 {
            let pix = if self.target == "jpg" {
                "yuvj420p"
            } else {
                "rgb24"
            };
            a.add(["-filter_complex",&format!("[0:V:0]format=rgba[fg];color=white:s={}x{}[bg];[bg][fg]overlay=shortest=1,format={pix}[outv]",self.width,self.height),"-map","[outv]"]);
        } else {
            a.add(["-map", "0:V:0"]);
        }
        if self.animated && matches!(self.target.as_str(), "gif" | "webp") {
            if self.target == "gif" {
                a.add(["-c:v", "gif", "-loop", "0"]);
            } else {
                a.add(["-c:v", "libwebp_anim", "-quality", "85", "-loop", "0"]);
            }
            return;
        }
        match self.target.as_str() {
            "jpg" => a.add([
                "-frames:v",
                "1",
                "-c:v",
                "mjpeg",
                "-q:v",
                self.quality("5", "3", "2"),
            ]),
            "png" | "bmp" | "gif" | "tiff" => a.add(["-frames:v", "1", "-c:v", &self.target]),
            "webp" => a.add([
                "-frames:v",
                "1",
                "-c:v",
                "libwebp",
                "-quality",
                self.quality("75", "85", "95"),
            ]),
            "avif" => a.add([
                "-frames:v",
                "1",
                "-c:v",
                "libaom-av1",
                "-still-picture",
                "1",
                "-crf",
                self.quality("32", "28", "20"),
                "-cpu-used",
                self.quality("6", "6", "4"),
            ]),
            "tga" => a.add(["-frames:v", "1", "-c:v", "targa"]),
            _ => {}
        }
    }

    fn has_dimensions(&self) -> bool {
        self.options.max_width.is_some() || self.options.max_height.is_some()
    }

    fn custom(&self, a: &mut Args) {
        let o = &self.options;
        if let Some(rate) = o.video_bitrate {
            for key in ["-crf", "-q:v", "-cq", "-global_quality", "-qp_i", "-qp_p"] {
                a.remove(key);
            }
            a.set("-b:v", &format!("{rate}k"));
            match self.backend.as_str() {
                "Nvidia" => a.set("-rc", "vbr"),
                "Amd" => a.set("-rc", "vbr_peak"),
                "MediaFoundation" => {
                    a.remove("-quality");
                    a.set("-rate_control", "cbr");
                }
                _ => {}
            }
        }
        if let Some(rate) = o.audio_bitrate {
            a.remove("-q:a");
            a.set("-b:a", &format!("{rate}k"));
        }
        if let Some(rate) = o.sample_rate {
            a.set("-ar", &rate.to_string());
        }
        if let Some(n) = o.channels {
            a.set("-ac", &n.to_string());
        }
        if let Some(q) = o.image_quality {
            let q = f64::from(q);
            match self.target.as_str() {
                "jpg" => a.set(
                    "-q:v",
                    &(31.0 - (q * 29.0 / 100.0).round_ties_even())
                        .clamp(2.0, 31.0)
                        .to_string(),
                ),
                "webp" => a.set("-quality", &q.to_string()),
                "avif" => a.set(
                    "-crf",
                    &((100.0 - q) * 63.0 / 100.0)
                        .round_ties_even()
                        .clamp(0.0, 63.0)
                        .to_string(),
                ),
                _ => {}
            }
        }
        if self.has_dimensions() {
            let scale = if self.target_kind == "Video" {
                let w = o.max_width.map_or_else(|| "iw".into(), |v| v.to_string());
                let h = o.max_height.map_or_else(|| "ih".into(), |v| v.to_string());
                format!(
                    "scale=w='min(iw,{w})':h='min(ih,{h})':force_original_aspect_ratio=decrease:force_divisible_by=2:flags=lanczos"
                )
            } else {
                let (w, h) =
                    verification::dimensions(self.width, self.height, o.max_width, o.max_height);
                format!("scale={w}:{h}:flags=lanczos")
            };
            if let Some(i) = a.position("-filter_complex") {
                a.0[i + 1] =
                    a.0[i + 1].replace("[outv]", &format!("[unscaled];[unscaled]{scale}[outv]"));
            } else {
                a.filter(&scale, false);
            }
        }
        if let Some(rate) = &o.frame_rate {
            a.filter(&format!("fps={rate}"), false);
            a.set("-fps_mode", "cfr");
        }
    }
}

impl Track {
    fn opus_layout(&self, a: &mut Args, index: usize) {
        if self.channels == 6 && self.layout.eq_ignore_ascii_case("5.1(side)") {
            a.add([
                &format!("-filter:a:{index}"),
                "channelmap=map=0|1|2|3|4|5:channel_layout=5.1",
            ]);
        }
    }
}

fn respond(value: &impl Serialize) -> Result<()> {
    let mut output = io::stdout().lock();
    serde_json::to_writer(&mut output, value).map_err(|_| Failure::from("media-plan-output"))?;
    output.flush()?;
    Ok(())
}

pub(crate) fn run() -> Result<()> {
    const MAX: u64 = 8 * 1024 * 1024;
    let mut data = Vec::new();
    io::stdin().lock().take(MAX + 1).read_to_end(&mut data)?;
    if data.len() as u64 > MAX {
        return Err("media-plan-too-large".into());
    }
    let request: Request =
        serde_json::from_slice(&data).map_err(|_| Failure::from("media-plan-request"))?;
    match request {
        Request::Encode(mut request) => respond(&request.args()),
        Request::Icon {
            input,
            output,
            size,
        } => {
            let filter = format!(
                "scale={size}:{size}:force_original_aspect_ratio=decrease,pad={size}:{size}:(ow-iw)/2:(oh-ih)/2:color=0x00000000"
            );
            respond(&[
                "-hide_banner",
                "-y",
                "-i",
                &input,
                "-vf",
                &filter,
                "-frames:v",
                "1",
                "-c:v",
                "png",
                "-progress",
                "pipe:1",
                "-nostats",
                &output,
            ])
        }
        Request::Compatibility {
            input,
            output,
            maps,
            audio_output,
            image_output,
        } => {
            let mut args = Args::default();
            args.add([
                "-hide_banner",
                "-loglevel",
                "warning",
                "-nostdin",
                "-y",
                "-xerror",
                "-err_detect",
                "explode",
                "-i",
                &input,
            ]);
            for map in maps {
                args.add(["-map", &map]);
            }
            if !audio_output {
                args.add(["-c:v", "ffv1", "-level", "3", "-g", "1"]);
                if !image_output {
                    args.add([
                        "-map", "0:s?", "-map", "0:t?", "-c:s", "copy", "-c:t", "copy",
                    ]);
                }
            }
            args.add([
                "-c:a",
                "pcm_s32le",
                "-map_metadata",
                "0",
                "-map_chapters",
                "0",
                "-threads",
                "1",
                "-filter_threads",
                "1",
                "-filter_complex_threads",
                "1",
                "-progress",
                "pipe:1",
                "-nostats",
                "-f",
                "matroska",
                &output,
            ]);
            respond(&args)
        }
        Request::Budget(request) => respond(&request.evaluate()),
        Request::Validate(request) => respond(&request.evaluate()),
        Request::Remux { container, streams } => respond(&remux::check(&container, &streams)),
        Request::MatchStreams { expected, actual } => {
            respond(&verification::match_streams(expected, actual))
        }
    }
}
