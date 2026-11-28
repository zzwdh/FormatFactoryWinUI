use super::{Error, Result};
use sha2::{Digest, Sha256};
use std::cmp::Ordering;
use std::collections::{BTreeMap, HashMap};
use std::fs;
use std::io::Read;
use std::os::windows::fs::MetadataExt;
use std::path::Path;

const MAX_ENTRIES: usize = 100_000;
type Structure = BTreeMap<String, (bool, u64)>;

// Win32 compares names ordinally. Unicode uppercasing alone would collapse
// different NTFS names such as "Straße" and "STRASSE".
struct WindowsName(Vec<u16>);

impl Ord for WindowsName {
    #[allow(unsafe_code)]
    fn cmp(&self, other: &Self) -> Ordering {
        #[link(name = "kernel32")]
        unsafe extern "system" {
            fn CompareStringOrdinal(
                first: *const u16,
                n: i32,
                second: *const u16,
                m: i32,
                ignore_case: i32,
            ) -> i32;
        }
        // Both slices have validated lengths below the Windows path limit.
        match unsafe {
            CompareStringOrdinal(
                self.0.as_ptr(),
                self.0.len() as i32,
                other.0.as_ptr(),
                other.0.len() as i32,
                1,
            )
        } {
            1 => Ordering::Less,
            2 => Ordering::Equal,
            3 => Ordering::Greater,
            _ => self.0.cmp(&other.0),
        }
    }
}

impl PartialOrd for WindowsName {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}
impl PartialEq for WindowsName {
    fn eq(&self, other: &Self) -> bool {
        self.cmp(other) == Ordering::Equal
    }
}
impl Eq for WindowsName {}

pub(super) fn same_windows_name(first: &str, second: &str) -> bool {
    WindowsName(first.encode_utf16().collect()) == WindowsName(second.encode_utf16().collect())
}

#[derive(Debug)]
pub(super) struct Listing {
    entries: Structure,
    pub encrypted: bool,
    pub total: u64,
}

fn validate_name(name: &str) -> Result<()> {
    if name.encode_utf16().count() > 30_000
        || name
            .chars()
            .any(|c| c.is_control() || "<>:\"|?*".contains(c))
    {
        return Err(Error::UnsafeEntry);
    }
    for part in name.split('/') {
        let stem = part
            .split('.')
            .next()
            .unwrap_or_default()
            .to_ascii_uppercase();
        let device = matches!(
            stem.as_str(),
            "CON" | "PRN" | "AUX" | "NUL" | "CONIN$" | "CONOUT$"
        ) || stem
            .strip_prefix("COM")
            .or_else(|| stem.strip_prefix("LPT"))
            .is_some_and(|n| {
                matches!(
                    n,
                    "1" | "2" | "3" | "4" | "5" | "6" | "7" | "8" | "9" | "¹" | "²" | "³"
                )
            });
        if part.is_empty() || part.ends_with('.') || part.ends_with(char::is_whitespace) || device {
            return Err(Error::UnsafeEntry);
        }
    }
    Ok(())
}

impl Listing {
    pub fn parse(text: &str, container: Option<&Path>) -> Result<Self> {
        let mut entries = Structure::new();
        let mut fields = HashMap::new();
        let mut encrypted = false;
        let mut total = 0u64;
        for line in text.lines().chain(std::iter::once("")) {
            if !line.is_empty() {
                if let Some((key, value)) = line.split_once(" = ") {
                    if (key == "Path" || !fields.is_empty()) && fields.insert(key, value).is_some()
                    {
                        return Err(Error::UnsafeEntry);
                    }
                }
                continue;
            }
            let Some(raw_name) = fields.remove("Path") else {
                continue;
            };
            let attributes = fields.get("Attributes").copied().unwrap_or_default();
            let directory = fields.get("Folder") == Some(&"+")
                || attributes
                    .split(' ')
                    .next()
                    .unwrap_or_default()
                    .contains('D')
                || attributes
                    .split_whitespace()
                    .any(|v| v.len() >= 10 && v.starts_with('d'));
            let special = fields
                .iter()
                .any(|(k, v)| k.to_ascii_lowercase().contains("link") && !v.is_empty())
                || attributes
                    .split_whitespace()
                    .any(|v| v.len() >= 10 && v.starts_with(['l', 's', 'p', 'b', 'c']))
                || fields
                    .get("Mode")
                    .is_some_and(|v| v.starts_with(['l', 's', 'p', 'b', 'c']))
                || fields.get("Anti") == Some(&"+");
            if special {
                return Err(Error::UnsafeEntry);
            }
            let normalized = raw_name.replace('\\', "/");
            if normalized.starts_with('/') {
                return Err(Error::UnsafeEntry);
            }
            let name = normalized.trim_end_matches('/').trim_start_matches("./");
            let size =
                parse_size(fields.get("Size").copied().unwrap_or("0")).ok_or(Error::UnsafeEntry)?;
            encrypted |= fields.get("Encrypted") == Some(&"+");
            fields.clear();
            if directory && matches!(name, "." | "") {
                continue;
            }
            validate_name(name)?;
            total = total
                .checked_add(size)
                .filter(|n| *n <= i64::MAX as u64)
                .ok_or(Error::UnsafeEntry)?;
            if entries
                .insert(
                    name.to_owned(),
                    (directory, if directory { 0 } else { size }),
                )
                .is_some()
                || entries.len() > MAX_ENTRIES
            {
                return Err(Error::UnsafeEntry);
            }
        }
        // XZ stores only an expanded size. 7-Zip derives the TAR name from its container.
        let filename = container
            .and_then(Path::file_name)
            .and_then(|s| s.to_str())
            .unwrap_or_default();
        let lower = filename.to_ascii_lowercase();
        if entries.is_empty() && (lower.ends_with(".tar.xz") || lower.ends_with(".txz")) {
            let size = text
                .lines()
                .find_map(|l| l.strip_prefix("Size = "))
                .and_then(parse_size)
                .ok_or(Error::CorruptArchive)?;
            let name = if lower.ends_with(".txz") {
                format!("{}.tar", &filename[..filename.len() - 4])
            } else {
                filename[..filename.len() - 3].to_owned()
            };
            validate_name(&name)?;
            entries.insert(name, (false, size));
            total = size;
        }
        // Include implicit directories in the identity check, so two different
        // spellings cannot silently collapse onto one Win32 directory on extraction.
        let mut names = BTreeMap::new();
        let mut parents = Vec::new();
        let mut name_bytes = 0usize;
        for (name, &(directory, _)) in &entries {
            let mut item = name.as_str();
            let mut is_directory = directory;
            loop {
                let key = WindowsName(item.encode_utf16().collect());
                if let Some(&(previous, was_directory)) = names.get(&key) {
                    if previous != item || was_directory != is_directory {
                        return Err(Error::UnsafeEntry);
                    }
                    break;
                }
                name_bytes += item.len();
                if name_bytes > 64 * 1024 * 1024 || names.len() == MAX_ENTRIES {
                    return Err(Error::UnsafeEntry);
                }
                names.insert(key, (item, is_directory));
                if !entries.contains_key(item) {
                    parents.push(item.to_owned());
                }
                let Some((parent, _)) = item.rsplit_once('/') else {
                    break;
                };
                item = parent;
                is_directory = true;
            }
        }
        for name in parents {
            entries.insert(name, (true, 0));
        }
        Ok(Self {
            entries,
            encrypted,
            total,
        })
    }

    pub fn snapshot(root: &Path) -> Result<Self> {
        let mut entries = Structure::new();
        let mut pending = vec![root.to_owned()];
        while let Some(dir) = pending.pop() {
            if fs::symlink_metadata(&dir)?.file_attributes() & 0x400 != 0 {
                return Err(Error::UnsafeEntry);
            }
            for item in fs::read_dir(&dir)? {
                let path = item?.path();
                let meta = fs::symlink_metadata(&path)?;
                if meta.file_attributes() & 0x400 != 0 {
                    return Err(Error::UnsafeEntry);
                }
                let name = path
                    .strip_prefix(root)
                    .ok()
                    .and_then(|p| p.to_str())
                    .ok_or(Error::UnsafeEntry)?
                    .replace('\\', "/");
                validate_name(&name)?;
                let directory = meta.is_dir();
                entries.insert(name, (directory, if directory { 0 } else { meta.len() }));
                if entries.len() > MAX_ENTRIES {
                    return Err(Error::UnsafeEntry);
                }
                if directory {
                    pending.push(path);
                }
            }
        }
        Ok(Self {
            entries,
            encrypted: false,
            total: 0,
        })
    }

    pub fn compare(&self, other: &Self) -> Result<()> {
        if self.entries == other.entries {
            Ok(())
        } else {
            Err(Error::ValidationFailed)
        }
    }

    pub fn single_file(&self) -> Option<&str> {
        if self.entries.len() != 1 {
            return None;
        }
        self.entries
            .iter()
            .find(|(_, (dir, _))| !dir)
            .map(|(name, _)| name.as_str())
    }

    pub fn hashes(&self, root: &Path) -> Result<BTreeMap<String, [u8; 32]>> {
        let mut hashes = BTreeMap::new();
        let mut buffer = vec![0; 128 * 1024];
        for (name, (directory, _)) in &self.entries {
            if *directory {
                continue;
            }
            let mut file = crate::input_file(&root.join(name))?;
            let mut hash = Sha256::new();
            loop {
                let read = file.read(&mut buffer)?;
                if read == 0 {
                    break;
                }
                hash.update(&buffer[..read]);
            }
            hashes.insert(name.clone(), hash.finalize().into());
        }
        Ok(hashes)
    }
}

fn parse_size(s: &str) -> Option<u64> {
    if s.is_empty() || !s.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    s.parse().ok().filter(|n| *n <= i64::MAX as u64)
}
