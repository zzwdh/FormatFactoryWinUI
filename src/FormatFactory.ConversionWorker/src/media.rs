use std::ffi::OsString;
use std::fs::OpenOptions;
use std::io::{self, Read, Write};
use std::os::windows::ffi::OsStringExt;
use std::os::windows::fs::OpenOptionsExt;
use std::os::windows::process::CommandExt;
use std::process::{Command, Stdio};

// stdout and stderr belong to the encoder. Only the startup handshake travels
// over this private pipe, so FFmpeg logs and progress pass through byte-for-byte.
pub(crate) fn run(args: &[OsString]) -> i32 {
    launch(args).unwrap_or(1)
}

fn launch(args: &[OsString]) -> io::Result<i32> {
    let invalid = || io::Error::from(io::ErrorKind::InvalidInput);
    let (id, stdin) = match args {
        [id] => (id, Stdio::null()),
        [id, flag] if flag == "--stdin" => (id, Stdio::inherit()),
        _ => return Err(invalid()),
    };
    let id = id.to_str().ok_or_else(invalid)?;
    if id.len() != 32 || !id.bytes().all(|b| b.is_ascii_hexdigit()) {
        return Err(invalid());
    }
    let mut control = OpenOptions::new()
        .read(true)
        .write(true)
        .security_qos_flags(0x0010_0000) // SECURITY_SQOS_PRESENT | SECURITY_ANONYMOUS
        .open(format!(r"\\.\pipe\FluentFormatConverter-{id}"))?;
    let mut length = [0; 4];
    control.read_exact(&mut length)?;
    let length = u32::from_le_bytes(length) as usize;
    if length < 4 || length > 65536 || length % 2 != 0 {
        return Err(invalid());
    }
    let mut data = vec![0; length];
    control.read_exact(&mut data)?;
    let wide: Vec<_> = data
        .chunks_exact(2)
        .map(|w| u16::from_le_bytes([w[0], w[1]]))
        .collect();
    let (&0, payload) = wide.split_last().ok_or_else(invalid)? else {
        return Err(invalid());
    };
    let mut argv = payload.split(|w| *w == 0).map(OsString::from_wide);
    let program = argv.next().filter(|s| !s.is_empty()).ok_or_else(invalid)?;
    let child = Command::new(program)
        .args(argv)
        .creation_flags(0x0800_0000) // CREATE_NO_WINDOW
        .stdin(stdin)
        .spawn();
    let mut child = match child {
        Ok(child) => child,
        Err(err) => {
            control.write_all(&0u32.to_le_bytes())?;
            control.write_all(&err.raw_os_error().unwrap_or(1).to_le_bytes())?;
            return Ok(1);
        }
    };
    // Keep the child handle alive until the host has attached pause/diagnostics.
    // The host releases the launch request only after assigning our job object.
    let acknowledge = (|| {
        control.write_all(&child.id().to_le_bytes())?;
        control.write_all(&0u32.to_le_bytes())?;
        let mut ack = [0];
        control.read_exact(&mut ack)?;
        if ack != [1] {
            return Err(invalid());
        }
        Ok(())
    })();
    if let Err(err) = acknowledge {
        let _ = child.kill();
        let _ = child.wait();
        return Err(err);
    }
    drop(control);
    Ok(child.wait()?.code().unwrap_or(1))
}
