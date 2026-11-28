use super::{Error, Result, listing::same_windows_name};
use std::fs;
use std::path::Path;

#[derive(Clone, Copy)]
enum Group {
    SplitZip,
    PartRar,
    Numbered,
}

fn parse(name: &str, kind: Group) -> Option<(&str, &str)> {
    let (stem, number, width) = match kind {
        Group::SplitZip => {
            let (stem, number) = name.rsplit_once(".z")?;
            (stem, number, 2)
        }
        Group::PartRar => {
            let (stem, number) = name.strip_suffix(".rar")?.rsplit_once(".part")?;
            (stem, number, 1)
        }
        Group::Numbered => {
            let (stem, number) = name.rsplit_once('.')?;
            if !stem.ends_with(".7z") && !stem.ends_with(".zip") {
                return None;
            }
            (stem, number, 3)
        }
    };
    (!stem.is_empty() && number.len() >= width && number.bytes().all(|b| b.is_ascii_digit()))
        .then_some((stem, number))
}

pub(super) fn validate(primary: &Path) -> Result<()> {
    if !primary.is_file() {
        return Err(Error::MissingVolume);
    }
    let name = primary
        .file_name()
        .and_then(|s| s.to_str())
        .ok_or(Error::MissingVolume)?
        .to_ascii_lowercase();
    let (kind, stem) = if let Some(stem) = name.strip_suffix(".zip") {
        (Group::SplitZip, stem)
    } else if let Some((stem, _)) = parse(&name, Group::PartRar) {
        (Group::PartRar, stem)
    } else if let Some((stem, _)) = parse(&name, Group::Numbered) {
        (Group::Numbered, stem)
    } else {
        return Ok(());
    };
    let mut numbers = Vec::new();
    for entry in fs::read_dir(primary.parent().ok_or(Error::MissingVolume)?)? {
        let entry = entry?;
        let filename = entry.file_name();
        let Some(name) = filename.to_str() else {
            continue;
        };
        if let Some((candidate, number)) = parse(&name.to_ascii_lowercase(), kind)
            && same_windows_name(stem, candidate)
            && !entry.file_type()?.is_dir()
        {
            numbers.push(number.parse::<u64>().map_err(|_| Error::MissingVolume)?);
        }
    }
    numbers.sort_unstable();
    if (!matches!(kind, Group::SplitZip) && numbers.is_empty())
        || numbers.iter().enumerate().any(|(i, n)| *n != i as u64 + 1)
    {
        return Err(Error::MissingVolume);
    }
    Ok(())
}
