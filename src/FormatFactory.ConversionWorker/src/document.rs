pub(crate) mod docx;
pub(crate) mod docx_write;
mod markdown;

use crate::{Result, output_file, write_json};
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::io::{self, BufReader, Read, Write};
use std::path::PathBuf;

#[derive(Deserialize)]
#[serde(rename_all = "PascalCase")]
struct Document {
    format: String,
    output: PathBuf,
    title: String,
    blocks: Vec<Block>,
    links: HashMap<String, String>,
    image_label: String,
    footnote_label: String,
}

#[derive(Clone, Deserialize, Serialize)]
#[serde(tag = "Kind", rename_all_fields = "PascalCase")]
enum Block {
    Heading {
        level: i32,
        text: String,
    },
    Paragraph {
        text: String,
    },
    List {
        ordered: bool,
        items: Vec<Item>,
    },
    Table {
        rows: Vec<Row>,
    },
    Image {
        resource_id: String,
        alternative_text: Option<String>,
        caption: Option<String>,
    },
    PageBreak,
    Footnote {
        id: String,
        blocks: Vec<Block>,
    },
}
#[derive(Clone, Deserialize, Serialize)]
#[serde(rename_all = "PascalCase")]
struct Item {
    blocks: Vec<Block>,
}
#[derive(Clone, Deserialize, Serialize)]
#[serde(rename_all = "PascalCase")]
struct Row {
    cells: Vec<Cell>,
}
#[derive(Clone, Deserialize, Serialize)]
#[serde(rename_all = "PascalCase")]
struct Cell {
    blocks: Vec<Block>,
    column_span: i32,
    row_span: i32,
}

fn flatten(blocks: &[Block], label: &str) -> String {
    blocks
        .iter()
        .map(|block| match block {
            Block::Heading { text, .. } | Block::Paragraph { text } => text.clone(),
            Block::Image {
                alternative_text,
                caption,
                ..
            } => alternative_text
                .as_deref()
                .or(caption.as_deref())
                .unwrap_or(label)
                .into(),
            Block::List { items, .. } => items
                .iter()
                .map(|item| flatten(&item.blocks, label))
                .collect::<Vec<_>>()
                .join("; "),
            Block::Table { rows } => rows
                .iter()
                .map(|row| {
                    row.cells
                        .iter()
                        .map(|cell| flatten(&cell.blocks, label))
                        .collect::<Vec<_>>()
                        .join(" / ")
                })
                .collect::<Vec<_>>()
                .join("; "),
            Block::Footnote { blocks, .. } => flatten(blocks, label),
            Block::PageBreak => String::new(),
        })
        .filter(|s| !s.trim().is_empty())
        .collect::<Vec<_>>()
        .join(" ")
}

fn plain(out: &mut String, blocks: &[Block], depth: usize, doc: &Document) {
    use std::fmt::Write;
    let indent = "  ".repeat(depth);
    for block in blocks {
        match block {
            Block::Heading { level, text } => {
                let marker = "#".repeat((*level).clamp(1, 6) as usize);
                let _ = write!(out, "{indent}{marker} {text}\r\n\r\n");
            }
            Block::Paragraph { text } => {
                let _ = write!(out, "{indent}{text}\r\n\r\n");
            }
            Block::List { ordered, items } => {
                for (i, item) in items.iter().enumerate() {
                    out.push_str(&indent);
                    if *ordered {
                        let _ = write!(out, "{}. ", i + 1);
                    } else {
                        out.push_str("- ");
                    }
                    plain(out, &item.blocks, depth + 1, doc);
                }
                out.push_str("\r\n");
            }
            Block::Table { rows } => {
                for row in rows {
                    out.push_str(&indent);
                    out.push_str(
                        &row.cells
                            .iter()
                            .map(|cell| flatten(&cell.blocks, &doc.image_label))
                            .collect::<Vec<_>>()
                            .join("\t"),
                    );
                    out.push_str("\r\n");
                }
                out.push_str("\r\n");
            }
            Block::Image {
                alternative_text, ..
            } => {
                let _ = write!(out, "{indent}[{}", doc.image_label);
                if let Some(alt) = alternative_text.as_ref().filter(|s| !s.trim().is_empty()) {
                    let _ = write!(out, ": {alt}");
                }
                out.push_str("]\r\n\r\n");
            }
            Block::PageBreak => out.push_str("\x0c\r\n"),
            Block::Footnote { id, blocks } => {
                let _ = write!(out, "{indent}[{} {id}]\r\n", doc.footnote_label);
                plain(out, blocks, depth + 1, doc);
            }
        }
    }
}

const JSON_DEPTH: usize = 512;
const DEPTH_ERROR: &str = "document-nesting-limit-512";

pub(crate) fn with_document_stack(run: fn() -> Result<()>) -> Result<()> {
    // Deep tables alternate several Serde structures per cell. Keep that
    // bounded recursion off the small default Windows main-thread stack.
    std::thread::Builder::new()
        .stack_size(16 * 1024 * 1024)
        .spawn(run)?
        .join()
        .map_err(|_| "invalid-document-request")?
}

struct DepthReader<R> {
    source: R,
    depth: usize,
    quoted: bool,
    escape: bool,
}
impl<R: Read> Read for DepthReader<R> {
    fn read(&mut self, bytes: &mut [u8]) -> io::Result<usize> {
        let count = self.source.read(bytes)?;
        for &ch in &bytes[..count] {
            if self.quoted {
                if self.escape {
                    self.escape = false;
                } else if ch == b'\\' {
                    self.escape = true;
                } else if ch == b'"' {
                    self.quoted = false;
                }
            } else {
                match ch {
                    b'"' => self.quoted = true,
                    b'[' | b'{' => {
                        self.depth += 1;
                        if self.depth > JSON_DEPTH {
                            return Err(io::Error::new(io::ErrorKind::InvalidData, DEPTH_ERROR));
                        }
                    }
                    b']' | b'}' => self.depth = self.depth.saturating_sub(1),
                    _ => {}
                }
            }
        }
        Ok(count)
    }
}

pub(crate) fn run() -> Result<()> {
    let input = DepthReader {
        source: io::stdin().lock(),
        depth: 0,
        quoted: false,
        escape: false,
    };
    let mut json = serde_json::Deserializer::from_reader(BufReader::new(input));
    json.disable_recursion_limit();
    let doc = Document::deserialize(&mut json).map_err(|error| {
        if error.io_error_kind() == Some(io::ErrorKind::InvalidData) {
            DEPTH_ERROR
        } else {
            "invalid-document-request"
        }
    })?;
    json.end().map_err(|_| "invalid-document-request")?;
    let mut content = String::new();
    match doc.format.as_str() {
        "txt" => plain(&mut content, &doc.blocks, 0, &doc),
        "md" => content = markdown::render(&doc),
        _ => return Err("invalid-document-format".into()),
    }
    let mut output = output_file(&doc.output)?;
    output.write_all(content.trim_end().as_bytes())?;
    output.write_all(b"\r\n")?;
    output.flush()?;
    write_json(&true)
}
