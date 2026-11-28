use serde::{Deserialize, Serialize};

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub(super) struct Budget {
    target: String,
    audio_output: bool,
    modern_audio: bool,
    quality_mode: bool,
    duration_ticks: i64,
    pub(super) limit: i64,
    tracks: i32,
    pub(super) video_bitrate: Option<i32>,
    pub(super) audio_bitrate: Option<i32>,
    pub(super) measured: Option<i64>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub(super) struct Rates {
    pub(super) error: Option<&'static str>,
    pub(super) video_bitrate: Option<i32>,
    pub(super) audio_bitrate: Option<i32>,
}

impl Budget {
    pub(super) fn evaluate(&self) -> Rates {
        let error = |key| Rates {
            error: Some(key),
            video_bitrate: None,
            audio_bitrate: None,
        };
        let exhausted = || error("MediaSizeCannotMeetTarget");
        if let Some(size) = self.measured {
            if size <= 0 {
                return exhausted();
            }
            let ratio = (0.95 * self.limit as f64 / size as f64).min(0.9);
            if self.audio_output {
                let old = self.audio_bitrate.unwrap_or(0);
                let next = audio_rate(&self.target, (f64::from(old) * ratio).floor() as i32);
                if next < 8 || next >= old {
                    return exhausted();
                }
                return Rates {
                    error: None,
                    video_bitrate: self.video_bitrate,
                    audio_bitrate: Some(next),
                };
            }
            let old = self.video_bitrate.unwrap_or(0);
            let next = (f64::from(old) * ratio).floor() as i32;
            if next < 1 || next >= old {
                return exhausted();
            }
            return Rates {
                error: None,
                video_bitrate: Some(next),
                audio_bitrate: self.audio_bitrate,
            };
        }
        if self.duration_ticks <= 0 {
            return error("MediaSizeDurationRequired");
        }
        let duration = self.duration_ticks as f64 / 10_000_000.0;
        let total = (self.limit as f64 * 0.95 * 8.0 / duration / 1000.0).floor();
        if self.audio_output {
            let next = audio_rate(&self.target, total as i32);
            if next < 8 {
                return exhausted();
            }
            return Rates {
                error: None,
                video_bitrate: None,
                audio_bitrate: Some(next),
            };
        }
        let audio = self.audio_bitrate.unwrap_or(if self.modern_audio {
            if self.quality_mode { 160 } else { 128 }
        } else {
            192
        });
        let video = total - f64::from(audio) * f64::from(self.tracks);
        if video < 1.0 {
            return exhausted();
        }
        Rates {
            error: None,
            video_bitrate: Some(video.min(200000.0) as i32),
            audio_bitrate: (self.tracks > 0).then_some(audio),
        }
    }
}

fn audio_rate(target: &str, ceiling: i32) -> i32 {
    let rates: &[i32] = match target {
        "mp3" => &[
            8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320,
        ],
        "ac3" => &[
            32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 448, 512, 576, 640,
        ],
        "wma" => &[24, 32, 40, 48, 64, 80, 96, 128, 160, 192, 256, 320],
        _ => {
            return ceiling.clamp(
                0,
                match target {
                    "opus" => 512,
                    "eac3" => 640,
                    _ => 1536,
                },
            );
        }
    };
    rates
        .iter()
        .copied()
        .rev()
        .find(|v| *v <= ceiling)
        .unwrap_or(0)
}

pub(super) fn dimensions(w: i32, h: i32, max_w: Option<i32>, max_h: Option<i32>) -> (i32, i32) {
    let ratio = 1.0f64
        .min(max_w.map_or(1.0, |v| f64::from(v) / f64::from(w)))
        .min(max_h.map_or(1.0, |v| f64::from(v) / f64::from(h)));
    (
        (f64::from(w) * ratio).floor().max(1.0) as i32,
        (f64::from(h) * ratio).floor().max(1.0) as i32,
    )
}

#[derive(Deserialize)]
#[serde(
    tag = "check",
    rename_all = "camelCase",
    rename_all_fields = "camelCase"
)]
pub(super) enum Validation {
    Duration {
        source: i64,
        output: i64,
        frame_rate: Option<f64>,
    },
    Dimensions {
        source_width: i32,
        source_height: i32,
        max_width: Option<i32>,
        max_height: Option<i32>,
        width: i32,
        height: i32,
    },
    CompatibleAudio {
        supported: bool,
        audio_output: bool,
        expected_channels: i32,
        tracks: Vec<Audio>,
        has_video: bool,
    },
    LosslessImage {
        source_width: i32,
        source_height: i32,
        width: i32,
        height: i32,
        source_depth: i32,
        depth: i32,
        pages: i32,
    },
}

#[derive(Deserialize)]
pub(super) struct Audio {
    channels: i32,
    codec: String,
}

impl Validation {
    pub(super) fn evaluate(self) -> bool {
        match self {
            Self::Duration {
                source,
                output,
                frame_rate,
            } => {
                let tolerance = 0.25f64.max(frame_rate.map_or(0.0, |v| 1.0 / v));
                source > 0
                    && output > 0
                    && source.abs_diff(output) as f64 / 10_000_000.0 <= tolerance
            }
            Self::Dimensions {
                source_width,
                source_height,
                max_width,
                max_height,
                width,
                height,
            } => {
                let expected = if max_width.is_none() && max_height.is_none() {
                    (source_width, source_height)
                } else {
                    dimensions(source_width, source_height, max_width, max_height)
                };
                expected == (width, height)
            }
            Self::CompatibleAudio {
                supported,
                audio_output,
                expected_channels,
                tracks,
                has_video,
            } => {
                supported
                    && has_video != audio_output
                    && matches!(tracks.as_slice(), [t] if t.channels == expected_channels && !t.codec.eq_ignore_ascii_case("apple_apac"))
            }
            Self::LosslessImage {
                source_width,
                source_height,
                width,
                height,
                source_depth,
                depth,
                pages,
            } => {
                (source_width <= 0 || source_width == width)
                    && (source_height <= 0 || source_height == height)
                    && depth >= source_depth
                    && pages <= 1
            }
        }
    }
}

#[derive(Deserialize, Eq, PartialEq, Ord, PartialOrd)]
#[serde(rename_all = "camelCase")]
pub(super) struct Stream {
    #[serde(rename = "type")]
    kind: String,
    codec: String,
    width: i32,
    height: i32,
    sample_rate: i32,
    channels: i32,
}

pub(super) fn match_streams(mut expected: Vec<Stream>, mut actual: Vec<Stream>) -> bool {
    if expected.is_empty() || expected.len() != actual.len() {
        return false;
    }
    expected.sort_unstable();
    actual.sort_unstable();
    expected == actual
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn budget_and_duration_bounds() {
        assert_eq!(audio_rate("mp3", 159), 128);
        assert_eq!(audio_rate("wma", 23), 0);
        assert_eq!(audio_rate("opus", 9999), 512);
        assert!(
            !Validation::Duration {
                source: 1,
                output: 0,
                frame_rate: None
            }
            .evaluate()
        );
        assert!(
            Validation::Duration {
                source: 10_000_000,
                output: 14_999_999,
                frame_rate: Some(2.0)
            }
            .evaluate()
        );
        assert!(
            !Validation::Duration {
                source: 10_000_000,
                output: 15_000_001,
                frame_rate: Some(2.0)
            }
            .evaluate()
        );
    }
}
