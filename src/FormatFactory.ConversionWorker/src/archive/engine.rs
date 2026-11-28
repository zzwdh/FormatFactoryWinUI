use super::{Error, Result};
use std::ffi::{OsStr, OsString};
use std::io::{self, Read, Write};
use std::os::windows::process::CommandExt;
use std::path::Path;
use std::process::{Command, Stdio};

pub(super) struct Engine<'a>(pub &'a Path);

pub(super) struct Output {
    pub success: bool,
    pub listing: String,
    error: String,
}

impl Output {
    fn mentions(&self, strings: &[&str]) -> bool {
        let listing = self.listing.to_ascii_lowercase();
        let error = self.error.to_ascii_lowercase();
        strings
            .iter()
            .any(|s| listing.contains(s) || error.contains(s))
    }

    pub fn needs_password(&self, already_supplied: bool) -> bool {
        if self.mentions(&[
            "unsupported method",
            "missing volume",
            "access is denied",
            "not enough memory",
            "no space left",
            "can not open output file",
        ]) {
            return false;
        }
        self.mentions(&["wrong password", "data error in encrypted file"])
            || (!already_supplied && self.mentions(&["enter password"]))
    }

    pub fn read_error(&self) -> Error {
        if self.mentions(&["missing volume"])
            || (self.mentions(&["unexpected end of archive"]) && self.mentions(&["volume"]))
        {
            Error::MissingVolume
        } else {
            Error::CorruptArchive
        }
    }

    pub fn require(self, error: Error) -> Result<String> {
        if self.success {
            Ok(self.listing)
        } else {
            Err(error)
        }
    }
}

fn capture(stream: impl Read, max_units: u64) -> Result<String> {
    let mut data = Vec::new();
    // The public listing limit counts UTF-16 units. A non-ASCII filename
    // can require three UTF-8 bytes per unit.
    let max_bytes = max_units * 3;
    stream.take(max_bytes + 1).read_to_end(&mut data)?;
    if data.len() as u64 > max_bytes {
        return Err(Error::UnsafeEntry);
    }
    let text = String::from_utf8(data).map_err(|_| Error::UnsafeEntry)?;
    if text.len() as u64 > max_units && text.encode_utf16().count() as u64 > max_units {
        return Err(Error::UnsafeEntry);
    }
    Ok(text)
}

impl Engine<'_> {
    pub fn call(
        &self,
        switches: &[&str],
        paths: &[&OsStr],
        password: Option<&str>,
        cwd: Option<&Path>,
    ) -> Result<Output> {
        let mut command = Command::new(self.0);
        let mut child = command
            .args(switches)
            .args(paths)
            .args(["-sccUTF-8", "-bb0", "-bd"])
            .current_dir(
                cwd.or_else(|| self.0.parent())
                    .ok_or(Error::EngineUnavailable)?,
            )
            .creation_flags(0x0800_0000) // CREATE_NO_WINDOW
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .map_err(|_| Error::EngineUnavailable)?;
        let mut stdin = child.stdin.take().ok_or(Error::EngineFailed)?;
        let stdout = child.stdout.take().ok_or(Error::EngineFailed)?;
        let stderr = child.stderr.take().ok_or(Error::EngineFailed)?;
        std::thread::scope(|scope| {
            let output = scope.spawn(move || capture(stdout, 32 * 1024 * 1024));
            let errors = scope.spawn(move || capture(stderr, 256 * 1024));
            let input = scope.spawn(move || -> io::Result<()> {
                // A bare -p is used only when creating. On read 7-Zip prompts
                // automatically. No credential enters a command line, log or file.
                if let Some(secret) = password {
                    for _ in 0..2 {
                        stdin.write_all(secret.as_bytes())?;
                        stdin.write_all(b"\r\n")?;
                    }
                }
                Ok(())
            });
            let status = child.wait()?;
            let listing = output.join().map_err(|_| Error::EngineFailed)??;
            let error = errors.join().map_err(|_| Error::EngineFailed)??;
            if let Err(e) = input.join().map_err(|_| Error::EngineFailed)? {
                if e.kind() != io::ErrorKind::BrokenPipe {
                    return Err(e.into());
                }
            }
            Ok(Output {
                success: status.success(),
                listing,
                error,
            })
        })
    }

    pub fn list(&self, path: &Path, password: Option<&str>, tar: bool) -> Result<Output> {
        let mut args = vec!["l", "-slt", "-ba"];
        if tar {
            args.push("-ttar");
        }
        self.call(&args, &[path.as_os_str()], password, None)
    }

    pub fn extract(
        &self,
        path: &Path,
        dest: &Path,
        password: Option<&str>,
        tar: bool,
    ) -> Result<Output> {
        let mut args = vec!["x", "-y", "-aos"];
        if tar {
            args.push("-ttar");
        }
        let mut directory = OsString::from("-o");
        directory.push(dest);
        self.call(
            &args,
            &[directory.as_os_str(), path.as_os_str()],
            password,
            None,
        )
    }
}
