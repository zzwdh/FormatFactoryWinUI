use crate::{Failure, Result};
use serde::Deserialize;
use sha2::{Digest, Sha256};
use std::collections::{HashMap, HashSet};
use std::fs::{self, File, OpenOptions};
use std::io::{self, BufRead, BufReader, Read, Write};
use std::os::windows::fs::OpenOptionsExt;
use std::path::Path;
use std::thread;
use std::time::{Duration, Instant};

#[derive(Deserialize)]
#[serde(rename_all = "PascalCase")]
struct Open {
    path: String,
    temporary: String,
    #[serde(default)]
    direct: bool,
    #[serde(default)]
    hash: bool,
    #[serde(default)]
    directory_lock: bool,
}

#[derive(Deserialize)]
#[serde(rename_all = "PascalCase")]
struct Row {
    id: String,
    source: usize,
    grouped: bool,
}

#[derive(Deserialize)]
#[serde(rename_all = "PascalCase")]
struct Plan {
    mode: String,
    existing: Vec<Row>,
    incoming: Vec<Row>,
    known: Vec<String>,
}

// Indices address existing rows followed by incoming rows. JSON records stay in the
// host until it has installed its crash checkpoint and streams the selected snapshot.
fn selection(p: Plan) -> Result<Vec<usize>> {
    let offset = p.existing.len();
    match p.mode.as_str() {
        "snapshot" => {
            let known: HashSet<_> = p.known.into_iter().collect();
            let mut seen = HashSet::new();
            Ok(p.incoming
                .iter()
                .enumerate()
                .map(|(i, r)| (i + offset, r))
                .chain(
                    p.existing
                        .iter()
                        .enumerate()
                        .filter(|(_, r)| !known.contains(&r.id)),
                )
                .filter(|(_, r)| seen.insert(&r.id))
                .map(|(i, _)| i)
                .collect())
        }
        "discover" => {
            let mut order: Vec<_> = (0..offset).collect();
            let rows: Vec<_> = p.existing.iter().chain(&p.incoming).collect();
            let mut by_id = HashMap::new();
            let mut counts = HashMap::new();
            for (i, row) in p.existing.iter().enumerate() {
                by_id.entry(&row.id).or_insert(i);
                *counts.entry(row.source).or_insert(0usize) += 1;
            }
            for (i, r) in p.incoming.iter().enumerate() {
                if let Some(&pos) = by_id.get(&r.id) {
                    *counts.get_mut(&rows[order[pos]].source).unwrap() -= 1;
                    order[pos] = offset + i;
                } else {
                    if !r.grouped && counts.get(&r.source).copied().unwrap_or(0) > 0 {
                        continue;
                    }
                    by_id.insert(&r.id, order.len());
                    order.push(offset + i);
                }
                *counts.entry(r.source).or_insert(0) += 1;
            }
            Ok(order)
        }
        "history" if p.incoming.len() == 1 => {
            let new = &p.incoming[0];
            Ok(std::iter::once(offset)
                .chain(
                    p.existing
                        .iter()
                        .enumerate()
                        .filter(|(_, r)| r.id != new.id && r.source != new.source)
                        .map(|(i, _)| i),
                )
                .take(1000)
                .collect())
        }
        _ => Err("invalid-request".into()),
    }
}

fn lock(path: &Path) -> Result<File> {
    let started = Instant::now();
    loop {
        let result = OpenOptions::new()
            .read(true)
            .share_mode(0)
            .open(path)
            .or_else(|err| {
                if err.kind() != io::ErrorKind::NotFound {
                    return Err(err);
                }
                OpenOptions::new()
                    .read(true)
                    .write(true)
                    .share_mode(0)
                    .create_new(true)
                    .open(path)
            });
        match result {
            Ok(f) => return Ok(f),
            Err(e) if matches!(e.raw_os_error(), Some(32 | 33 | 80 | 183)) => {
                if started.elapsed() >= Duration::from_secs(30) {
                    return Err("store-lock-timeout".into());
                }
                thread::sleep(Duration::from_millis(50));
            }
            Err(e) => return Err(e.into()),
        }
    }
}

fn parse_line<T: serde::de::DeserializeOwned>(reader: &mut impl BufRead) -> Result<T> {
    let mut line = String::new();
    reader.read_line(&mut line)?;
    serde_json::from_str(&line).map_err(|_| Failure::from("invalid-request"))
}

fn frame_length(reader: &mut impl Read) -> Result<u64> {
    let mut header = [0; 4];
    reader.read_exact(&mut header)?;
    Ok(u64::from(u32::from_le_bytes(header)))
}

pub(crate) fn run() -> Result<()> {
    let mut input = BufReader::new(io::stdin().lock());
    let mut output = io::stdout().lock();
    let request: Open = parse_line(&mut input)?;
    let path = Path::new(&request.path);
    let tmp = Path::new(&request.temporary);
    if !path.is_absolute() || path.parent() != tmp.parent() || path == tmp {
        return Err("invalid-request".into());
    }
    fs::create_dir_all(path.parent().ok_or("invalid-request")?)?;
    let mut lock_name = path.as_os_str().to_owned();
    lock_name.push(".lock");
    let lock_path = if request.directory_lock {
        path.with_file_name(".receipts.lock")
    } else {
        lock_name.into()
    };
    let _lock = lock(&lock_path)?;
    writeln!(output, "ready")?;
    output.flush()?;
    let delete = if request.direct {
        false
    } else {
        let plan: Plan = parse_line(&mut input)?;
        let queue = plan.mode != "history";
        let order = selection(plan)?;
        serde_json::to_writer(&mut output, &order).map_err(io::Error::other)?;
        writeln!(output)?;
        output.flush()?;
        queue && order.is_empty()
    };

    // Only the explicit zero frame authorizes publication. EOF rolls back a broken pipe.
    let first = frame_length(&mut input)?;
    if delete || (request.direct && first == 0) {
        if first != 0 {
            return Err("invalid-request".into());
        }
        match fs::remove_file(path) {
            Err(e) if e.kind() != io::ErrorKind::NotFound => return Err(e.into()),
            _ => {}
        }
    } else {
        if first == 0 {
            return Err("invalid-request".into());
        }
        let mut file = OpenOptions::new()
            .write(true)
            .share_mode(0)
            .create_new(true)
            .open(tmp)?;
        let mut hash = request.hash.then(Sha256::new);
        let result = (|| -> Result<()> {
            let mut buffer = vec![0u8; 64 * 1024];
            let mut remaining = first;
            while remaining > 0 {
                while remaining > 0 {
                    let n = remaining.min(buffer.len() as u64) as usize;
                    input.read_exact(&mut buffer[..n])?;
                    file.write_all(&buffer[..n])?;
                    if let Some(h) = &mut hash {
                        h.update(&buffer[..n]);
                    }
                    remaining -= n as u64;
                }
                remaining = frame_length(&mut input)?;
            }
            file.sync_all()?;
            drop(file);
            fs::rename(tmp, path)?;
            Ok(())
        })();
        if result.is_err() {
            let _ = fs::remove_file(tmp);
        }
        result?;
        if let Some(h) = hash {
            writeln!(output, "{:X}", h.finalize())?;
        }
    }
    Ok(())
}
