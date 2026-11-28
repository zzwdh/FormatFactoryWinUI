use super::{Args, Encode};

const SDR: &str = "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p";

impl Encode {
    pub(super) fn video(&self, a: &mut Args) {
        a.add(["-map", "0:V:0"]);
        if self.compatible_audio {
            for track in &self.audio_tracks {
                a.add(["-map", &track.map]);
            }
        } else {
            a.add(["-map", "0:a?"]);
        }
        a.add(["-map_metadata", "0", "-map_chapters", "0"]);
        if self.backend == "MediaFoundation" && self.hardware_decode {
            a.add(["-vf", "scale_d3d11=iw:ih"]);
        }
        match self.container.as_str() {
            "MKV" => a.add([
                "-map", "0:s?", "-map", "0:t?", "-c:s", "copy", "-c:t", "copy",
            ]),
            "MP4" | "MOV"
                if !self.subtitles.is_empty()
                    && self.subtitles.iter().all(|s| {
                        matches!(
                            s.to_ascii_lowercase().as_str(),
                            "subrip" | "srt" | "ass" | "ssa" | "webvtt" | "mov_text" | "text"
                        )
                    }) =>
            {
                a.add(["-map", "0:s?", "-c:s", "mov_text"])
            }
            _ => {}
        }
        if self.hdr && matches!(self.container.as_str(), "AVI" | "FLV") {
            a.add(["-vf", SDR]);
        }
        if matches!(self.codec.as_deref(), Some("av1" | "vp9"))
            || self.codec.is_none() && self.container == "WEBM"
        {
            self.modern_video(a);
            return;
        }
        if self.container == "AVI" {
            a.add([
                "-c:v",
                "mpeg4",
                "-q:v",
                "4",
                "-c:a",
                "libmp3lame",
                "-b:a",
                "192k",
            ]);
            return;
        }
        let hdr = self.hdr && matches!(self.container.as_str(), "MP4" | "MKV" | "MOV");
        let hevc = self.codec.as_deref() == Some("hevc") || self.codec.is_none() && hdr;
        self.encoder(a, hevc, hdr);
        if self.backend == "Cpu"
            && !hevc
            && !self.has_dimensions()
            && self.width > 0
            && self.height > 0
            && (self.width % 2 != 0 || self.height % 2 != 0)
        {
            a.filter("pad=ceil(iw/2)*2:ceil(ih/2)*2:0:0", true);
        }
        if self.hdr && self.container == "FLV" {
            a.add([
                "-color_trc",
                "bt709",
                "-color_primaries",
                "bt709",
                "-colorspace",
                "bt709",
                "-color_range",
                "tv",
            ]);
        } else {
            for (key, value) in [
                ("-color_trc", &self.color_transfer),
                ("-color_primaries", &self.color_primaries),
                ("-colorspace", &self.color_space),
                ("-color_range", &self.color_range),
            ] {
                if !value.trim().is_empty()
                    && !matches!(value.as_str(), "unknown" | "unspecified" | "reserved")
                    && !(key == "-colorspace" && matches!(value.as_str(), "gbr" | "rgb"))
                {
                    a.add([key, value]);
                }
            }
        }
        if !self.audio_tracks.is_empty() {
            a.add(["-c:a", "aac", "-b:a", "192k"]);
        }
        a.add(["-max_muxing_queue_size", "4096", "-fps_mode", "passthrough"]);
        if self.container == "MP4" {
            a.add(["-movflags", "+faststart"]);
        }
    }

    fn modern_video(&self, a: &mut Args) {
        if self.codec.as_deref() == Some("av1") {
            a.add([
                "-c:v",
                "libaom-av1",
                "-crf",
                self.quality("38", "32", "24"),
                "-b:v",
                "0",
                "-cpu-used",
                self.quality("6", "6", "4"),
            ]);
        } else {
            a.add([
                "-c:v",
                "libvpx-vp9",
                "-crf",
                self.quality("34", "30", "24"),
                "-b:v",
                "0",
                "-row-mt",
                "1",
            ]);
        }
        if !self.audio_tracks.is_empty() {
            a.add([
                "-c:a",
                "libopus",
                "-b:a",
                self.quality("128k", "128k", "160k"),
            ]);
            for (index, t) in self.audio_tracks.iter().enumerate() {
                t.opus_layout(a, index);
            }
        }
        a.add(["-max_muxing_queue_size", "4096", "-fps_mode", "passthrough"]);
    }

    fn encoder(&self, a: &mut Args, hevc: bool, hdr: bool) {
        let preset = self.quality("veryfast", "fast", "slow");
        let quality = self.quality("28", "23", "18");
        let main_profile = if hdr { "main10" } else { "main" };
        let ten_bit = hevc && hdr;
        match self.backend.as_str() {
            "Nvidia" => {
                a.add([
                    "-c:v",
                    if hevc { "hevc_nvenc" } else { "h264_nvenc" },
                    "-preset",
                    self.quality("p1", "p4", "p6"),
                    "-tune",
                    "hq",
                    "-rc",
                    "vbr",
                    "-cq",
                    quality,
                    "-b:v",
                    "0",
                    "-multipass",
                    "disabled",
                    "-spatial-aq",
                    "1",
                    "-temporal-aq",
                    "0",
                    "-aq-strength",
                    "8",
                    "-rc-lookahead",
                    "0",
                    "-bf",
                    "3",
                    "-b_ref_mode",
                    "middle",
                ]);
                if hevc {
                    a.add(["-profile:v", main_profile]);
                }
                if !self.hardware_decode {
                    a.add(["-pix_fmt", if ten_bit { "p010le" } else { "yuv420p" }]);
                }
            }
            "Intel" => {
                a.add([
                    "-c:v",
                    if hevc { "hevc_qsv" } else { "h264_qsv" },
                    "-preset",
                    preset,
                    "-global_quality",
                    quality,
                    "-async_depth",
                    "8",
                    "-adaptive_i",
                    "1",
                    "-adaptive_b",
                    "1",
                ]);
                if hevc {
                    a.add(["-profile:v", main_profile]);
                }
                if !self.hardware_decode {
                    a.add(["-pix_fmt", if ten_bit { "p010le" } else { "nv12" }]);
                }
            }
            "Amd" => {
                let qp = self.quality("28", "23", "20");
                a.add([
                    "-c:v",
                    if hevc { "hevc_amf" } else { "h264_amf" },
                    "-usage",
                    "transcoding",
                    "-quality",
                    self.quality("speed", "quality", "quality"),
                    "-rc",
                    "cqp",
                    "-qp_i",
                    qp,
                    "-qp_p",
                    qp,
                ]);
                if ten_bit {
                    a.add(["-profile:v", "main10", "-pix_fmt", "p010le"]);
                } else {
                    a.add(["-pix_fmt", "yuv420p"]);
                }
            }
            "MediaFoundation" => {
                a.add([
                    "-c:v",
                    if hevc { "hevc_mf" } else { "h264_mf" },
                    "-hw_encoding",
                    "1",
                    "-rate_control",
                    "quality",
                    "-quality",
                    self.quality("65", "80", "90"),
                ]);
                if ten_bit {
                    a.add(["-profile:v", "main10"]);
                    if !self.hardware_decode {
                        a.add(["-pix_fmt", "p010le"]);
                    }
                } else if !self.hardware_decode {
                    a.add(["-pix_fmt", if hevc { "yuv420p" } else { "nv12" }]);
                }
            }
            _ => {
                let crf = if hevc {
                    self.quality("30", "24", "20")
                } else {
                    quality
                };
                a.add([
                    "-c:v",
                    if hevc { "libx265" } else { "libx264" },
                    "-preset",
                    preset,
                    "-crf",
                    crf,
                    "-pix_fmt",
                    if ten_bit { "yuv420p10le" } else { "yuv420p" },
                ]);
            }
        }
    }
}
