use std::ffi::OsString;
use std::io::{self, BufWriter, Write};
use std::path::Path;

// Frame data is already PNG-encoded by the selected FFmpeg. Write ICONDIR
// and ICONDIRENTRY first, then stream each image into the container in order.
pub(crate) fn run(args: &[OsString]) -> crate::Result<()> {
    let [flag, output, frames @ ..] = args else {
        return Err("invalid-request".into());
    };
    if flag != "--output" || frames.is_empty() || frames.len() % 2 != 0 {
        return Err("invalid-request".into());
    }
    let count = u16::try_from(frames.len() / 2).map_err(|_| "invalid-request")?;
    let mut entries = Vec::with_capacity(usize::from(count));
    let mut offset = 6 + 16 * u32::from(count);
    for frame in frames.chunks_exact(2) {
        let size: i32 = frame[0]
            .to_str()
            .and_then(|s| s.parse().ok())
            .ok_or("invalid-request")?;
        let dimension = if size >= 256 { 0 } else { size as u8 };
        let file = crate::input_file(Path::new(&frame[1]))?;
        let length = u32::try_from(file.metadata()?.len()).map_err(|_| "invalid-request")?;
        entries.push((dimension, file, length, offset));
        offset = offset.checked_add(length).ok_or("invalid-request")?;
    }
    let mut out = BufWriter::new(crate::output_file(Path::new(output))?);
    out.write_all(&[0, 0, 1, 0])?;
    out.write_all(&count.to_le_bytes())?;
    for (size, _, length, offset) in &entries {
        out.write_all(&[*size, *size, 0, 0, 1, 0, 32, 0])?;
        out.write_all(&length.to_le_bytes())?;
        out.write_all(&offset.to_le_bytes())?;
    }
    for (_, mut file, length, _) in entries {
        if io::copy(&mut file, &mut out)? != u64::from(length) {
            return Err(io::Error::from(io::ErrorKind::UnexpectedEof).into());
        }
    }
    out.flush()?;
    Ok(())
}
