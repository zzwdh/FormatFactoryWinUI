mod engine;
mod listing;
mod volumes;

use engine::Engine;
use listing::Listing;
use serde::Deserialize;
use std::ffi::OsStr;
use std::fs;
use std::io::{self, BufRead, Read, Write};
use std::os::windows::ffi::OsStrExt;
use std::os::windows::fs::MetadataExt;
use std::path::{Path, PathBuf};

#[derive(Debug, Clone, Copy)]
enum Error {
    EngineUnavailable,
    EngineFailed,
    CorruptArchive,
    MissingVolume,
    UnsafeEntry,
    InsufficientSpace,
    ValidationFailed,
    InvalidOptions,
    OutputUnavailable,
}
type Result<T> = std::result::Result<T, Error>;
impl From<io::Error> for Error {
    fn from(_: io::Error) -> Self {
        Self::EngineFailed
    }
}

#[derive(Deserialize)]
enum Format {
    Zip,
    SevenZip,
    Tar,
    TarGZip,
    TarXz,
}

impl Format {
    fn extension(&self) -> &'static str {
        match self {
            Self::Zip => ".zip",
            Self::SevenZip => ".7z",
            Self::Tar => ".tar",
            Self::TarGZip => ".tar.gz",
            Self::TarXz => ".tar.xz",
        }
    }
    fn wrapped(&self) -> bool {
        matches!(self, Self::TarGZip | Self::TarXz)
    }
}

#[derive(Deserialize)]
struct Request {
    engine: PathBuf,
    source: PathBuf,
    scratch: PathBuf,
    format: Format,
    strict: bool,
}

fn read_line(input: &mut impl BufRead, limit: u64) -> Result<String> {
    let mut line = String::new();
    input.take(limit + 1).read_line(&mut line)?;
    if line.len() as u64 > limit || !line.ends_with('\n') {
        return Err(Error::InvalidOptions);
    }
    line.pop();
    if line.ends_with('\r') {
        line.pop();
    }
    Ok(line)
}

fn signal(line: &str) -> Result<()> {
    let mut output = io::stdout().lock();
    writeln!(output, "{line}")?;
    output.flush()?;
    Ok(())
}

fn progress(input: &mut impl BufRead, stage: &str) -> Result<()> {
    signal(stage)?;
    // The callback may cancel or inspect the staged file before the next step.
    if read_line(input, 8)? != "ok" {
        return Err(Error::InvalidOptions);
    }
    Ok(())
}

fn read_password(input: &mut impl BufRead) -> Result<String> {
    let value = read_line(input, 4098)?;
    if value.encode_utf16().count() > 1024 || value.chars().any(char::is_control) {
        return Err(Error::InvalidOptions);
    }
    Ok(value)
}

fn ask(input: &mut impl BufRead, retry: bool) -> Result<String> {
    signal(if retry {
        "password retry"
    } else {
        "password required"
    })?;
    read_password(input)
}

#[allow(unsafe_code)]
fn ensure_space(
    destination: &Path,
    total: u64,
    wrapped_input: bool,
    request: &Request,
) -> Result<()> {
    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn GetDiskFreeSpaceExW(
            dir: *const u16,
            available: *mut u64,
            total: *mut u64,
            free: *mut u64,
        ) -> i32;
    }
    let dir: Vec<u16> = destination
        .as_os_str()
        .encode_wide()
        .chain(Some(0))
        .collect();
    let mut available = 0u64;
    // The optional total/free outputs may be NULL. The existing private scratch
    // directory identifies the actual destination volume (including mount points).
    let reported = unsafe {
        GetDiskFreeSpaceExW(
            dir.as_ptr(),
            &mut available,
            std::ptr::null_mut(),
            std::ptr::null_mut(),
        )
    };
    let copies = 2.1
        + f64::from(wrapped_input)
        + f64::from(request.format.wrapped())
        + f64::from(request.strict);
    if reported != 0 && total as f64 * copies + 1_048_576.0 > available as f64 {
        return Err(Error::InsufficientSpace);
    }
    Ok(())
}

// Retry after a bad password. Only descend into ordinary directories: 7-Zip
// may have left partial, read-only output if the previous extraction failed.
fn clear_extraction(root: &Path) -> Result<()> {
    let mut pending = vec![root.to_owned()];
    while let Some(path) = pending.pop() {
        let meta = fs::symlink_metadata(&path)?;
        if meta.file_attributes() & 0x400 == 0 {
            if meta.permissions().readonly() {
                let mut p = meta.permissions();
                p.set_readonly(false);
                fs::set_permissions(&path, p)?;
            }
            if meta.is_dir() {
                for child in fs::read_dir(&path)? {
                    pending.push(child?.path());
                }
            }
        }
    }
    fs::remove_dir_all(root)?;
    fs::create_dir(root)?;
    Ok(())
}

fn unpack_source(
    request: &Request,
    engine: &Engine,
    input: &mut impl BufRead,
) -> Result<(PathBuf, Listing)> {
    let source = &request.source;
    let content = request.scratch.join("content");
    fs::create_dir(&content).map_err(|_| Error::OutputUnavailable)?;
    let source_name = source
        .file_name()
        .and_then(|s| s.to_str())
        .ok_or(Error::InvalidOptions)?
        .to_ascii_lowercase();
    let wrapped = [".tar.gz", ".tar.xz", ".tgz", ".txz"]
        .iter()
        .any(|ext| source_name.ends_with(ext));
    let mut password = None::<String>;
    let snapshot = loop {
        let output = engine.list(source, password.as_deref(), false)?;
        if !output.success {
            if output.needs_password(password.is_some()) {
                password = Some(ask(input, password.is_some())?);
                continue;
            }
            // -ba suppresses the missing-volume header in some RAR/ZIP failures.
            let details = engine.call(
                &["l", "-slt"],
                &[source.as_os_str()],
                password.as_deref(),
                None,
            )?;
            if matches!(output.read_error(), Error::MissingVolume) {
                return Err(Error::MissingVolume);
            }
            return Err(details.read_error());
        }
        let listing = Listing::parse(&output.listing, Some(source))?;
        if listing.encrypted && password.is_none() {
            password = Some(ask(input, false)?);
            continue;
        }
        ensure_space(&request.scratch, listing.total, wrapped, request)?;
        progress(input, "extracting")?;
        let extraction = engine.extract(source, &content, password.as_deref(), false)?;
        if !extraction.success {
            if extraction.needs_password(password.is_some()) {
                password = Some(ask(input, true)?);
                clear_extraction(&content)?;
                continue;
            }
            return Err(extraction.read_error());
        }
        let snapshot = Listing::snapshot(&content)?;
        listing.compare(&snapshot)?;
        break snapshot;
    };
    if !wrapped {
        return Ok((content, snapshot));
    }
    let tar = content.join(snapshot.single_file().ok_or(Error::CorruptArchive)?);
    let output = engine.list(&tar, None, true)?;
    if !output.success {
        return Err(output.read_error());
    }
    let listing = Listing::parse(&output.listing, None)?;
    ensure_space(&request.scratch, listing.total, true, request)?;
    let inner = request.scratch.join("inner-content");
    fs::create_dir(&inner)?;
    let extraction = engine.extract(&tar, &inner, None, true)?;
    if !extraction.success {
        return Err(extraction.read_error());
    }
    let snapshot = Listing::snapshot(&inner)?;
    listing.compare(&snapshot)?;
    Ok((inner, snapshot))
}

fn create(
    request: &Request,
    engine: &Engine,
    content: &Path,
    staged: &Path,
    password: Option<&str>,
) -> Result<()> {
    let method = match request.format {
        Format::Zip => "-tzip",
        Format::SevenZip => "-t7z",
        _ => "-ttar",
    };
    let mut switches = vec!["a", method, "-y", "-spd"];
    if matches!(request.format, Format::Zip) {
        switches.push("-mcu=on");
    }
    if password.is_some() {
        switches.push("-p");
        switches.push(if matches!(request.format, Format::Zip) {
            "-mem=AES256"
        } else {
            "-mhe=on"
        });
    }
    let tar = request.scratch.join("payload.tar");
    let first = if request.format.wrapped() {
        tar.as_path()
    } else {
        staged
    };
    engine
        .call(
            &switches,
            &[first.as_os_str(), OsStr::new(".")],
            password,
            Some(content),
        )?
        .require(Error::EngineFailed)?;
    if request.format.wrapped() {
        let method = if matches!(request.format, Format::TarGZip) {
            "-tgzip"
        } else {
            "-txz"
        };
        engine
            .call(
                &["a", method, "-y"],
                &[staged.as_os_str(), tar.as_os_str()],
                None,
                Some(&request.scratch),
            )?
            .require(Error::EngineFailed)?;
    }
    Ok(())
}

fn verify_container(
    request: &Request,
    engine: &Engine,
    staged: &Path,
    password: Option<&str>,
) -> Result<PathBuf> {
    engine
        .call(&["t"], &[staged.as_os_str()], password, None)?
        .require(Error::ValidationFailed)?;
    if !request.format.wrapped() {
        return Ok(staged.to_owned());
    }
    let wrapper = request.scratch.join("verified-wrapper");
    fs::create_dir(&wrapper)?;
    let entries = Listing::parse(
        &engine
            .list(staged, None, false)?
            .require(Error::ValidationFailed)?,
        Some(staged),
    )?;
    engine
        .extract(staged, &wrapper, None, false)?
        .require(Error::ValidationFailed)?;
    entries.compare(&Listing::snapshot(&wrapper)?)?;
    let tar = wrapper.join(entries.single_file().ok_or(Error::ValidationFailed)?);
    engine
        .call(&["t", "-ttar"], &[tar.as_os_str()], None, None)?
        .require(Error::ValidationFailed)?;
    Ok(tar)
}

fn execute(input: &mut impl BufRead) -> Result<()> {
    let request: Request =
        serde_json::from_str(&read_line(input, 1024 * 1024)?).map_err(|_| Error::InvalidOptions)?;
    if !request.engine.is_absolute()
        || !request.source.is_absolute()
        || !request.scratch.is_absolute()
    {
        return Err(Error::InvalidOptions);
    }
    let output_password = read_password(input)?;
    let password = (!output_password.is_empty()).then_some(output_password.as_str());
    if let Some(value) = password {
        match request.format {
            Format::SevenZip => (),
            Format::Zip if value.len() <= 99 && value.bytes().all(|b| (32..=126).contains(&b)) => {
                ()
            }
            _ => return Err(Error::InvalidOptions),
        }
    }
    volumes::validate(&request.source)?;
    let engine = Engine(&request.engine);
    progress(input, "inspecting")?;
    let (content, source) = unpack_source(&request, &engine, input)?;
    let source_hashes = if request.strict {
        Some(source.hashes(&content)?)
    } else {
        None
    };
    progress(input, "compressing")?;
    let staged = request
        .scratch
        .join(format!("converted{}", request.format.extension()));
    create(&request, &engine, &content, &staged, password)?;
    progress(input, "verifying")?;
    let verified = verify_container(&request, &engine, &staged, password)?;
    let target_password = if verified == staged { password } else { None };
    let target = Listing::parse(
        &engine
            .list(&verified, target_password, false)?
            .require(Error::ValidationFailed)?,
        None,
    )?;
    source.compare(&target)?;
    if let Some(expected) = source_hashes {
        progress(input, "comparing")?;
        let dest = request.scratch.join("verified-content");
        fs::create_dir(&dest)?;
        engine
            .extract(&verified, &dest, target_password, false)?
            .require(Error::ValidationFailed)?;
        let actual = Listing::snapshot(&dest)?;
        target.compare(&actual)?;
        if expected != actual.hashes(&dest)? {
            return Err(Error::ValidationFailed);
        }
    }
    Ok(())
}

pub(crate) fn run() -> i32 {
    match execute(&mut io::stdin().lock()) {
        Ok(()) if signal("ok").is_ok() => 0,
        Ok(()) => 1,
        Err(error) => {
            let _ = signal(&format!("error {error:?}"));
            1
        }
    }
}
