#![cfg(windows)]

use serde_json::json;
use std::fs;
use std::io::{BufRead, BufReader, Write};
use std::path::Path;
use std::process::{Command, Stdio};

fn snapshot(root: &Path, bytes: &[u8], commit: bool) -> bool {
    let mut worker = Command::new(env!("CARGO_BIN_EXE_format_factory_conversion_worker"))
        .args(["--protocol-v1", "store-transaction"])
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    let mut input = worker.stdin.take().unwrap();
    let mut output = BufReader::new(worker.stdout.take().unwrap());
    writeln!(
        input,
        "{}",
        json!({"Path":root.join("queue.json"),"Temporary":root.join("private.tmp")})
    )
    .unwrap();
    let mut reply = String::new();
    output.read_line(&mut reply).unwrap();
    assert_eq!(reply.trim(), "ready");
    writeln!(input, "{}", json!({"Mode":"snapshot","Existing":[],"Incoming":[{"Id":"id","Source":0,"Grouped":false}],"Known":[]})).unwrap();
    reply.clear();
    output.read_line(&mut reply).unwrap();
    assert_eq!(reply.trim(), "[0]");
    for data in bytes.chunks(7) {
        input.write_all(&(data.len() as u32).to_le_bytes()).unwrap();
        input.write_all(data).unwrap();
    }
    if commit {
        input.write_all(&[0; 4]).unwrap();
    }
    drop(input);
    worker.wait().unwrap().success()
}

#[test]
fn host_disconnect_after_complete_json_preserves_previous_snapshot_and_releases_lock() {
    let root = std::env::temp_dir().join(format!("ffc-store-{}", std::process::id()));
    fs::create_dir(&root).unwrap();
    let path = root.join("queue.json");
    fs::write(&path, b"old").unwrap();
    let snapshot_bytes = br#"{"SchemaVersion":4,"Items":[{"InputPath":"new"}]}"#;
    assert!(!snapshot(&root, snapshot_bytes, false));
    assert_eq!(fs::read(&path).unwrap(), b"old");
    assert!(!root.join("private.tmp").exists());
    assert!(snapshot(&root, snapshot_bytes, true));
    assert_eq!(fs::read(&path).unwrap(), snapshot_bytes);
    fs::remove_dir_all(root).unwrap();
}
