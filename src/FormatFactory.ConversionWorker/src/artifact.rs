use crate::{Result, input_file, output_file, write_json};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::fs;
use std::io::{self, Read};
use std::os::windows::ffi::OsStrExt;
use std::os::windows::fs::MetadataExt;
use std::path::{Component, Path, PathBuf};

#[derive(Deserialize)]
#[serde(rename_all = "PascalCase")]
struct Request {
    mode: String,
    path: PathBuf,
    directory: bool,
    destination: Option<PathBuf>,
    scratch: Option<PathBuf>,
    files: Option<Vec<Entry>>,
}

#[derive(Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
struct Entry {
    path: String,
    is_directory: bool,
    length: u64,
    sha256: String,
}

fn metadata(path: &Path) -> Result<fs::Metadata> {
    let m = fs::symlink_metadata(path)?;
    if m.file_attributes() & 0x400 != 0 {
        return Err("artifact-link".into());
    }
    Ok(m)
}

fn fingerprint(root: &Path, directory: bool) -> Result<Vec<Entry>> {
    if metadata(root)?.is_dir() != directory {
        return Err("invalid-artifact-type".into());
    }
    let mut pending = vec![root.to_path_buf()];
    let mut entries = Vec::new();
    let mut buffer = vec![0; 128 * 1024];
    while let Some(path) = pending.pop() {
        let relative = path
            .strip_prefix(root)
            .map_err(|_| "invalid-artifact-path")?
            .to_str()
            .ok_or("invalid-artifact-path")?
            .to_owned();
        if metadata(&path)?.is_dir() {
            if !relative.is_empty() {
                entries.push(Entry {
                    path: relative,
                    is_directory: true,
                    length: 0,
                    sha256: String::new(),
                });
            }
            for child in fs::read_dir(path)? {
                pending.push(child?.path());
            }
        } else {
            let mut file = input_file(&path)?;
            let length = file.metadata()?.len();
            let mut hash = Sha256::new();
            loop {
                let n = file.read(&mut buffer)?;
                if n == 0 {
                    break;
                }
                hash.update(&buffer[..n]);
            }
            entries.push(Entry {
                path: relative,
                is_directory: false,
                length,
                sha256: format!("{:X}", hash.finalize()),
            });
        }
    }
    // Match StringComparer.Ordinal, including supplementary-plane filenames in old receipts.
    entries.sort_by(|a, b| a.path.encode_utf16().cmp(b.path.encode_utf16()));
    Ok(entries)
}

fn copy(root: &Path, destination: &Path, directory: bool, entries: Vec<Entry>) -> Result<()> {
    if metadata(root)?.is_dir() != directory {
        return Err("invalid-artifact-type".into());
    }
    if directory {
        fs::create_dir(destination)?;
    }
    for entry in entries {
        let relative = Path::new(&entry.path);
        if relative
            .components()
            .any(|c| !matches!(c, Component::Normal(_)))
            || directory == entry.path.is_empty()
        {
            return Err("invalid-artifact-path".into());
        }
        let (from, to) = if directory {
            (root.join(relative), destination.join(relative))
        } else {
            (root.to_owned(), destination.to_owned())
        };
        if metadata(&from)?.is_dir() != entry.is_directory {
            return Err("invalid-artifact-type".into());
        }
        if entry.is_directory {
            fs::create_dir(&to)?;
        } else {
            let mut source = input_file(&from)?;
            let mut target = output_file(&to)?;
            io::copy(&mut source, &mut target)?;
            target.sync_all()?;
        }
    }
    Ok(())
}

// MoveFileExW without REPLACE_EXISTING is a single no-clobber rename for files and directories.
#[allow(unsafe_code)]
fn publish(source: &Path, destination: &Path) -> Result<()> {
    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn MoveFileExW(from: *const u16, to: *const u16, flags: u32) -> i32;
    }
    let wide = |p: &Path| -> Result<Vec<u16>> {
        let name = p.file_name().ok_or("invalid-artifact-path")?;
        let dir = p.parent().ok_or("invalid-artifact-path")?.canonicalize()?;
        Ok(dir
            .join(name)
            .as_os_str()
            .encode_wide()
            .chain(Some(0))
            .collect())
    };
    let from = wide(source)?;
    let to = wide(destination)?;
    if unsafe { MoveFileExW(from.as_ptr(), to.as_ptr(), 0) } == 0 {
        return Err(io::Error::last_os_error().into());
    }
    Ok(())
}

pub(crate) fn run() -> Result<()> {
    let r: Request = serde_json::from_reader(io::stdin().lock()).map_err(|_| "invalid-request")?;
    match r.mode.as_str() {
        "fingerprint" => write_json(&fingerprint(&r.path, r.directory)?),
        "copy" => {
            copy(
                &r.path,
                r.destination.as_deref().ok_or("invalid-request")?,
                r.directory,
                r.files.ok_or("invalid-request")?,
            )?;
            write_json(&true)
        }
        "publish" => {
            let destination = r.destination.as_deref().ok_or("invalid-request")?;
            if metadata(&r.path)?.is_dir() != r.directory {
                return Err("invalid-artifact-type".into());
            }
            match publish(&r.path, destination) {
                Err(e) if e.key == "io" && e.detail == Some(17) && !r.directory => {
                    // ERROR_NOT_SAME_DEVICE. Stage on the destination volume so an
                    // interrupted copy cannot expose a partial document at its public name.
                    let scratch = r.scratch.as_deref().ok_or("invalid-request")?;
                    if scratch.parent() != destination.parent() || scratch == destination {
                        return Err("invalid-request".into());
                    }
                    let mut source = input_file(&r.path)?;
                    let mut target = output_file(scratch)?;
                    let copied = (|| -> Result<()> {
                        io::copy(&mut source, &mut target)?;
                        target.sync_all()?;
                        drop(target);
                        publish(scratch, destination)
                    })();
                    if copied.is_err() {
                        let _ = fs::remove_file(scratch);
                    }
                    copied?;
                    drop(source);
                    // The published file is complete. Workspace cleanup can retry removing the source.
                    let _ = fs::remove_file(&r.path);
                }
                result => result?,
            }
            write_json(&true)
        }
        _ => Err("invalid-request".into()),
    }
}
