mod validation;

use crate::{Failure, Result};
use serde::Deserialize;
use serde::de::DeserializeOwned;
use std::fs::File;
use std::io::{self, BufRead, Read, Write};
use std::path::PathBuf;
use zip::{CompressionMethod, ZipWriter, write::SimpleFileOptions};

const XHTML: &str = "http://www.w3.org/1999/xhtml";
const OPF: &str = "http://www.idpf.org/2007/opf";
const DC: &str = "http://purl.org/dc/elements/1.1/";
const CONTAINER: &str = "urn:oasis:names:tc:opendocument:xmlns:container";
const STYLES: &str = r#"html { writing-mode: horizontal-tb; }
body { font-family: serif; line-height: 1.6; margin: 5%; }
img { max-width: 100%; height: auto; }
body.fixed-layout { box-sizing: border-box; width: 100vw; height: 100vh; margin: 0; overflow: hidden; }
body.fixed-layout figure { box-sizing: border-box; width: 100%; height: 100%; margin: 0; }
body.fixed-layout img { width: 100%; height: 100%; object-fit: contain; }
table { border-collapse: collapse; max-width: 100%; }
th, td { border: 1px solid currentColor; padding: 0.25em; }
.page-break { break-before: page; }"#;

#[derive(Deserialize)]
struct Publication {
    path: PathBuf,
    identifier: String,
    title: String,
    language: String,
    creator: Option<String>,
    modified: String,
    fixed: bool,
    chapters: usize,
    resources: usize,
}
#[derive(Deserialize)]
struct Chapter {
    id: String,
    title: String,
    width: Option<i32>,
    height: Option<i32>,
    body: String,
}
#[derive(Deserialize)]
struct Resource {
    id: String,
    name: String,
    media: String,
    length: u64,
    path: Option<PathBuf>,
}

fn record<T: DeserializeOwned>(input: &mut impl BufRead) -> Result<T> {
    let mut json = Vec::new();
    if input.read_until(b'\n', &mut json)? == 0 {
        return Err("invalid-request".into());
    }
    serde_json::from_slice(&json).map_err(|_| "invalid-request".into())
}

fn xml(s: &str) -> String {
    let mut result = String::with_capacity(s.len());
    for c in s.chars() {
        match c {
            '&' => result.push_str("&amp;"),
            '<' => result.push_str("&lt;"),
            '>' => result.push_str("&gt;"),
            '"' => result.push_str("&quot;"),
            '\r' => result.push_str("&#13;"),
            '\n' => result.push_str("&#10;"),
            '\t' => result.push_str("&#9;"),
            _ => result.push(c),
        }
    }
    result
}

fn zip_error(e: zip::result::ZipError) -> Failure {
    match e {
        zip::result::ZipError::Io(io) => io.into(),
        _ => "invalid-epub".into(),
    }
}

fn text(
    zip: &mut ZipWriter<File>,
    path: &str,
    value: &str,
    method: CompressionMethod,
) -> Result<()> {
    zip.start_file(
        path,
        SimpleFileOptions::default().compression_method(method),
    )
    .map_err(zip_error)?;
    zip.write_all(value.as_bytes())?;
    Ok(())
}

fn create(input: &mut impl BufRead) -> Result<()> {
    let book: Publication = record(input)?;
    let mut zip = ZipWriter::new(crate::output_file(&book.path)?);
    let compressed = CompressionMethod::Deflated;
    text(
        &mut zip,
        "mimetype",
        "application/epub+zip",
        CompressionMethod::Stored,
    )?;
    text(
        &mut zip,
        "META-INF/container.xml",
        &format!(
            r#"<container xmlns="{CONTAINER}" version="1.0"><rootfiles><rootfile full-path="EPUB/package.opf" media-type="application/oebps-package+xml"/></rootfiles></container>"#
        ),
        compressed,
    )?;
    text(&mut zip, "EPUB/styles.css", STYLES, compressed)?;
    let lang = xml(&book.language);
    let title = xml(&book.title);
    let mut manifest = String::from(
        r#"<item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/><item id="styles" href="styles.css" media-type="text/css"/>"#,
    );
    let mut spine = String::new();
    let mut links = String::new();
    for index in 1..=book.chapters {
        let chapter: Chapter = record(input)?;
        let id = xml(&chapter.id);
        let caption = xml(&chapter.title);
        let filename = format!("chapter-{index:04}.xhtml");
        let viewport = if book.fixed {
            format!(
                r#"<meta name="viewport" content="width={},height={}"/>"#,
                chapter.width.unwrap_or(1200).max(1),
                chapter.height.unwrap_or(1600).max(1)
            )
        } else {
            String::new()
        };
        let xhtml = format!(
            r#"<html xmlns="{XHTML}" xml:lang="{lang}"><head><title>{caption}</title>{viewport}<link rel="stylesheet" type="text/css" href="styles.css"/></head>{}</html>"#,
            chapter.body
        );
        text(&mut zip, &format!("EPUB/{filename}"), &xhtml, compressed)?;
        links.push_str(&format!(r#"<li><a href="{filename}">{caption}</a></li>"#));
        manifest.push_str(&format!(
            r#"<item id="{id}" href="{filename}" media-type="application/xhtml+xml"/>"#
        ));
        spine.push_str(&format!(r#"<itemref idref="{id}"/>"#));
    }
    text(
        &mut zip,
        "EPUB/nav.xhtml",
        &format!(
            r#"<html xmlns="{XHTML}" xmlns:epub="http://www.idpf.org/2007/ops" xml:lang="{lang}"><head><title>{title}</title></head><body><nav epub:type="toc"><h1>{title}</h1><ol>{links}</ol></nav></body></html>"#
        ),
        compressed,
    )?;
    for _ in 0..book.resources {
        let resource: Resource = record(input)?;
        if resource.name.contains('\\')
            || resource
                .name
                .split('/')
                .any(|s| matches!(s, "" | "." | ".."))
        {
            return Err("invalid-request".into());
        }
        let href = format!("assets/{}", resource.name);
        let name = format!("EPUB/{href}");
        let options = SimpleFileOptions::default()
            .compression_method(compressed)
            .large_file(resource.length >= u32::MAX as u64);
        zip.start_file(name, options).map_err(zip_error)?;
        let read = if let Some(path) = resource.path {
            io::copy(&mut crate::input_file(&path)?, &mut zip)?
        } else {
            io::copy(&mut input.take(resource.length), &mut zip)?
        };
        if read != resource.length {
            return Err("invalid-epub".into());
        }
        manifest.push_str(&format!(
            r#"<item id="{}" href="{}" media-type="{}"/>"#,
            xml(&resource.id),
            xml(&href),
            xml(&resource.media)
        ));
    }
    let creator = book
        .creator
        .filter(|s| !s.trim().is_empty())
        .map(|s| format!("<dc:creator>{}</dc:creator>", xml(&s)))
        .unwrap_or_default();
    let layout = if book.fixed {
        r#"<meta property="rendition:layout">pre-paginated</meta><meta property="rendition:spread">none</meta><meta property="rendition:orientation">auto</meta>"#
    } else {
        ""
    };
    let package = format!(
        r#"<package xmlns="{OPF}" version="3.0" unique-identifier="pub-id" xml:lang="{lang}"><metadata xmlns:dc="{DC}"><dc:identifier id="pub-id">{}</dc:identifier><dc:title>{title}</dc:title><dc:language>{lang}</dc:language><meta property="dcterms:modified">{}</meta>{creator}{layout}</metadata><manifest>{manifest}</manifest><spine>{spine}</spine></package>"#,
        xml(&book.identifier),
        xml(&book.modified)
    );
    text(&mut zip, "EPUB/package.opf", &package, compressed)?;
    zip.finish().map_err(zip_error)?.flush()?;
    Ok(())
}

pub(crate) fn run(write: bool) -> Result<()> {
    let mut input = io::stdin().lock();
    if write {
        create(&mut input)?;
        io::stdout().lock().write_all(b"true")?;
    } else {
        #[derive(Deserialize)]
        struct Request {
            path: PathBuf,
        }
        let request: Request = serde_json::from_reader(input).map_err(|_| "invalid-request")?;
        let result = validation::validate(&request.path)?;
        serde_json::to_writer(io::stdout().lock(), &result).map_err(io::Error::other)?;
    }
    Ok(())
}
