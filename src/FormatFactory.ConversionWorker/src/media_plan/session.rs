use super::{Args, Encode, verification::Budget};
use crate::{Failure, Result};
use serde::{Deserialize, Serialize, de::DeserializeOwned};
use std::io::{self, BufRead, Read, Write};
use std::path::Path;

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Begin {
    plan: Encode,
    available: [bool; 4],
    decode_available: bool,
    source_codec: String,
    budget: Option<Budget>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Outcome {
    exit_code: i32,
}

#[derive(Serialize)]
#[serde(
    tag = "event",
    rename_all = "camelCase",
    rename_all_fields = "camelCase"
)]
enum Event<'a> {
    Image {
        quality: i32,
        attempt: i32,
    },
    Capture,
    Run {
        arguments: Args,
        backend: &'a str,
        hardware_decode: bool,
        number: u32,
        size_attempt: i32,
        size_attempts: i32,
    },
    Fallback {
        backend: &'a str,
    },
    Measured {
        size_attempt: i32,
        limit: i64,
        actual: i64,
        video_bitrate: Option<i32>,
        audio_bitrate: Option<i32>,
    },
    Error {
        key: &'a str,
    },
    Failed,
    Complete,
}

fn read<T: DeserializeOwned>(input: &mut impl BufRead) -> Result<T> {
    const LIMIT: u64 = 8 * 1024 * 1024;
    let mut bytes = Vec::new();
    input.take(LIMIT + 1).read_until(b'\n', &mut bytes)?;
    if bytes.len() as u64 > LIMIT || bytes.last() != Some(&b'\n') {
        return Err("media-session-request".into());
    }
    serde_json::from_slice(&bytes).map_err(|_| "media-session-request".into())
}

fn emit(output: &mut impl Write, event: Event<'_>) -> Result<()> {
    serde_json::to_writer(&mut *output, &event)
        .map_err(|_| Failure::from("media-session-output"))?;
    output.write_all(b"\n")?;
    output.flush()?;
    Ok(())
}

fn attempts(available: [bool; 4], decode: bool) -> Vec<(&'static str, bool)> {
    let mut result = Vec::new();
    for (backend, enabled) in ["Nvidia", "Intel", "Amd", "MediaFoundation"]
        .into_iter()
        .zip(available)
    {
        if enabled {
            if decode && backend != "Amd" {
                result.push((backend, true));
            }
            result.push((backend, false));
        }
    }
    result.push(("Cpu", false));
    result
}

fn output_size(plan: &Encode) -> Result<Option<i64>> {
    let path = Path::new(&plan.output);
    if plan.target == "frames" {
        let Some(dir) = path.parent() else {
            return Ok(None);
        };
        if !dir.is_dir() {
            return Ok(None);
        }
        for entry in dir.read_dir()? {
            let entry = entry?;
            let name = entry.file_name().to_string_lossy().to_ascii_lowercase();
            if name.starts_with("frame-") && name.ends_with(".png") && entry.path().is_file() {
                return Ok(Some(0));
            }
        }
        return Ok(None);
    }
    match std::fs::metadata(path) {
        Ok(info) if info.is_file() => Ok(Some(
            i64::try_from(info.len()).map_err(|_| Failure::from("media-output-size"))?,
        )),
        Ok(_) => Ok(None),
        Err(e) if e.kind() == io::ErrorKind::NotFound => Ok(None),
        Err(e) => Err(e.into()),
    }
}

#[derive(Deserialize)]
#[serde(
    tag = "mode",
    rename_all = "camelCase",
    rename_all_fields = "camelCase"
)]
enum Start {
    Encode(Box<Begin>),
    ImageSize {
        path: String,
        limit: u64,
        initial_quality: i32,
    },
}

fn image_search(
    input: &mut impl BufRead,
    output: &mut impl Write,
    path: &Path,
    limit: u64,
    initial: i32,
) -> Result<()> {
    if !(1..=100).contains(&initial) || limit == 0 {
        return Err("media-session-request".into());
    }
    let (mut low, mut high, mut quality) = (1, initial, initial);
    let mut found = false;
    for attempt in 1..=9 {
        if high < low {
            break;
        }
        emit(output, Event::Image { quality, attempt })?;
        let _: Outcome = read(input)?;
        let size = match std::fs::metadata(path) {
            Ok(info) if info.is_file() => info.len(),
            Ok(_) => 0,
            Err(e) if e.kind() == io::ErrorKind::NotFound => 0,
            Err(e) => return Err(e.into()),
        };
        if size == 0 {
            return emit(
                output,
                Event::Error {
                    key: "NativeImageValidationFailed",
                },
            );
        }
        if size <= limit {
            found = true;
            emit(output, Event::Capture)?;
            if quality == initial {
                break;
            }
            low = quality + 1;
        } else {
            high = quality - 1;
        }
        quality = low + (high - low) / 2;
    }
    emit(
        output,
        if found {
            Event::Complete
        } else {
            Event::Error {
                key: "MediaSizeCannotMeetTarget",
            }
        },
    )
}

// The worker owns the entire sequence. The host runs each supplied argv under its
// pausable job handle and acknowledges it only after both engine pipes have drained.
pub(crate) fn session() -> Result<()> {
    let mut input = io::stdin().lock();
    let mut output = io::stdout().lock();
    let request = read(&mut input)?;
    let plan = match request {
        Start::Encode(plan) => plan,
        Start::ImageSize {
            path,
            limit,
            initial_quality,
        } => {
            return image_search(
                &mut input,
                &mut output,
                Path::new(&path),
                limit,
                initial_quality,
            );
        }
    };
    let Begin {
        mut plan,
        available,
        decode_available,
        source_codec,
        mut budget,
    } = *plan;
    let decode = decode_available
        && !plan.has_dimensions()
        && plan.options.frame_rate.is_none()
        && plan.color_space != "ycgco"
        && !(plan.hdr && matches!(plan.container.as_str(), "AVI" | "FLV"))
        && matches!(
            source_codec.as_str(),
            "h264" | "hevc" | "av1" | "vp9" | "vp8" | "mpeg2video" | "vc1" | "mjpeg"
        );
    let attempts = attempts(available, decode);
    let size_attempts = if budget.is_some() { 3 } else { 1 };
    let mut number = 0;
    for size_attempt in 1..=size_attempts {
        if let Some(b) = &mut budget {
            let rates = b.evaluate();
            if let Some(key) = rates.error {
                return emit(&mut output, Event::Error { key });
            }
            b.audio_bitrate = rates.audio_bitrate;
            b.video_bitrate = rates.video_bitrate;
            plan.options.audio_bitrate = rates.audio_bitrate;
            plan.options.video_bitrate = rates.video_bitrate;
        }
        let mut completed = None;
        for &(backend, hardware_decode) in &attempts {
            plan.backend = backend.into();
            plan.hardware_decode = hardware_decode;
            number += 1;
            let arguments = plan.args();
            emit(
                &mut output,
                Event::Run {
                    arguments,
                    backend,
                    hardware_decode,
                    number,
                    size_attempt,
                    size_attempts,
                },
            )?;
            let result: Outcome = read(&mut input)?;
            if result.exit_code == 0 {
                completed = output_size(&plan)?;
                if completed.is_some() {
                    break;
                }
            }
            if backend != "Cpu" && !hardware_decode {
                emit(&mut output, Event::Fallback { backend })?;
            }
        }
        let Some(actual) = completed else {
            return emit(&mut output, Event::Failed);
        };
        let Some(b) = &mut budget else {
            return emit(&mut output, Event::Complete);
        };
        emit(
            &mut output,
            Event::Measured {
                size_attempt,
                limit: b.limit,
                actual,
                video_bitrate: b.video_bitrate,
                audio_bitrate: b.audio_bitrate,
            },
        )?;
        if actual <= b.limit {
            return emit(&mut output, Event::Complete);
        }
        b.measured = Some(actual);
    }
    emit(
        &mut output,
        Event::Error {
            key: "MediaSizeCannotMeetTarget",
        },
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn hardware_falls_back_to_cpu_decode_before_switching_encoder() {
        assert_eq!(
            attempts([true, false, true, true], true),
            [
                ("Nvidia", true),
                ("Nvidia", false),
                ("Amd", false),
                ("MediaFoundation", true),
                ("MediaFoundation", false),
                ("Cpu", false)
            ]
        );
        assert_eq!(
            attempts([false, true, false, false], false),
            [("Intel", false), ("Cpu", false)]
        );
    }
}
