#![deny(unsafe_code)]

#[cfg(windows)]
mod archive;
#[cfg(windows)]
mod artifact;
mod clipboard_html;
mod cso;
#[cfg(windows)]
mod discovery;
mod document;
mod document_route;
mod epub;
mod ffprobe;
mod ico;
mod image;
#[cfg(windows)]
mod media;
mod media_plan;
mod scheduler;
#[cfg(windows)]
mod store;
#[cfg(windows)]
mod watcher;

use std::ffi::OsString;
use std::fs::{File, OpenOptions};
use std::io::{self, Write};
use std::path::Path;
use std::process::ExitCode;
use std::time::{Duration, Instant};

#[derive(Debug, PartialEq)]
struct Failure {
    key: &'static str,
    detail: Option<i64>,
}

impl Failure {
    fn block(key: &'static str, index: u32) -> Self {
        Self {
            key,
            detail: Some(i64::from(index)),
        }
    }
}

impl From<&'static str> for Failure {
    fn from(key: &'static str) -> Self {
        Self { key, detail: None }
    }
}

impl From<io::Error> for Failure {
    fn from(error: io::Error) -> Self {
        Self {
            key: "io",
            detail: error.raw_os_error().map(i64::from),
        }
    }
}

type Result<T> = std::result::Result<T, Failure>;

fn input_file(path: &Path) -> io::Result<File> {
    let mut options = OpenOptions::new();
    options.read(true);
    #[cfg(windows)]
    {
        use std::os::windows::fs::OpenOptionsExt;
        options.share_mode(1); // FILE_SHARE_READ, same as the managed converter.
    }
    options.open(path).inspect(set_file_io_priority)
}

fn output_file(path: &Path) -> io::Result<File> {
    let mut options = OpenOptions::new();
    options.write(true).create_new(true);
    #[cfg(windows)]
    {
        use std::os::windows::fs::OpenOptionsExt;
        options.share_mode(0);
    }
    options.open(path).inspect(set_file_io_priority)
}

#[cfg(windows)]
#[allow(unsafe_code)]
fn set_file_io_priority(file: &File) {
    use std::ffi::c_void;
    use std::os::windows::io::AsRawHandle;
    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn SetFileInformationByHandle(
            file: *mut c_void,
            class: i32,
            info: *const u32,
            size: u32,
        ) -> i32;
    }
    // FileIoPriorityHintInfo (12) takes a FILE_IO_PRIORITY_HINT_INFO containing
    // one PRIORITY_HINT enum. The File and IoPriorityHintLow value outlive this call.
    let low = 1u32;
    unsafe {
        SetFileInformationByHandle(file.as_raw_handle(), 12, &low, size_of::<u32>() as u32);
    }
}

#[cfg(not(windows))]
fn set_file_io_priority(_: &File) {}

struct Progress {
    last: Instant,
}

impl Progress {
    fn report(&mut self, bytes: u64, total: u64, block: u32, count: u32) -> Result<()> {
        if block == 0 || block == count || self.last.elapsed() >= Duration::from_millis(200) {
            let mut out = io::stdout().lock();
            writeln!(out, "progress {bytes} {total} {block} {count}")?;
            out.flush()?;
            self.last = Instant::now();
        }
        Ok(())
    }
}

fn write_json<T: serde::Serialize>(value: &T) -> Result<()> {
    serde_json::to_writer(io::stdout().lock(), value).map_err(io::Error::other)?;
    Ok(())
}

fn execute(operation: &OsString, rest: &[OsString]) -> Result<()> {
    #[cfg(windows)]
    if operation == "discover-files" && rest.is_empty() {
        return discovery::run();
    }
    #[cfg(windows)]
    if operation == "store-transaction" && rest.is_empty() {
        return store::run();
    }
    if operation == "disc-check" {
        return cso::check(rest);
    }
    if operation == "ico-pack" {
        return Ok(ico::run(rest)?);
    }
    let [
        direction,
        input_flag,
        input,
        output_flag,
        output,
        size_flag,
        size,
    ] = rest
    else {
        return Err("invalid-request".into());
    };
    if operation != "disc-image"
        || input_flag != "--input"
        || output_flag != "--output"
        || size_flag != "--block-size"
    {
        return Err("invalid-request".into());
    }
    let size: u32 = size
        .to_str()
        .and_then(|s| s.parse().ok())
        .ok_or("invalid-request")?;
    let input = Path::new(input);
    let output = Path::new(output);
    let mut progress = Progress {
        last: Instant::now(),
    };
    match direction.to_str() {
        Some("iso-to-cso") => cso::encode(input, output, size, &mut progress),
        Some("cso-to-iso") => cso::decode(input, output, &mut progress),
        _ => Err("invalid-request".into()),
    }
}

fn main() -> ExitCode {
    let args: Vec<_> = std::env::args_os().skip(1).collect();
    let [version, operation, rest @ ..] = args.as_slice() else {
        return finish(Err("invalid-request".into()), false);
    };
    if version != "--protocol-v1" {
        return finish(Err("invalid-request".into()), false);
    }

    #[cfg(windows)]
    match (operation.to_str(), rest) {
        (Some("run-engine"), args) => std::process::exit(media::run(args)),
        (Some("archive-convert"), []) => std::process::exit(archive::run()),
        (Some("clipboard-html"), []) => return finish(clipboard_html::run(), false),
        (Some("image-inspect"), []) => return finish(image::run(), false),
        (Some("media-encode"), []) => return finish(media_plan::session(), false),
        (Some("media-plan"), []) => return finish(media_plan::run(), false),
        (Some("ffprobe-parse"), []) => return finish(ffprobe::run(), false),
        (Some("epub-write"), []) => return finish(epub::run(true), false),
        (Some("epub-validate"), []) => return finish(epub::run(false), false),
        (Some("watch-directory"), []) => return finish(watcher::run(), false),
        (Some("document-route"), []) => return finish(document_route::run(), false),
        (Some("docx-write"), []) => return finish(document::docx_write::run(), false),
        (Some("document-read"), []) => return finish(document::with_document_stack(document::docx::run), false),
        (Some("document-text"), []) => return finish(document::with_document_stack(document::run), false),
        (Some("scheduler"), []) => return finish(scheduler::run(), false),
        (Some("artifact"), []) => return finish(artifact::run(), false),
        _ => {}
    }
    finish(execute(operation, rest), true)
}

fn finish(result: Result<()>, ack: bool) -> ExitCode {
    match result {
        Ok(()) if !ack => ExitCode::SUCCESS,
        Ok(()) => match writeln!(io::stdout().lock(), "ok") {
            Ok(()) => ExitCode::SUCCESS,
            Err(_) => ExitCode::FAILURE,
        },
        Err(err) => {
            let mut stderr = io::stderr().lock();
            let _ = match err.detail {
                Some(detail) => writeln!(stderr, "error {} {detail}", err.key),
                None => writeln!(stderr, "error {}", err.key),
            };
            ExitCode::FAILURE
        }
    }
}
