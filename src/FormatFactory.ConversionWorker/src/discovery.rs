use crate::Result;
use serde::{Deserialize, Serialize};
use std::collections::{HashSet, VecDeque};
use std::fs;
use std::io::{self, BufWriter, Write};
use std::os::windows::fs::MetadataExt;
use std::path::PathBuf;

#[derive(Deserialize)]
#[serde(rename_all = "PascalCase")]
struct Scan {
    root: PathBuf,
    recursive: bool,
    skip_hidden: bool,
    ignore_inaccessible: bool,
    extensions: Option<Vec<String>>,
}

#[derive(Serialize)]
#[serde(rename_all = "PascalCase")]
struct FileEntry<'a> {
    path: &'a std::path::Path,
    length: u64,
    last_write_time_utc_ticks: u64,
    creation_time_utc_ticks: u64,
}

fn date_ticks(filetime: u64) -> u64 {
    const WINDOWS_EPOCH: u64 = 504_911_232_000_000_000;
    filetime
        .saturating_add(WINDOWS_EPOCH)
        .min(3_155_378_975_999_999_999)
}

pub(crate) fn run() -> Result<()> {
    let r: Scan = serde_json::from_reader(io::stdin().lock()).map_err(|_| "invalid-request")?;
    let extensions = r.extensions.map(|e| {
        e.into_iter()
            .map(|s| s.trim_start_matches('.').to_ascii_lowercase())
            .collect::<HashSet<_>>()
    });
    let mut pending = VecDeque::from([r.root]);
    let mut output = BufWriter::new(io::stdout().lock());
    let mask = 0x400 | if r.skip_hidden { 0x2 | 0x4 } else { 0 };
    while let Some(dir) = pending.pop_front() {
        let entries = match fs::read_dir(dir) {
            Ok(e) => e,
            Err(e) if r.ignore_inaccessible && e.kind() == io::ErrorKind::PermissionDenied => {
                continue;
            }
            Err(e) => return Err(e.into()),
        };
        for entry in entries {
            let (path, meta) = match entry.and_then(|e| Ok((e.path(), e.metadata()?))) {
                Ok(result) => result,
                Err(e)
                    if e.kind() == io::ErrorKind::NotFound
                        || r.ignore_inaccessible && e.kind() == io::ErrorKind::PermissionDenied =>
                {
                    continue;
                }
                Err(e) => return Err(e.into()),
            };
            if meta.file_attributes() & mask != 0 {
                continue;
            }
            if meta.is_dir() {
                if r.recursive {
                    pending.push_back(path);
                }
            } else if extensions.as_ref().is_none_or(|set| {
                // Match the host Path.GetExtension convention even for a filename such as .mp3.
                path.file_name()
                    .and_then(|s| s.to_str())
                    .and_then(|name| name.rsplit_once('.'))
                    .is_some_and(|(_, ext)| !ext.is_empty() && set.contains(&ext.to_ascii_lowercase()))
            }) {
                serde_json::to_writer(
                    &mut output,
                    &FileEntry {
                        path: &path,
                        length: meta.file_size(),
                        last_write_time_utc_ticks: date_ticks(meta.last_write_time()),
                        creation_time_utc_ticks: date_ticks(meta.creation_time()),
                    },
                )
                .map_err(io::Error::other)?;
                writeln!(output)?;
            }
        }
        output.flush()?;
    }
    Ok(())
}
