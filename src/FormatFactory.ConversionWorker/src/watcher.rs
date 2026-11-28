//! Win32 directory subscriptions. Keep the event, directory and buffer alive until
//! GetOverlappedResult acknowledges completion, including after CancelIoEx.
#![allow(unsafe_code)]
use crate::Result;
use serde::{Deserialize, Serialize};
use std::ffi::c_void;
use std::fs::{File, OpenOptions};
use std::io::{self, BufRead, Read, Write};
use std::os::windows::fs::OpenOptionsExt;
use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle, RawHandle};
use std::path::PathBuf;
use std::ptr::{null, null_mut};
use std::sync::Arc;

#[repr(C)]
struct Overlapped {
    internal: usize,
    transferred: usize,
    offset: u32,
    offset_high: u32,
    event: RawHandle,
}
#[link(name = "kernel32")]
unsafe extern "system" {
    fn CreateEventW(
        attrs: *const c_void,
        manual_reset: i32,
        initial: i32,
        name: *const u16,
    ) -> RawHandle;
    fn SetEvent(event: RawHandle) -> i32;
    fn WaitForMultipleObjects(count: u32, handles: *const RawHandle, all: i32, timeout: u32)
    -> u32;
    fn ReadDirectoryChangesW(
        directory: RawHandle,
        buffer: *mut c_void,
        size: u32,
        subtree: i32,
        filter: u32,
        count: *mut u32,
        ov: *mut Overlapped,
        routine: *const c_void,
    ) -> i32;
    fn CancelIoEx(directory: RawHandle, ov: *mut Overlapped) -> i32;
    fn GetOverlappedResult(
        directory: RawHandle,
        ov: *mut Overlapped,
        count: *mut u32,
        wait: i32,
    ) -> i32;
}

fn event() -> io::Result<OwnedHandle> {
    // Unnamed auto-reset event with default security, never inherited.
    let handle = unsafe { CreateEventW(null(), 0, 0, null()) };
    if handle.is_null() {
        Err(io::Error::last_os_error())
    } else {
        Ok(unsafe { OwnedHandle::from_raw_handle(handle) })
    }
}

struct Subscription {
    directory: File,
    completed: OwnedHandle,
    state: Box<Overlapped>,
    buffer: Box<[u32]>,
    pending: bool,
    subtree: bool,
}
impl Subscription {
    fn open(request: Request) -> io::Result<Self> {
        let directory = OpenOptions::new()
            .access_mode(1)
            .share_mode(7)
            .custom_flags(0x02000000 | 0x40000000)
            .open(request.path)?; // BACKUP_SEMANTICS | OVERLAPPED
        let completed = event()?;
        let state = Box::new(Overlapped {
            internal: 0,
            transferred: 0,
            offset: 0,
            offset_high: 0,
            event: completed.as_raw_handle(),
        });
        Ok(Self {
            directory,
            completed,
            state,
            buffer: vec![0; 16 * 1024].into_boxed_slice(),
            pending: false,
            subtree: request.subtree,
        })
    }

    fn submit(&mut self) -> io::Result<()> {
        debug_assert!(!self.pending);
        self.state.internal = 0;
        self.state.transferred = 0;
        // The directory, DWORD-aligned buffer and OVERLAPPED have stable addresses.
        // This method is called again only after the previous request completed.
        let ok = unsafe {
            ReadDirectoryChangesW(
                self.directory.as_raw_handle(),
                self.buffer.as_mut_ptr().cast(),
                (self.buffer.len() * 4) as u32,
                self.subtree.into(),
                0x01 | 0x02 | 0x08 | 0x10 | 0x40,
                null_mut(),
                self.state.as_mut(),
                null(),
            )
        };
        if ok == 0 {
            let error = io::Error::last_os_error();
            if error.raw_os_error() != Some(997) {
                return Err(error);
            }
        }
        self.pending = true;
        Ok(())
    }

    fn read(&mut self) -> Result<Vec<Change>> {
        let mut size = 0;
        let ok = unsafe {
            GetOverlappedResult(
                self.directory.as_raw_handle(),
                self.state.as_mut(),
                &mut size,
                1,
            )
        };
        self.pending = false;
        if ok == 0 {
            return Err(io::Error::last_os_error().into());
        }
        // A zero-byte completion has no records. Re-arm the same subscription.
        if size == 0 {
            return Ok(Vec::new());
        }
        if size as usize > self.buffer.len() * 4 {
            return Err("GeneralServiceDirectoryWatchInvalidRecord".into());
        }
        // The kernel finished writing this initialized buffer before this borrow.
        let bytes =
            unsafe { std::slice::from_raw_parts(self.buffer.as_ptr().cast::<u8>(), size as usize) };
        parse(bytes)
    }
}
impl Drop for Subscription {
    fn drop(&mut self) {
        if self.pending {
            unsafe {
                CancelIoEx(self.directory.as_raw_handle(), self.state.as_mut());
                let mut done = 0;
                GetOverlappedResult(
                    self.directory.as_raw_handle(),
                    self.state.as_mut(),
                    &mut done,
                    1,
                );
            }
        }
    }
}

#[derive(Deserialize)]
struct Request {
    path: PathBuf,
    subtree: bool,
}
#[derive(Serialize)]
#[serde(rename_all = "PascalCase")]
struct Change {
    action: u32,
    relative_path: String,
}

fn parse(mut bytes: &[u8]) -> Result<Vec<Change>> {
    let mut changes = Vec::new();
    loop {
        let header = bytes
            .get(..12)
            .ok_or("GeneralServiceDirectoryWatchInvalidRecord")?;
        let next = u32::from_le_bytes(header[..4].try_into().unwrap()) as usize;
        let action = u32::from_le_bytes(header[4..8].try_into().unwrap());
        let length = u32::from_le_bytes(header[8..].try_into().unwrap()) as usize;
        if length % 2 != 0 || length > bytes.len() - 12 {
            return Err("GeneralServiceDirectoryWatchInvalidRecord".into());
        }
        if (1..=5).contains(&action) {
            let path = char::decode_utf16(
                bytes[12..12 + length]
                    .chunks_exact(2)
                    .map(|bytes| u16::from_le_bytes([bytes[0], bytes[1]])),
            )
            .map(|c| c.unwrap_or(char::REPLACEMENT_CHARACTER))
            .collect();
            changes.push(Change {
                action,
                relative_path: path,
            });
        }
        if next == 0 {
            return Ok(changes);
        }
        if next < 12 + length || next % 4 != 0 || next >= bytes.len() {
            return Err("GeneralServiceDirectoryWatchInvalidOffset".into());
        }
        bytes = &bytes[next..];
    }
}

pub(crate) fn run() -> Result<()> {
    let mut line = String::new();
    io::stdin().lock().read_line(&mut line)?;
    let request: Request = serde_json::from_str(&line).map_err(|_| "invalid-request")?;
    let mut subscription = Subscription::open(request)?;
    let stop = Arc::new(event()?);
    let monitor = stop.clone();
    std::thread::Builder::new()
        .name("watch-owner".into())
        .spawn(move || {
            // Closing the host pipe (including host termination) stops an idle worker.
            let mut signal = [0];
            let _ = io::stdin().lock().read(&mut signal);
            unsafe {
                SetEvent(monitor.as_raw_handle());
            }
        })?;
    let waits = [stop.as_raw_handle(), subscription.completed.as_raw_handle()];
    subscription.submit().map_err(|error| crate::Failure {
        key: "GeneralServiceDirectoryWatchStartFailed",
        detail: error.raw_os_error().map(i64::from),
    })?;
    let mut stdout = io::stdout().lock();
    writeln!(stdout, "ready")?;
    stdout.flush()?;
    loop {
        match unsafe { WaitForMultipleObjects(2, waits.as_ptr(), 0, u32::MAX) } {
            0 => return Ok(()),
            1 => {
                let changes = subscription.read()?;
                subscription.submit()?;
                serde_json::to_writer(&mut stdout, &changes).map_err(io::Error::other)?;
                writeln!(stdout)?;
                stdout.flush()?;
            }
            _ => return Err(io::Error::last_os_error().into()),
        }
    }
}
