use super::{Failure, Result, input_file, write_json};
use serde::Deserialize;
use serde::Serialize;
use std::collections::HashSet;
use std::fs::File;
use std::io::{self, BufReader, Read, Seek, SeekFrom};
use std::path::Path;

const ANIMATION: &str = "NativeImageAnimationValidationFailed";
const TIFF: &str = "NativeImageValidationFailed";
const LIMIT: usize = 100_000;

struct Source {
    file: BufReader<File>,
    length: u64,
    pos: u64,
    invalid: &'static str,
}
impl Source {
    fn open(path: &Path, invalid: &'static str) -> Result<Self> {
        let file = input_file(path)?;
        let length = file.metadata()?.len();
        Ok(Self {
            file: BufReader::new(file),
            length,
            pos: 0,
            invalid,
        })
    }
    fn read(&mut self, bytes: &mut [u8]) -> Result<()> {
        if bytes.len() as u64 > self.length - self.pos {
            return Err(self.invalid.into());
        }
        self.file.read_exact(bytes)?;
        self.pos += bytes.len() as u64;
        Ok(())
    }
    fn bytes<const N: usize>(&mut self) -> Result<[u8; N]> {
        let mut bytes = [0; N];
        self.read(&mut bytes)?;
        Ok(bytes)
    }
    fn byte(&mut self) -> Result<u8> {
        Ok(self.bytes::<1>()?[0])
    }
    fn le16(&mut self) -> Result<u32> {
        Ok(u16::from_le_bytes(self.bytes()?).into())
    }
    fn le24(&mut self) -> Result<u32> {
        let [a, b, c] = self.bytes()?;
        Ok(u32::from_le_bytes([a, b, c, 0]))
    }
    fn le32(&mut self) -> Result<u32> {
        Ok(u32::from_le_bytes(self.bytes()?))
    }
    fn go(&mut self, offset: u64) -> Result<()> {
        if offset > self.length {
            return Err(self.invalid.into());
        }
        self.file.seek_relative(offset as i64 - self.pos as i64)?;
        self.pos = offset;
        Ok(())
    }
    fn skip(&mut self, length: u64) -> Result<()> {
        self.go(self.pos.checked_add(length).ok_or(self.invalid)?)
    }
    fn pad(&mut self, length: u32) -> Result<()> {
        if length & 1 != 0 && self.byte()? != 0 {
            return Err(self.invalid.into());
        }
        Ok(())
    }
}

#[derive(Default, Serialize)]
#[serde(rename_all = "PascalCase")]
struct Animation {
    width: u32,
    height: u32,
    frame_durations: Vec<u32>,
    loop_count: u32,
    is_lossless: bool,
}

fn gif(file: &mut Source) -> Result<Animation> {
    file.go(6)?;
    let mut info = Animation {
        width: file.le16()?,
        height: file.le16()?,
        loop_count: 1,
        is_lossless: true,
        ..Animation::default()
    };
    let flags = file.byte()?;
    file.skip(2)?;
    if flags & 0x80 != 0 {
        file.skip(3 << ((flags & 7) + 1))?;
    }
    let mut delay = 0;
    loop {
        match file.byte()? {
            0x3b => break,
            0x21 => match file.byte()? {
                0xf9 => {
                    if file.byte()? != 4 {
                        return Err(ANIMATION.into());
                    }
                    file.skip(1)?;
                    delay = file.le16()? * 10;
                    file.skip(1)?;
                    if file.byte()? != 0 {
                        return Err(ANIMATION.into());
                    }
                }
                label => {
                    let mut loop_extension = false;
                    if label == 0xff {
                        let size = file.byte()?;
                        if size == 11 {
                            let app = file.bytes::<11>()?;
                            loop_extension = matches!(&app, b"NETSCAPE2.0" | b"ANIMEXTS1.0");
                        } else {
                            file.skip(size.into())?;
                        }
                    }
                    sub_blocks(file, loop_extension, &mut info.loop_count)?;
                }
            },
            0x2c => {
                file.skip(8)?;
                let flags = file.byte()?;
                if flags & 0x80 != 0 {
                    file.skip(3 << ((flags & 7) + 1))?;
                }
                file.skip(1)?;
                sub_blocks(file, false, &mut info.loop_count)?;
                if info.frame_durations.len() == LIMIT {
                    return Err(ANIMATION.into());
                }
                info.frame_durations.push(delay);
                delay = 0;
            }
            _ => return Err(ANIMATION.into()),
        }
    }
    if info.width == 0 || info.height == 0 || info.frame_durations.is_empty() {
        return Err(ANIMATION.into());
    }
    Ok(info)
}

fn sub_blocks(file: &mut Source, loop_extension: bool, loops: &mut u32) -> Result<()> {
    loop {
        let size = file.byte()?;
        if size == 0 {
            return Ok(());
        }
        if loop_extension && size == 3 {
            let [flag, low, high] = file.bytes()?;
            if flag == 1 {
                let repeats = u16::from_le_bytes([low, high]);
                *loops = if repeats == 0 {
                    0
                } else {
                    u32::from(repeats) + 1
                };
            }
        } else {
            file.skip(size.into())?;
        }
    }
}

fn webp(file: &mut Source, verify: bool) -> Result<Animation> {
    file.go(4)?;
    if u64::from(file.le32()?) + 8 != file.length {
        return Err(ANIMATION.into());
    }
    file.go(12)?;
    let mut info = Animation {
        is_lossless: true,
        ..Animation::default()
    };
    let mut has_anim = false;
    while file.pos != file.length {
        let tag = file.bytes::<4>()?;
        let length = file.le32()?;
        let end = file.pos + u64::from(length);
        if end > file.length {
            return Err(ANIMATION.into());
        }
        match &tag {
            b"VP8X" => {
                if length != 10 || info.width != 0 || file.byte()? & 0xc3 != 2 || file.le24()? != 0
                {
                    return Err(ANIMATION.into());
                }
                info.width = file.le24()? + 1;
                info.height = file.le24()? + 1;
                if info.width.checked_mul(info.height).is_none() {
                    return Err(ANIMATION.into());
                }
            }
            b"ANIM" => {
                if length != 6 || info.width == 0 || has_anim {
                    return Err(ANIMATION.into());
                }
                file.skip(4)?;
                info.loop_count = file.le16()?;
                has_anim = true;
            }
            b"ANMF" => {
                if !has_anim || length < 24 || info.frame_durations.len() == LIMIT {
                    return Err(ANIMATION.into());
                }
                let x = file.le24()? * 2;
                let y = file.le24()? * 2;
                let width = file.le24()? + 1;
                let height = file.le24()? + 1;
                let delay = file.le24()?;
                if file.byte()? & 0xfc != 0 || x + width > info.width || y + height > info.height {
                    return Err(ANIMATION.into());
                }
                let start = file.pos;
                let (lossless, alpha) = frame_chunks(file, end, width, height)?;
                if verify {
                    verify_frame(file, start, end, width, height, alpha)?;
                }
                info.is_lossless &= lossless;
                info.frame_durations.push(delay);
            }
            _ => {}
        }
        file.go(end)?;
        file.pad(length)?;
    }
    if info.frame_durations.len() < 2 {
        return Err(ANIMATION.into());
    }
    Ok(info)
}

fn frame_chunks(file: &mut Source, end: u64, width: u32, height: u32) -> Result<(bool, bool)> {
    let mut image = None;
    let mut alpha = false;
    while file.pos < end {
        let tag = file.bytes::<4>()?;
        let length = file.le32()?;
        let chunk_end = file.pos + u64::from(length);
        if chunk_end + u64::from(length & 1) > end {
            return Err(ANIMATION.into());
        }
        match &tag {
            b"VP8L" => {
                if image.is_some() || alpha || length < 5 || file.byte()? != 0x2f {
                    return Err(ANIMATION.into());
                }
                let bits = file.le32()?;
                if bits >> 29 != 0
                    || (bits & 0x3fff) + 1 != width
                    || ((bits >> 14) & 0x3fff) + 1 != height
                {
                    return Err(ANIMATION.into());
                }
                image = Some(true);
                alpha = bits & (1 << 28) != 0;
            }
            b"VP8 " => {
                if image.is_some() || length < 10 {
                    return Err(ANIMATION.into());
                }
                let bits = file.le24()?;
                if bits & 1 != 0
                    || (bits >> 1) & 7 > 3
                    || bits & 16 == 0
                    || bits >> 5 > length - 10
                    || file.bytes::<3>()? != [0x9d, 1, 0x2a]
                    || file.le16()? & 0x3fff != width
                    || file.le16()? & 0x3fff != height
                {
                    return Err(ANIMATION.into());
                }
                image = Some(false);
            }
            b"ALPH" => {
                if length < 2 || image.is_some() || alpha {
                    return Err(ANIMATION.into());
                }
                let flags = file.byte()?;
                if flags & 0xc0 != 0
                    || flags & 3 > 1
                    || (flags >> 4) & 3 > 1
                    || (flags & 3 == 0
                        && u64::from(length) != 1 + u64::from(width) * u64::from(height))
                {
                    return Err(ANIMATION.into());
                }
                alpha = true;
            }
            _ => {}
        }
        file.go(chunk_end)?;
        file.pad(length)?;
    }
    Ok((image.ok_or(ANIMATION)?, alpha))
}

// A seekable stand-alone RIFF view over one ANMF payload. The decoder only
// allocates this frame's pixels even if a tiny frame sits on a large canvas.
struct FrameView<'a> {
    header: [u8; 30],
    source: &'a mut BufReader<File>,
    start: u64,
    length: u64,
    pos: u64,
}
impl Read for FrameView<'_> {
    fn read(&mut self, out: &mut [u8]) -> io::Result<usize> {
        let count = if self.pos < 30 {
            let bytes = &self.header[self.pos as usize..];
            let count = bytes.len().min(out.len());
            out[..count].copy_from_slice(&bytes[..count]);
            count
        } else {
            let count = (self.length.saturating_sub(self.pos)).min(out.len() as u64) as usize;
            self.source.read(&mut out[..count])?
        };
        self.pos += count as u64;
        Ok(count)
    }
}
impl Seek for FrameView<'_> {
    fn seek(&mut self, from: SeekFrom) -> io::Result<u64> {
        let target = match from {
            SeekFrom::Start(pos) => Some(pos),
            SeekFrom::Current(delta) => self.pos.checked_add_signed(delta),
            SeekFrom::End(delta) => self.length.checked_add_signed(delta),
        }
        .filter(|p| *p <= self.length)
        .ok_or(io::ErrorKind::InvalidInput)?;
        self.source
            .seek(SeekFrom::Start(self.start + target.saturating_sub(30)))?;
        self.pos = target;
        Ok(target)
    }
}
fn verify_frame(
    file: &mut Source,
    start: u64,
    end: u64,
    width: u32,
    height: u32,
    alpha: bool,
) -> Result<()> {
    let mut header = [0; 30];
    header[..4].copy_from_slice(b"RIFF");
    let riff_size = u32::try_from(end - start + 22).map_err(|_| ANIMATION)?;
    header[4..8].copy_from_slice(&riff_size.to_le_bytes());
    header[8..16].copy_from_slice(b"WEBPVP8X");
    header[16] = 10;
    header[20] = if alpha { 16 } else { 0 };
    header[24..27].copy_from_slice(&(width - 1).to_le_bytes()[..3]);
    header[27..].copy_from_slice(&(height - 1).to_le_bytes()[..3]);
    file.go(start)?;
    let view = FrameView {
        header,
        source: &mut file.file,
        start,
        length: u64::from(riff_size) + 8,
        pos: 0,
    };
    let mut decoder = image_webp::WebPDecoder::new(BufReader::new(view)).map_err(|_| ANIMATION)?;
    if decoder.dimensions() != (width, height) || decoder.is_animated() {
        return Err(ANIMATION.into());
    }
    let size = decoder.output_buffer_size().ok_or(ANIMATION)?;
    let mut pixels = Vec::new();
    pixels.try_reserve_exact(size).map_err(|_| ANIMATION)?;
    pixels.resize(size, 0);
    decoder.read_image(&mut pixels).map_err(|_| ANIMATION)?;
    file.file.seek(SeekFrom::Start(end))?;
    file.pos = end;
    Ok(())
}
fn number(file: &mut Source, width: usize, little: bool) -> Result<u64> {
    let mut buf = [0; 8];
    if little {
        file.read(&mut buf[..width])?;
        Ok(u64::from_le_bytes(buf))
    } else {
        file.read(&mut buf[8 - width..])?;
        Ok(u64::from_be_bytes(buf))
    }
}

fn tiff_pages(file: &mut Source) -> Result<Option<usize>> {
    if file.length < 4 {
        return Ok(None);
    }
    let little = match &file.bytes::<2>()? {
        b"II" => true,
        b"MM" => false,
        _ => return Ok(None),
    };
    let big = match number(file, 2, little)? {
        42 => false,
        43 => true,
        _ => return Ok(None),
    };
    if big && (number(file, 2, little)? != 8 || number(file, 2, little)? != 0) {
        return Err(TIFF.into());
    }
    let pointer = if big { 8 } else { 4 };
    let mut offset = number(file, pointer, little)?;
    let header = file.pos;
    let mut visited = HashSet::new();
    while offset != 0 {
        if visited.len() == LIMIT || offset < header || !visited.insert(offset) {
            return Err(TIFF.into());
        }
        file.go(offset)?;
        let entries = number(file, if big { 8 } else { 2 }, little)?;
        let entries_size = entries.checked_mul(if big { 20 } else { 12 }).ok_or(TIFF)?;
        file.skip(entries_size)?;
        offset = number(file, pointer, little)?;
    }
    if visited.is_empty() {
        return Err(TIFF.into());
    }
    Ok(Some(visited.len()))
}

fn detect(file: &mut Source) -> Result<bool> {
    if file.length < 12 {
        return Ok(false);
    }
    let header = file.bytes::<12>()?;
    if header[..8] == *b"\x89PNG\r\n\x1a\n" {
        file.go(8)?;
        while file.length - file.pos >= 12 {
            let length = u32::from_be_bytes(file.bytes()?);
            match &file.bytes::<4>()? {
                b"acTL" => return Ok(true),
                b"IDAT" | b"IEND" => return Ok(false),
                _ => {}
            }
            let remaining = u64::from(length) + 4;
            if remaining > file.length - file.pos {
                break;
            }
            file.skip(remaining)?;
        }
    } else if header[..4] == *b"RIFF" && header[8..] == *b"WEBP" {
        while file.length - file.pos >= 8 {
            let tag = file.bytes::<4>()?;
            let length = file.le32()?;
            let end = file.pos + u64::from(length) + u64::from(length & 1);
            if &tag == b"ANIM" || &tag == b"ANMF" {
                return Ok(true);
            }
            if &tag == b"VP8X" && length > 0 && file.pos < file.length && file.byte()? & 2 != 0 {
                return Ok(true);
            }
            if end > file.length {
                break;
            }
            file.go(end)?;
        }
    } else if header[4..8] == *b"ftyp" {
        let length = u32::from_be_bytes(header[..4].try_into().unwrap());
        file.go(8)?;
        let end = if length == 1 {
            u64::from_be_bytes(file.bytes()?)
        } else {
            u64::from(length)
        };
        if end < file.pos + 8 || end > file.length {
            return Ok(false);
        }
        let start = file.pos;
        let limit = end.min(start + 4096);
        while file.pos + 4 <= limit {
            let is_brand = file.pos != start + 4;
            if file.bytes::<4>()? == *b"avis" && is_brand {
                return Ok(true);
            }
        }
    }
    Ok(false)
}

#[derive(Deserialize)]
struct Request {
    mode: String,
    path: std::path::PathBuf,
}

pub(super) fn run() -> Result<()> {
    let request: Request =
        serde_json::from_reader(io::stdin().lock()).map_err(|_| "invalid-request")?;
    let mode = request.mode.as_str();
    let mut file = Source::open(
        &request.path,
        if mode == "tiff-pages" {
            TIFF
        } else {
            ANIMATION
        },
    )?;
    match mode {
        "detect" => write_json(&detect(&mut file)?),
        "tiff-pages" => write_json(&tiff_pages(&mut file)?.unwrap_or(0)),
        "animation" | "verify-webp" => {
            let header = file.bytes::<12>()?;
            let verify = mode == "verify-webp";
            let info = if header[..4] == *b"RIFF" && header[8..] == *b"WEBP" {
                webp(&mut file, verify)?
            } else if !verify && matches!(&header[..6], b"GIF87a" | b"GIF89a") {
                gif(&mut file)?
            } else {
                return Err(Failure::from("NativeImageAnimationUnsupported"));
            };
            write_json(&info)
        }
        _ => Err("invalid-request".into()),
    }
}
