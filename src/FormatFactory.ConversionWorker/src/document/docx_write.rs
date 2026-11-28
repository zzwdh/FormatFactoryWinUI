//! OPC/WordprocessingML writer for the text-and-page-break PDF extraction contract.
use crate::{Result, output_file, write_json};
use serde::Deserialize;
use std::fs::File;
use std::io::{self, BufRead, BufReader, BufWriter, Read, Write};
use std::path::PathBuf;
use unicode_general_category::{GeneralCategory, get_general_category};
use zip::{CompressionMethod, ZipWriter, write::SimpleFileOptions};

const W: &str = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
const TYPES: &[u8] = br#"<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/><Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/><Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/></Types>"#;
const ROOT_RELS: &[u8] = br#"<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/></Relationships>"#;
const WORD_RELS: &[u8] = br#"<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>"#;

#[derive(Deserialize)]
struct Request {
    output: PathBuf,
    properties: bool,
}

fn start(zip: &mut ZipWriter<File>, path: &str, large: bool) -> Result<()> {
    let options = SimpleFileOptions::default()
        .compression_method(CompressionMethod::Deflated)
        .large_file(large);
    zip.start_file(path, options).map_err(io::Error::from)?;
    Ok(())
}

// .NET can hold lone UTF-16 surrogates. Carry one across pipe chunks, drop
// unpaired units, and preserve real U+FFFD characters distinctly.
fn utf16(
    input: &mut impl Read,
    mut emit: impl FnMut(Option<char>) -> io::Result<()>,
) -> io::Result<()> {
    let mut length = [0; 4];
    input.read_exact(&mut length)?;
    let mut remaining = u32::from_le_bytes(length) as usize;
    let mut buffer = [0; 8192];
    let mut high: Option<u16> = None;
    while remaining != 0 {
        let count = remaining.min(buffer.len() / 2);
        input.read_exact(&mut buffer[..count * 2])?;
        for unit in buffer[..count * 2].chunks_exact(2) {
            let unit = u16::from_le_bytes([unit[0], unit[1]]);
            if let Some(lead) = high.take() {
                if (0xdc00..=0xdfff).contains(&unit) {
                    emit(char::from_u32(
                        0x10000 + ((u32::from(lead) - 0xd800) << 10) + u32::from(unit) - 0xdc00,
                    ))?;
                    continue;
                }
                emit(None)?;
            }
            if (0xd800..=0xdbff).contains(&unit) {
                high = Some(unit);
            } else {
                emit(char::from_u32(u32::from(unit)))?;
            }
        }
        remaining -= count;
    }
    if high.is_some() {
        emit(None)?;
    }
    Ok(())
}

fn is_xml(c: char) -> bool {
    matches!(c, '\t'|'\r'|'\n'|'\u{20}'..='\u{d7ff}'|'\u{e000}'..='\u{fffd}'|'\u{10000}'..)
}
fn escaped(c: char, target: &mut impl Write) -> io::Result<()> {
    match c {
        '&' => target.write_all(b"&amp;"),
        '<' => target.write_all(b"&lt;"),
        '>' => target.write_all(b"&gt;"),
        '"' => target.write_all(b"&quot;"),
        '\r' => target.write_all(b"&#13;"),
        '\n' => target.write_all(b"&#10;"),
        '\t' => target.write_all(b"&#9;"),
        c => target.write_all(c.encode_utf8(&mut [0; 4]).as_bytes()),
    }
}

fn metadata(input: &mut impl Read) -> Result<(String, bool)> {
    let mut text = Vec::new();
    let mut blank = true;
    utf16(input, |c| {
        if let Some(c) = c.filter(|c| is_xml(*c)) {
            blank &= c.is_whitespace();
            escaped(c, &mut text)?;
        }
        Ok(())
    })?;
    Ok((String::from_utf8(text).expect("escaped Unicode"), blank))
}

fn paragraph(input: &mut impl Read, target: &mut impl Write) -> Result<bool> {
    let mut visible = false;
    let mut in_text = false;
    let mut previous_cr = false;
    target.write_all(b"<w:p><w:r>")?;
    utf16(input, |scalar| {
        let skip_lf = previous_cr && scalar == Some('\n');
        previous_cr = scalar == Some('\r');
        let Some(c) = scalar.filter(|c| is_xml(*c)) else {
            return Ok(());
        };
        if skip_lf {
            return Ok(());
        }
        if matches!(c, '\r' | '\n' | '\t') {
            if in_text {
                target.write_all(b"</w:t>")?;
                in_text = false;
            }
            if c == '\t' {
                target.write_all(b"<w:tab/>")?;
            } else {
                target.write_all(b"</w:r></w:p><w:p><w:r>")?;
            }
        } else {
            if !in_text {
                target.write_all(b"<w:t xml:space=\"preserve\">")?;
                in_text = true;
            }
            if !visible {
                visible = !c.is_whitespace()
                    && !matches!(
                        get_general_category(c),
                        GeneralCategory::Control | GeneralCategory::Format
                    );
            }
            escaped(c, target)?;
        }
        Ok(())
    })?;
    if in_text {
        target.write_all(b"</w:t>")?;
    }
    target.write_all(b"</w:r></w:p>")?;
    Ok(visible)
}

pub(crate) fn run() -> Result<()> {
    let mut input = BufReader::new(io::stdin().lock());
    let mut header = String::new();
    input.read_line(&mut header)?;
    let request: Request = serde_json::from_str(&header).map_err(|_| "invalid-document-request")?;
    let (title, _) = metadata(&mut input)?;
    let (mut language, blank) = metadata(&mut input)?;
    if blank {
        language = "zh-CN".into();
    }
    let mut zip = ZipWriter::new(output_file(&request.output)?);
    if request.properties {
        for (path, data) in [
            ("[Content_Types].xml", TYPES),
            ("_rels/.rels", ROOT_RELS),
            ("word/_rels/document.xml.rels", WORD_RELS),
        ] {
            start(&mut zip, path, false)?;
            zip.write_all(data)?;
        }
        start(&mut zip, "docProps/core.xml", false)?;
        write!(
            zip,
            r#"<cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>{title}</dc:title><dc:language>{language}</dc:language></cp:coreProperties>"#
        )?;
        start(&mut zip, "word/styles.xml", false)?;
        write!(
            zip,
            r#"<w:styles xmlns:w="{W}"><w:docDefaults><w:rPrDefault><w:rPr><w:lang w:val="{language}" w:eastAsia="{language}"/></w:rPr></w:rPrDefault></w:docDefaults></w:styles>"#
        )?;
    } else {
        // Clipboard documents have no package title or language override.
        start(&mut zip, "[Content_Types].xml", false)?;
        zip.write_all(br#"<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>"#)?;
        start(&mut zip, "_rels/.rels", false)?;
        zip.write_all(br#"<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>"#)?;
    }
    start(&mut zip, "word/document.xml", true)?;
    let mut visible = false;
    {
        let mut body = BufWriter::with_capacity(64 * 1024, &mut zip);
        write!(body, r#"<w:document xmlns:w="{W}"><w:body>"#)?;
        loop {
            let mut kind = [0];
            input.read_exact(&mut kind)?;
            match kind[0] {
                b'P' => visible |= paragraph(&mut input, &mut body)?,
                b'B' => body.write_all(b"<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>")?,
                b'E' => break,
                _ => return Err("invalid-document-request".into()),
            }
        }
        if !visible {
            return Err("PdfDocxNoText".into());
        }
        body.write_all(b"</w:body></w:document>")?;
        body.flush()?;
    }
    zip.finish().map_err(io::Error::from)?.flush()?;
    write_json(&true)
}
