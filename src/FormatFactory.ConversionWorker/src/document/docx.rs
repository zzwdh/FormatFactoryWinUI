use super::{Block, Cell, Item, Row};
use crate::{Result, input_file, output_file, write_json};
use roxmltree::{Document as Xml, Node};
use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet};
use std::fs::{self, File};
use std::io::{self, Read, Write};
use std::path::{Path, PathBuf};
use zip::ZipArchive;
use zip::result::ZipError;

const W: &str = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
const R: &str = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
const REL: &str = "http://schemas.openxmlformats.org/package/2006/relationships";
const A: &str = "http://schemas.openxmlformats.org/drawingml/2006/main";
const WP: &str = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
const DC: &str = "http://purl.org/dc/elements/1.1/";
const DAMAGED: &str = "ConversionUiDocxDamagedOrEncrypted";

#[derive(Deserialize)]
#[serde(rename_all = "PascalCase")]
struct Request {
    path: PathBuf,
    assets: PathBuf,
    fallback_title: String,
}

#[derive(Serialize)]
#[serde(rename_all = "PascalCase")]
struct Content {
    title: String,
    language: String,
    blocks: Vec<Block>,
    resources: Vec<Resource>,
}

#[derive(Serialize)]
#[serde(rename_all = "PascalCase")]
struct Resource {
    id: String,
    media_type: &'static str,
    file_name: String,
}

#[derive(Default)]
struct Context {
    headings: HashMap<String, i32>,
    numbering: HashMap<String, bool>,
    notes: HashMap<String, Vec<Block>>,
    images: HashMap<String, String>,
}

fn xml(text: &str) -> Result<Xml<'_>> {
    Xml::parse(text).map_err(|_| DAMAGED.into())
}

fn optional_part(archive: &mut ZipArchive<File>, path: &str) -> Result<Option<String>> {
    let mut entry = match archive.by_name(path) {
        Ok(entry) => entry,
        Err(ZipError::FileNotFound) => return Ok(None),
        Err(_) => return Err(DAMAGED.into()),
    };
    let mut bytes = Vec::new();
    entry.read_to_end(&mut bytes).map_err(|_| DAMAGED)?;
    Ok(Some(xml_string(bytes)?))
}

fn xml_string(bytes: Vec<u8>) -> Result<String> {
    // XML byte-order signatures are authoritative, including unmarked UTF-16.
    let unicode32 = if bytes.starts_with(&[0xff, 0xfe, 0, 0]) {
        Some((true, 4))
    } else if bytes.starts_with(&[0, 0, 0xfe, 0xff]) {
        Some((false, 4))
    } else if bytes.starts_with(&[b'<', 0, 0, 0]) {
        Some((true, 0))
    } else if bytes.starts_with(&[0, 0, 0, b'<']) {
        Some((false, 0))
    } else {
        None
    };
    if let Some((le, skip)) = unicode32 {
        let mut text = String::new();
        let chunks = bytes[skip..].chunks_exact(4);
        if !chunks.remainder().is_empty() {
            return Err(DAMAGED.into());
        }
        for chunk in chunks {
            let unit = chunk.try_into().unwrap();
            text.push(
                char::from_u32(if le {
                    u32::from_le_bytes(unit)
                } else {
                    u32::from_be_bytes(unit)
                })
                .ok_or(DAMAGED)?,
            );
        }
        return Ok(text);
    }
    let unicode16 = if bytes.starts_with(&[0xff, 0xfe]) {
        Some((true, 2))
    } else if bytes.starts_with(&[0xfe, 0xff]) {
        Some((false, 2))
    } else if bytes.starts_with(&[b'<', 0]) {
        Some((true, 0))
    } else if bytes.starts_with(&[0, b'<']) {
        Some((false, 0))
    } else {
        None
    };
    if let Some((le, skip)) = unicode16 {
        let chunks = bytes[skip..].chunks_exact(2);
        if !chunks.remainder().is_empty() {
            return Err(DAMAGED.into());
        }
        return char::decode_utf16(chunks.map(|unit| {
            if le {
                u16::from_le_bytes([unit[0], unit[1]])
            } else {
                u16::from_be_bytes([unit[0], unit[1]])
            }
        }))
        .collect::<std::result::Result<String, _>>()
        .map_err(|_| DAMAGED.into());
    }
    if bytes.starts_with(&[0xef, 0xbb, 0xbf]) {
        return String::from_utf8(bytes[3..].to_vec()).map_err(|_| DAMAGED.into());
    }
    // All supported single-byte XML declarations start with the ASCII signature.
    if bytes.starts_with(b"<?xml")
        && let Some(end) = bytes.windows(2).position(|w| w == b"?>")
    {
        let declaration = std::str::from_utf8(&bytes[..end]).map_err(|_| DAMAGED)?;
        if let Some((_, remainder)) = declaration.split_once("encoding") {
            let value = remainder
                .trim_start()
                .strip_prefix('=')
                .ok_or(DAMAGED)?
                .trim_start();
            let quote = value
                .chars()
                .next()
                .filter(|q| matches!(q, '\'' | '"'))
                .ok_or(DAMAGED)?;
            let label = value[1..].split_once(quote).ok_or(DAMAGED)?.0;
            if !label.eq_ignore_ascii_case("utf-8") && !label.eq_ignore_ascii_case("utf8") {
                let encoding = encoding_rs::Encoding::for_label(label.as_bytes()).ok_or(DAMAGED)?;
                let decoded = encoding
                    .decode_without_bom_handling_and_without_replacement(&bytes)
                    .ok_or(DAMAGED)?;
                return Ok(decoded.into_owned());
            }
        }
    }
    String::from_utf8(bytes).map_err(|_| DAMAGED.into())
}

fn child<'a, 'input>(node: Node<'a, 'input>, name: &str) -> Option<Node<'a, 'input>> {
    node.children().find(|n| n.has_tag_name((W, name)))
}
fn style_value<'a>(node: Node<'a, '_>, name: &str) -> Option<&'a str> {
    child(node, name)?.attribute((W, "val"))
}
fn element_text(node: Node<'_, '_>) -> String {
    node.descendants()
        .filter(|n| n.is_text())
        .filter_map(|n| n.text())
        .collect()
}
fn paragraph_text(node: Node<'_, '_>) -> String {
    let mut out = String::new();
    for n in node.descendants().filter(|n| n.is_element()) {
        if n.has_tag_name((W, "t")) {
            out.push_str(&element_text(n));
        } else if n.has_tag_name((W, "tab")) {
            out.push('\t');
        } else if n.has_tag_name((W, "br")) {
            out.push_str("\r\n");
        }
    }
    out.trim().into()
}

impl Context {
    fn styles(&mut self, text: &str) -> Result<()> {
        let doc = xml(text)?;
        for n in doc.descendants().filter(|n| n.has_tag_name((W, "style"))) {
            let Some(id) = n.attribute((W, "styleId")) else {
                continue;
            };
            let mut level = child(n, "pPr")
                .and_then(|p| style_value(p, "outlineLvl"))
                .and_then(outline);
            if level.is_none()
                && let Some(name) = style_value(n, "name").filter(|s| {
                    s.get(..7)
                        .is_some_and(|s| s.eq_ignore_ascii_case("heading"))
                })
            {
                let digits: String = name.chars().filter(char::is_ascii_digit).collect();
                level = digits.parse::<i32>().ok().map(|n| n.clamp(1, 6));
            }
            if let Some(level) = level {
                self.headings.insert(id.into(), level);
            }
        }
        Ok(())
    }
    fn numbers(&mut self, text: &str) -> Result<()> {
        let doc = xml(text)?;
        let mut abstracts = HashMap::new();
        for n in doc
            .descendants()
            .filter(|n| n.has_tag_name((W, "abstractNum")))
        {
            if let Some(id) = n.attribute((W, "abstractNumId")) {
                let ordered = !n.descendants().any(|f| {
                    f.has_tag_name((W, "numFmt")) && f.attribute((W, "val")) == Some("bullet")
                });
                if abstracts.insert(id, ordered).is_some() {
                    return Err(DAMAGED.into());
                }
            }
        }
        for n in doc.descendants().filter(|n| n.has_tag_name((W, "num"))) {
            if let Some(id) = n.attribute((W, "numId")) {
                let ordered = style_value(n, "abstractNumId")
                    .and_then(|id| abstracts.get(id))
                    .copied()
                    .unwrap_or(true);
                if self.numbering.insert(id.to_owned(), ordered).is_some() {
                    return Err(DAMAGED.into());
                }
            }
        }
        Ok(())
    }
    fn footnotes(&mut self, text: &str) -> Result<()> {
        let doc = xml(text)?;
        for n in doc
            .descendants()
            .filter(|n| n.has_tag_name((W, "footnote")))
        {
            if let Some(id) = n
                .attribute((W, "id"))
                .filter(|s| s.trim().parse::<i32>().is_ok_and(|i| i >= 0))
            {
                let blocks = n
                    .children()
                    .filter(|p| p.has_tag_name((W, "p")))
                    .map(|p| Block::Paragraph {
                        text: paragraph_text(p),
                    })
                    .collect();
                if self.notes.insert(id.into(), blocks).is_some() {
                    return Err(DAMAGED.into());
                }
            }
        }
        Ok(())
    }
    fn paragraph(&self, node: Node<'_, '_>) -> Vec<Block> {
        let mut blocks = Vec::new();
        let text = paragraph_text(node);
        if !text.is_empty() {
            let level = child(node, "pPr").and_then(|p| {
                style_value(p, "outlineLvl").and_then(outline).or_else(|| {
                    style_value(p, "pStyle")
                        .and_then(|s| self.headings.get(s))
                        .copied()
                })
            });
            blocks.push(if let Some(level) = level {
                Block::Heading { level, text }
            } else {
                Block::Paragraph { text }
            });
        }
        for image in node.descendants().filter(|n| n.has_tag_name((A, "blip"))) {
            let Some(id) = image
                .attribute((R, "embed"))
                .and_then(|id| self.images.get(id))
            else {
                continue;
            };
            let container = image
                .ancestors()
                .find(|n| n.has_tag_name((WP, "inline")))
                .or_else(|| image.ancestors().find(|n| n.has_tag_name((WP, "anchor"))));
            let alt = container
                .and_then(|c| c.children().find(|n| n.has_tag_name((WP, "docPr"))))
                .and_then(|n| n.attribute("descr").or(n.attribute("title")))
                .map(str::to_owned);
            blocks.push(Block::Image {
                resource_id: id.clone(),
                alternative_text: alt,
                caption: None,
            });
        }
        for note in node
            .descendants()
            .filter(|n| n.has_tag_name((W, "footnoteReference")))
        {
            if let Some(id) = note.attribute((W, "id"))
                && let Some(blocks_for_note) = self.notes.get(id)
            {
                blocks.push(Block::Footnote {
                    id: format!("footnote-{id}"),
                    blocks: blocks_for_note.clone(),
                });
            }
        }
        if node
            .descendants()
            .any(|n| n.has_tag_name((W, "br")) && n.attribute((W, "type")) == Some("page"))
        {
            blocks.push(Block::PageBreak);
        }
        if blocks.is_empty() {
            blocks.push(Block::Paragraph {
                text: String::new(),
            });
        }
        blocks
    }

    fn blocks(&self, container: Node<'_, '_>, table_depth: usize) -> Result<Vec<Block>> {
        if table_depth > (super::JSON_DEPTH - 10) / 6 {
            return Err(super::DEPTH_ERROR.into());
        }
        let mut result = Vec::new();
        let mut pending: Option<(bool, Vec<Item>)> = None;
        // Expand block content controls in document order, retaining one list
        // across a content-control boundary just as across adjacent paragraphs.
        let mut iterators = vec![container.children()];
        while let Some(iter) = iterators.last_mut() {
            let Some(n) = iter.next() else {
                iterators.pop();
                continue;
            };
            if n.has_tag_name((W, "sdt")) {
                if let Some(body) = child(n, "sdtContent") {
                    iterators.push(body.children());
                }
            } else if n.has_tag_name((W, "p")) {
                let blocks = self.paragraph(n);
                let numbering = child(n, "pPr")
                    .and_then(|p| child(p, "numPr"))
                    .and_then(|p| style_value(p, "numId"));
                if let Some(id) = numbering {
                    let ordered = self.numbering.get(id).copied().unwrap_or(true);
                    if pending.as_ref().is_some_and(|list| list.0 != ordered) {
                        flush_list(&mut result, &mut pending);
                    }
                    pending
                        .get_or_insert_with(|| (ordered, Vec::new()))
                        .1
                        .push(Item { blocks });
                } else {
                    flush_list(&mut result, &mut pending);
                    result.extend(blocks);
                }
            } else if n.has_tag_name((W, "tbl")) {
                flush_list(&mut result, &mut pending);
                let mut rows = Vec::new();
                for row in n.children().filter(|row| row.has_tag_name((W, "tr"))) {
                    let mut cells = Vec::new();
                    for cell in row.children().filter(|cell| cell.has_tag_name((W, "tc"))) {
                        cells.push(Cell {
                            blocks: self.blocks(cell, table_depth + 1)?,
                            column_span: child(cell, "tcPr")
                                .and_then(|p| style_value(p, "gridSpan"))
                                .and_then(|v| v.trim().parse::<i32>().ok())
                                .unwrap_or(1)
                                .max(1),
                            row_span: 1,
                        });
                    }
                    rows.push(Row { cells });
                }
                result.push(Block::Table { rows });
            }
        }
        flush_list(&mut result, &mut pending);
        Ok(result)
    }
}

fn outline(text: &str) -> Option<i32> {
    text.trim()
        .parse::<i32>()
        .ok()
        .map(|n| n.saturating_add(1).clamp(1, 6))
}
fn flush_list(blocks: &mut Vec<Block>, pending: &mut Option<(bool, Vec<Item>)>) {
    if let Some((ordered, items)) = pending.take() {
        blocks.push(Block::List { ordered, items });
    }
}
fn resolve_target(target: &str) -> String {
    let normalized = target.replace('\\', "/");
    let mut parts = if target.starts_with('/') {
        Vec::new()
    } else {
        vec!["word"]
    };
    for part in normalized.split('/') {
        match part {
            "" | "." => {}
            ".." => {
                parts.pop();
            }
            part => parts.push(part),
        }
    }
    parts.join("/")
}

fn media_type(name: &str) -> &'static str {
    match name
        .rsplit_once('.')
        .map(|(_, ext)| ext.to_ascii_lowercase())
        .as_deref()
    {
        Some("jpg" | "jpeg") => "image/jpeg",
        Some("png") => "image/png",
        Some("gif") => "image/gif",
        Some("svg") => "image/svg+xml",
        Some("webp") => "image/webp",
        Some("bmp") => "image/bmp",
        Some("tif" | "tiff") => "image/tiff",
        _ => "application/octet-stream",
    }
}

fn resources(
    archive: &mut ZipArchive<File>,
    text: &str,
    directory: &Path,
    context: &mut Context,
) -> Result<Vec<Resource>> {
    let mut resources = Vec::new();
    let doc = xml(text)?;
    let mut ids = HashSet::new();
    for n in doc
        .descendants()
        .filter(|n| n.has_tag_name((REL, "Relationship")))
    {
        if n.attribute("TargetMode") == Some("External")
            || n.attribute("Type")
                != Some("http://schemas.openxmlformats.org/officeDocument/2006/relationships/image")
        {
            continue;
        }
        let (Some(id), Some(target)) = (n.attribute("Id"), n.attribute("Target")) else {
            continue;
        };
        if !ids.insert(id) {
            return Err(DAMAGED.into());
        }
        let path = resolve_target(target);
        let mut entry = match archive.by_name(&path) {
            Ok(entry) => entry,
            Err(ZipError::FileNotFound) => continue,
            Err(_) => return Err(DAMAGED.into()),
        };
        if resources.is_empty() {
            fs::create_dir_all(directory.parent().ok_or("invalid-document-request")?)?;
            fs::create_dir(directory)?;
        }
        // Relationship targets are ZIP member names. Never turn them into
        // filesystem destinations: the host owns numbered private staging files.
        let mut output = output_file(&directory.join(resources.len().to_string()))?;
        let count = io::copy(&mut entry, &mut output)?;
        if count != entry.size() {
            return Err(DAMAGED.into());
        }
        output.flush()?;
        let resource_id = format!("image-{id}");
        context.images.insert(id.to_owned(), resource_id.clone());
        let name = path.rsplit('/').next().unwrap_or_default();
        resources.push(Resource {
            id: resource_id,
            media_type: media_type(name),
            file_name: name.to_owned(),
        });
    }
    Ok(resources)
}

pub(crate) fn run() -> Result<()> {
    let req: Request =
        serde_json::from_reader(io::stdin().lock()).map_err(|_| "invalid-document-request")?;
    let mut archive = ZipArchive::new(input_file(&req.path)?).map_err(|_| DAMAGED)?;
    let text = optional_part(&mut archive, "word/document.xml")?
        .ok_or("ConversionUiDocxDocumentXmlMissing")?;
    let main = xml(&text)?;
    let body = child(main.root_element(), "body").ok_or("ConversionUiDocxBodyMissing")?;
    let mut context = Context::default();
    let assets = match optional_part(&mut archive, "word/_rels/document.xml.rels")? {
        Some(rels) => resources(&mut archive, &rels, &req.assets, &mut context)?,
        None => Vec::new(),
    };
    if let Some(part) = optional_part(&mut archive, "word/styles.xml")? {
        context.styles(&part)?;
    }
    if let Some(part) = optional_part(&mut archive, "word/numbering.xml")? {
        context.numbers(&part)?;
    }
    if let Some(part) = optional_part(&mut archive, "word/footnotes.xml")? {
        context.footnotes(&part)?;
    }
    let blocks = context.blocks(body, 0)?;
    let core = optional_part(&mut archive, "docProps/core.xml")?;
    let properties = core.as_deref().map(xml).transpose()?;
    let prop = |name| {
        properties
            .as_ref()?
            .descendants()
            .find(|n| n.has_tag_name((DC, name)))
            .map(element_text)
            .map(|v| v.trim().to_owned())
            .filter(|v| !v.is_empty())
    };
    let title = prop("title")
        .or_else(|| {
            blocks.iter().find_map(|b| match b {
                Block::Heading { text, .. } => Some(text.clone()),
                _ => None,
            })
        })
        .unwrap_or(req.fallback_title);
    let language = prop("language").unwrap_or_else(|| {
        main.descendants()
            .filter(|n| n.has_tag_name((W, "lang")))
            .filter_map(|n| n.attribute((W, "val")))
            .find(|v| !v.trim().is_empty())
            .unwrap_or("zh-CN")
            .to_owned()
    });
    write_json(&Content {
        title,
        language,
        blocks,
        resources: assets,
    })
}
