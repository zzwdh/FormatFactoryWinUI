use flate2::{Decompress, FlushDecompress, Status};
use std::collections::BTreeMap;
use std::time::{Duration, Instant};

pub(crate) const MAX_ARCHIVE: usize = 64 * 1024 * 1024;
const MAX_ENTRIES: usize = 4096;
pub(crate) const MAX_COVER: usize = 8 * 1024 * 1024;

#[derive(Debug)]
pub(crate) enum Error {
    Invalid,
    Timeout,
}
pub(crate) type Result<T> = std::result::Result<T, Error>;

fn check(ok: bool) -> Result<()> {
    if ok { Ok(()) } else { Err(Error::Invalid) }
}
fn check_time(deadline: Instant) -> Result<()> {
    if Instant::now() > deadline {
        Err(Error::Timeout)
    } else {
        Ok(())
    }
}
fn word(b: &[u8], p: usize) -> Result<u16> {
    b.get(p..p + 2)
        .and_then(|s| s.try_into().ok())
        .map(u16::from_le_bytes)
        .ok_or(Error::Invalid)
}
fn dword(b: &[u8], p: usize) -> Result<u32> {
    b.get(p..p + 4)
        .and_then(|s| s.try_into().ok())
        .map(u32::from_le_bytes)
        .ok_or(Error::Invalid)
}
fn extra_fields(extra: &[u8]) -> Result<()> {
    let mut p = 0;
    while p < extra.len() {
        check(word(extra, p)? != 1)?;
        let len = usize::from(word(extra, p + 2)?);
        p += 4 + len;
        check(p <= extra.len())?;
    }
    Ok(())
}

struct Entry {
    method: u16,
    flags: u16,
    local: usize,
    packed: usize,
    size: usize,
    crc: u32,
}

pub(crate) struct Index {
    size: usize,
    directory: usize,
    entries: BTreeMap<Vec<u8>, Entry>,
    deadline: Instant,
}

impl Index {
    pub(crate) fn open(bytes: &[u8]) -> Result<Self> {
        check((22..=MAX_ARCHIVE).contains(&bytes.len()))?;
        let deadline = Instant::now() + Duration::from_secs(3);
        let floor = bytes.len().saturating_sub(65557);
        let end = (floor..=bytes.len() - 22)
            .rev()
            .find(|&p| {
                bytes[p..].starts_with(b"PK\x05\x06")
                    && word(bytes, p + 20).is_ok_and(|n| p + 22 + usize::from(n) == bytes.len())
            })
            .ok_or(Error::Invalid)?;
        let count = usize::from(word(bytes, end + 10)?);
        check(
            count <= MAX_ENTRIES
                && dword(bytes, end + 4)? == 0
                && usize::from(word(bytes, end + 8)?) == count,
        )?;
        let directory = dword(bytes, end + 16)? as usize;
        let finish = directory
            .checked_add(dword(bytes, end + 12)? as usize)
            .ok_or(Error::Invalid)?;
        check(finish <= end)?;
        let mut entries = BTreeMap::new();
        let mut p = directory;
        for _ in 0..count {
            check_time(deadline)?;
            check(p + 46 <= finish && dword(bytes, p)? == 0x02014b50 && word(bytes, p + 34)? == 0)?;
            let flags = word(bytes, p + 8)?;
            let packed = dword(bytes, p + 20)?;
            let size = dword(bytes, p + 24)?;
            let local = dword(bytes, p + 42)?;
            check(flags & 1 == 0 && ![packed, size, local].contains(&u32::MAX))?;
            let name_end = p + 46 + usize::from(word(bytes, p + 28)?);
            let extra_end = name_end + usize::from(word(bytes, p + 30)?);
            let next = extra_end + usize::from(word(bytes, p + 32)?);
            check(next <= finish)?;
            extra_fields(&bytes[name_end..extra_end])?;
            let name = &bytes[p + 46..name_end];
            check(!name.contains(&0))?;
            let entry = Entry {
                method: word(bytes, p + 10)?,
                flags,
                crc: dword(bytes, p + 16)?,
                local: local as usize,
                packed: packed as usize,
                size: size as usize,
            };
            check(entries.insert(name.to_vec(), entry).is_none())?;
            p = next;
        }
        check(p == finish)?;
        Ok(Self {
            size: bytes.len(),
            directory,
            entries,
            deadline,
        })
    }

    pub(crate) fn member_size(&self, name: &[u8], limit: usize) -> Result<usize> {
        check_time(self.deadline)?;
        let entry = self.entries.get(name).ok_or(Error::Invalid)?;
        check(
            entry.size <= limit.min(MAX_COVER)
                && (entry.size <= 1024 * 1024
                    || entry.size as u64 <= 200 * (entry.packed as u64).max(1)),
        )?;
        Ok(entry.size)
    }

    pub(crate) fn extract(&self, bytes: &[u8], name: &[u8], output: &mut [u8]) -> Result<()> {
        check(self.size == bytes.len() && self.member_size(name, output.len())? == output.len())?;
        let entry = self.entries.get(name).ok_or(Error::Invalid)?;
        let p = entry.local;
        check(
            p < self.directory
                && dword(bytes, p)? == 0x04034b50
                && word(bytes, p + 8)? == entry.method,
        )?;
        let flags = word(bytes, p + 6)?;
        check(flags == entry.flags && flags & (1 | 0x40 | 0x2000) == 0)?;
        let name_start = p + 30;
        let name_end = name_start + usize::from(word(bytes, p + 26)?);
        let start = name_end + usize::from(word(bytes, p + 28)?);
        check(start <= self.directory && bytes.get(name_start..name_end) == Some(name))?;
        if flags & 8 == 0 {
            check(
                dword(bytes, p + 14)? == entry.crc
                    && dword(bytes, p + 18)? as usize == entry.packed
                    && dword(bytes, p + 22)? as usize == entry.size,
            )?;
        }
        check(entry.packed <= self.directory - start)?;
        let data = &bytes[start..start + entry.packed];
        match entry.method {
            0 => {
                check(data.len() == output.len())?;
                output.copy_from_slice(data);
            }
            8 => self.inflate(data, output)?,
            _ => return Err(Error::Invalid),
        }
        check(crc32fast::hash(output) == entry.crc)?;
        check_time(self.deadline)
    }

    fn inflate(&self, data: &[u8], output: &mut [u8]) -> Result<()> {
        let mut inflater = Decompress::new(false);
        let mut scratch = [0u8; 1];
        loop {
            check_time(self.deadline)?;
            let consumed = inflater.total_in() as usize;
            let written = inflater.total_out() as usize;
            check(written <= output.len())?;
            // Limit input as well as output. Long runs of empty DEFLATE blocks must
            // return to the deadline check even when they produce no output.
            let end_in = (consumed + 32 * 1024).min(data.len());
            let end_out = (written + 32 * 1024).min(output.len());
            let tail = if written == output.len() {
                &mut scratch[..]
            } else {
                &mut output[written..end_out]
            };
            let status = inflater
                .decompress(&data[consumed..end_in], tail, FlushDecompress::None)
                .map_err(|_| Error::Invalid)?;
            if status == Status::StreamEnd {
                return check(
                    inflater.total_out() == output.len() as u64
                        && inflater.total_in() == data.len() as u64,
                );
            }
            check(
                inflater.total_in() != consumed as u64 || inflater.total_out() != written as u64,
            )?;
        }
    }
}

pub(crate) fn resolve(base: &[u8], href: &[u8]) -> Result<String> {
    let base = std::str::from_utf8(base).map_err(|_| Error::Invalid)?;
    let href = href
        .split(|&c| c == b'?' || c == b'#')
        .next()
        .ok_or(Error::Invalid)?;
    let mut decoded = Vec::with_capacity(href.len());
    let mut i = 0;
    while i < href.len() {
        if href[i] == b'%' {
            let digit = |pos| {
                href.get(pos)
                    .and_then(|&v| char::from(v).to_digit(16))
                    .ok_or(Error::Invalid)
            };
            decoded.push((digit(i + 1)? * 16 + digit(i + 2)?) as u8);
            i += 3;
        } else {
            decoded.push(href[i]);
            i += 1;
        }
    }
    let decoded = std::str::from_utf8(&decoded).map_err(|_| Error::Invalid)?;
    check(
        !decoded.is_empty() && !decoded.starts_with('/') && !decoded.contains(['\\', ':', '\0']),
    )?;
    let prefix = base.rsplit_once('/').map_or("", |(parent, _)| parent);
    let mut parts = Vec::new();
    for part in prefix.split('/').chain(decoded.split('/')) {
        match part {
            "" | "." => {}
            ".." => {
                parts.pop().ok_or(Error::Invalid)?;
            }
            _ => parts.push(part),
        }
    }
    check(!parts.is_empty())?;
    Ok(parts.join("/"))
}
