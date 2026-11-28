use std::fs;
use std::path::{Path, PathBuf};
use std::process::{Command, Output};
use std::sync::atomic::{AtomicUsize, Ordering};

struct Scratch(PathBuf);

impl Scratch {
    fn new() -> Self {
        static NEXT: AtomicUsize = AtomicUsize::new(0);
        let path = std::env::temp_dir().join(format!(
            "ffc-cso-{}-{}",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        ));
        fs::create_dir(&path).unwrap();
        Self(path)
    }
}

impl Drop for Scratch {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}

fn run(direction: &str, input: &Path, output: &Path, size: u32) -> Output {
    Command::new(env!("CARGO_BIN_EXE_format_factory_conversion_worker"))
        .args(["--protocol-v1", "disc-image", direction, "--input"])
        .arg(input)
        .arg("--output")
        .arg(output)
        .arg("--block-size")
        .arg(size.to_string())
        .output()
        .unwrap()
}

fn success(result: Output, total: usize, blocks: usize) {
    assert!(
        result.status.success(),
        "{}",
        String::from_utf8_lossy(&result.stderr)
    );
    let stdout = String::from_utf8(result.stdout).unwrap();
    let lines: Vec<_> = stdout.lines().collect();
    assert_eq!(lines[0], format!("progress 0 {total} 0 {blocks}"));
    assert_eq!(
        lines[lines.len() - 2],
        format!("progress {total} {total} {blocks} {blocks}")
    );
    assert_eq!(lines.last(), Some(&"ok"));
}

#[test]
fn round_trip_incompressible_compressed_and_short_blocks() {
    let scratch = Scratch::new();
    let source = scratch.0.join("原 件.iso");
    let encoded = scratch.0.join("映像.cso");
    let restored = scratch.0.join("还原.iso");
    let size = 262_144;
    let mut data = vec![0; size * 2 + 13];
    let mut random = 0xbadc_a11eu32;
    for byte in &mut data[..size] {
        random ^= random << 13;
        random ^= random >> 17;
        random ^= random << 5;
        *byte = random as u8;
    }
    // A partially random second block compresses to more than the reader's buffer.
    let (first, second) = data.split_at_mut(size);
    second[..size * 3 / 4].copy_from_slice(&first[..size * 3 / 4]);
    fs::write(&source, &data).unwrap();
    success(
        run("iso-to-cso", &source, &encoded, size as u32),
        data.len(),
        3,
    );
    let cso = fs::read(&encoded).unwrap();
    let first_entry = u32::from_le_bytes(cso[24..28].try_into().unwrap());
    let second_entry = u32::from_le_bytes(cso[28..32].try_into().unwrap());
    assert_ne!(first_entry >> 31, 0);
    assert_eq!(second_entry >> 31, 0);
    success(
        run("cso-to-iso", &encoded, &restored, size as u32),
        data.len(),
        3,
    );
    assert_eq!(fs::read(&restored).unwrap(), data);
    let duplicate = run("iso-to-cso", &source, &encoded, 512);
    assert!(!duplicate.status.success());
    assert_eq!(fs::read(encoded).unwrap(), cso);
}

#[test]
fn index_backpatch_preserves_blocks_across_batch_boundary() {
    let scratch = Scratch::new();
    let source = scratch.0.join("batch.iso");
    let encoded = scratch.0.join("batch.cso");
    let restored = scratch.0.join("restored.iso");
    let mut data = vec![0; 512 * 16_384 + 19];
    for (index, block) in data.chunks_mut(512).enumerate() {
        block.fill((index % 251) as u8);
    }
    fs::write(&source, &data).unwrap();
    success(
        run("iso-to-cso", &source, &encoded, 512),
        data.len(),
        16_385,
    );
    success(
        run("cso-to-iso", &encoded, &restored, 512),
        data.len(),
        16_385,
    );
    assert_eq!(fs::read(restored).unwrap(), data);
}

// Construct a historical zero header size and RFC 1951 stored final block
// independently of the worker's compressor. Its index is in four-byte units.
fn legacy() -> Vec<u8> {
    let mut bytes = Vec::from(*b"CISO");
    bytes.extend(0u32.to_le_bytes());
    bytes.extend(515u64.to_le_bytes());
    bytes.extend(512u32.to_le_bytes());
    bytes.extend([1, 2, 0, 0]);
    bytes.extend((0x8000_0000u32 | 9).to_le_bytes());
    bytes.extend(137u32.to_le_bytes());
    bytes.extend(139u32.to_le_bytes());
    bytes.resize(548, 0x42);
    bytes.extend([0x01, 3, 0, 0xfc, 0xff, b'x', b'y', b'z']);
    bytes
}

#[test]
fn legacy_decode_checks_output_length_and_rejects_truncated_deflate() {
    let scratch = Scratch::new();
    let source = scratch.0.join("legacy.cso");
    fs::write(&source, legacy()).unwrap();
    let good = scratch.0.join("good.iso");
    success(run("cso-to-iso", &source, &good, 2048), 515, 2);
    let mut expected = vec![0x42; 512];
    expected.extend(b"xyz");
    assert_eq!(fs::read(good).unwrap(), expected);
    let mut bad = legacy();
    bad[8..16].copy_from_slice(&514u64.to_le_bytes());
    fs::write(&source, bad).unwrap();
    let result = run("cso-to-iso", &source, &scratch.0.join("bad.iso"), 2048);
    assert!(!result.status.success());
    assert_eq!(
        String::from_utf8(result.stderr).unwrap().trim(),
        "error ConversionUiCsoBlockExpandsBeyondDeclaredSize 1"
    );
    let mut truncated = legacy();
    // Clear BFINAL. The same three bytes can be returned, but an end marker is required.
    truncated[548] = 0;
    fs::write(&source, truncated).unwrap();
    let result = run(
        "cso-to-iso",
        &source,
        &scratch.0.join("incomplete.iso"),
        2048,
    );
    assert!(!result.status.success());
    assert_eq!(
        String::from_utf8(result.stderr).unwrap().trim(),
        "error ConversionUiCsoBlockDataTruncatedOrInvalid 1"
    );
}
