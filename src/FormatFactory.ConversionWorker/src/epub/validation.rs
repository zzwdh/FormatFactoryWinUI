use super::{CONTAINER, DC, OPF};
use roxmltree::{Document, Node};
use serde::Serialize;
use std::collections::{HashMap, HashSet};
use std::fs::File;
use std::io::{Read, Seek};
use std::path::Path;
use zip::ZipArchive;

#[derive(Serialize)]
#[serde(rename_all = "PascalCase")]
pub(super) struct Report {
    errors: Vec<Issue>,
    warnings: Vec<Issue>,
    package_path: Option<String>,
}

#[derive(Serialize)]
#[serde(rename_all = "PascalCase")]
struct Issue {
    key: &'static str,
    argument: Option<String>,
}

impl Issue {
    fn new(key: &'static str, value: impl Into<String>) -> Self {
        Self {
            key,
            argument: Some(value.into()),
        }
    }
    fn simple(key: &'static str) -> Self {
        Self {
            key,
            argument: None,
        }
    }
    fn zip(e: impl std::fmt::Display) -> Self {
        Self::new("ConversionUiEpubZipInvalid", e.to_string())
    }
    fn xml(e: impl std::fmt::Display) -> Self {
        Self::new("ConversionUiEpubXmlInvalid", e.to_string())
    }
}
type Check<T> = std::result::Result<T, Issue>;

pub(super) fn validate(path: &Path) -> crate::Result<Report> {
    let mut report = Report {
        errors: Vec::new(),
        warnings: Vec::new(),
        package_path: None,
    };
    let mut file = match crate::input_file(path) {
        Ok(file) => file,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => {
            report
                .errors
                .push(Issue::simple("ConversionUiEpubFileMissing"));
            return Ok(report);
        }
        Err(e) => return Err(e.into()),
    };
    let mut header = [0; 30];
    if file.read_exact(&mut header).is_err() || header[..4] != *b"PK\x03\x04" {
        report
            .errors
            .push(Issue::simple("ConversionUiEpubNotValidZip"));
    } else {
        let length = u16::from_le_bytes([header[26], header[27]]) as usize;
        let mut name = vec![0; length];
        if file.read_exact(&mut name).is_err() || name != b"mimetype" {
            report
                .errors
                .push(Issue::simple("ConversionUiEpubMimetypeMustBeFirst"));
        }
        if header[8..10] != [0, 0] {
            report
                .errors
                .push(Issue::simple("ConversionUiEpubMimetypeUncompressed"));
        }
    }
    file.rewind()?;
    if let Err(issue) = inspect(file, &mut report) {
        report.errors.push(issue);
    }
    Ok(report)
}

fn entry_text(zip: &mut ZipArchive<File>, name: &str) -> Check<String> {
    let mut entry = zip.by_name(name).map_err(Issue::zip)?;
    // Only XML metadata, fixed-layout XHTML and mimetype are inflated here;
    // publication assets are addressed from the central directory.
    const MAX: u64 = 256 * 1024 * 1024;
    if entry.size() > MAX {
        return Err(Issue::xml("EPUB XML entry exceeds 256 MiB"));
    }
    let mut bytes = Vec::new();
    (&mut entry)
        .take(MAX + 1)
        .read_to_end(&mut bytes)
        .map_err(Issue::zip)?;
    if bytes.len() as u64 > MAX {
        return Err(Issue::xml("EPUB XML entry exceeds 256 MiB"));
    }
    if bytes.starts_with(&[0xff, 0xfe])
        || bytes.starts_with(&[0xfe, 0xff])
        || bytes.starts_with(b"<\0")
        || bytes.starts_with(b"\0<")
    {
        let le = bytes.starts_with(&[0xff, 0xfe]) || bytes.starts_with(b"<\0");
        let start = if bytes.starts_with(&[0xff, 0xfe]) || bytes.starts_with(&[0xfe, 0xff]) {
            2
        } else {
            0
        };
        if (bytes.len() - start) % 2 != 0 {
            return Err(Issue::xml("Incomplete UTF-16 code unit"));
        }
        let units: Vec<_> = bytes[start..]
            .chunks_exact(2)
            .map(|s| {
                if le {
                    u16::from_le_bytes([s[0], s[1]])
                } else {
                    u16::from_be_bytes([s[0], s[1]])
                }
            })
            .collect();
        return String::from_utf16(&units).map_err(Issue::xml);
    }
    let text = String::from_utf8(bytes).map_err(Issue::xml)?;
    Ok(text
        .strip_prefix('\u{feff}')
        .map(str::to_owned)
        .unwrap_or(text))
}

fn parse(text: &str) -> Check<Document<'_>> {
    Document::parse_with_options(
        text,
        roxmltree::ParsingOptions {
            allow_dtd: true,
            ..Default::default()
        },
    )
    .map_err(Issue::xml)
}

fn normalized(name: &str) -> String {
    name.replace('\\', "/").trim_start_matches('/').to_owned()
}

fn resolve(dir: &str, href: &str) -> String {
    let input = href.split('#').next().unwrap_or_default().as_bytes();
    let mut bytes = Vec::new();
    let mut i = 0;
    while i < input.len() {
        if input[i] == b'%'
            && let Some(pair) = input.get(i + 1..i + 3)
            && let Ok(s) = std::str::from_utf8(pair)
            && let Ok(b) = u8::from_str_radix(s, 16)
        {
            bytes.push(b);
            i += 3;
        } else {
            bytes.push(input[i]);
            i += 1;
        }
    }
    let path = normalized(&format!("{dir}/{}", String::from_utf8_lossy(&bytes)));
    let mut parts = Vec::new();
    for part in path.split('/') {
        match part {
            "" | "." => (),
            ".." => {
                parts.pop();
            }
            _ => parts.push(part),
        }
    }
    parts.join("/")
}

fn external(href: &str) -> bool {
    href.split_once(':').is_some_and(|(scheme, _)| {
        scheme.starts_with(|c: char| c.is_ascii_alphabetic())
            && scheme
                .chars()
                .all(|c| c.is_ascii_alphanumeric() || matches!(c, '+' | '-' | '.'))
    })
}

fn inspect(file: File, report: &mut Report) -> Check<()> {
    let mut zip = ZipArchive::new(file).map_err(Issue::zip)?;
    let mut entries = HashMap::new();
    for name in zip.file_names().filter(|s| !s.ends_with('/')) {
        if entries.insert(normalized(name), name.to_owned()).is_some() {
            return Err(Issue::zip("Duplicate entry path"));
        }
    }
    if let Some(name) = entries.get("mimetype") {
        if entry_text(&mut zip, name)? != "application/epub+zip" {
            report
                .errors
                .push(Issue::simple("ConversionUiEpubMimetypeContentInvalid"));
        }
    } else {
        report
            .errors
            .push(Issue::simple("ConversionUiEpubRootMimetypeMissing"));
    }
    let container_name = entries
        .get("META-INF/container.xml")
        .ok_or(Issue::simple("ConversionUiEpubContainerMissing"))?;
    let container = entry_text(&mut zip, container_name)?;
    let container = parse(&container)?;
    let package_path = container
        .descendants()
        .find(|n| n.has_tag_name((CONTAINER, "rootfile")))
        .and_then(|n| n.attribute("full-path"))
        .map(normalized)
        .filter(|s| !s.trim().is_empty())
        .ok_or(Issue::simple("ConversionUiEpubContainerMissingOpf"))?;
    let Some(package) = entries.get(&package_path) else {
        report.package_path = Some(package_path.clone());
        return Err(Issue::new("ConversionUiEpubOpfNotFound", package_path));
    };
    let package = entry_text(&mut zip, package)?;
    let package = parse(&package)?;
    validate_package(&package, &package_path, &entries, &mut zip, report)?;
    report.package_path = Some(package_path);
    Ok(())
}

fn child<'a, 's>(node: Node<'a, 's>, tag: &str) -> Option<Node<'a, 's>> {
    node.children().find(|n| n.has_tag_name((OPF, tag)))
}
fn content(node: Node) -> String {
    node.descendants()
        .filter(Node::is_text)
        .filter_map(|n| n.text())
        .collect()
}

fn validate_package(
    doc: &Document,
    path: &str,
    entries: &HashMap<String, String>,
    zip: &mut ZipArchive<File>,
    report: &mut Report,
) -> Check<()> {
    let root = doc.root_element();
    if !root.has_tag_name((OPF, "package")) {
        report
            .errors
            .push(Issue::simple("ConversionUiOpfRootMustBePackage"));
        return Ok(());
    }
    let metadata = child(root, "metadata");
    for key in ["identifier", "title", "language"] {
        if metadata
            .and_then(|m| m.children().find(|n| n.has_tag_name((DC, key))))
            .is_none_or(|n| content(n).trim().is_empty())
        {
            report
                .errors
                .push(Issue::new("ConversionUiOpfMetadataMissing", key));
        }
    }
    let fixed = metadata.is_some_and(|m| {
        m.children().any(|n| {
            n.has_tag_name((OPF, "meta"))
                && n.attribute("property") == Some("rendition:layout")
                && content(n).trim() == "pre-paginated"
        })
    });
    let items: Vec<_> = child(root, "manifest")
        .into_iter()
        .flat_map(|m| m.children())
        .filter(|n| n.has_tag_name((OPF, "item")))
        .collect();
    if items.is_empty() {
        report
            .errors
            .push(Issue::simple("ConversionUiOpfManifestNotEmpty"));
        return Ok(());
    }
    let dir = path.rsplit_once('/').map(|(dir, _)| dir).unwrap_or("");
    let mut manifest = HashMap::new();
    let mut duplicates = HashSet::new();
    let mut nav = false;
    for item in items {
        let (id, href, media) = (
            item.attribute("id").unwrap_or(""),
            item.attribute("href").unwrap_or(""),
            item.attribute("media-type").unwrap_or(""),
        );
        if !id.trim().is_empty() {
            if manifest.contains_key(id) && duplicates.insert(id) {
                report
                    .errors
                    .push(Issue::new("ConversionUiOpfManifestDuplicateId", id));
            }
            manifest.entry(id).or_insert((href, media));
        }
        if [id, href, media].iter().any(|s| s.trim().is_empty()) {
            report
                .errors
                .push(Issue::simple("ConversionUiOpfManifestItemFieldsRequired"));
            continue;
        }
        nav |= item
            .attribute("properties")
            .unwrap_or("")
            .split_whitespace()
            .any(|p| p == "nav");
        if !external(href) {
            let target = resolve(dir, href);
            if !entries.contains_key(&target) {
                report
                    .errors
                    .push(Issue::new("ConversionUiOpfManifestResourceMissing", target));
            }
        }
    }
    if !nav {
        report
            .errors
            .push(Issue::simple("ConversionUiEpubNavigationRequired"));
    }
    let mut spine = child(root, "spine")
        .into_iter()
        .flat_map(|s| s.children())
        .filter(|n| n.has_tag_name((OPF, "itemref")))
        .peekable();
    if spine.peek().is_none() {
        report
            .errors
            .push(Issue::simple("ConversionUiOpfSpineNotEmpty"));
    }
    for item in spine {
        let idref = item.attribute("idref");
        let Some(&(href, media)) = idref.and_then(|id| manifest.get(id)) else {
            report.errors.push(Issue {
                key: "ConversionUiOpfSpineMissingManifestId",
                argument: idref.map(str::to_owned),
            });
            continue;
        };
        if fixed && media == "application/xhtml+xml" {
            let target = resolve(dir, href);
            if let Some(name) = entries.get(&target) {
                let page = entry_text(zip, name)?;
                let page = parse(&page)?;
                if page
                    .descendants()
                    .find(|n| {
                        n.is_element()
                            && n.tag_name().name() == "meta"
                            && n.attribute("name") == Some("viewport")
                    })
                    .and_then(|n| n.attribute("content"))
                    .is_none_or(|s| s.trim().is_empty())
                {
                    report
                        .errors
                        .push(Issue::new("ConversionUiFixedLayoutViewportMissing", target));
                }
            }
        }
    }
    if root.attribute("version") != Some("3.0") {
        report
            .warnings
            .push(Issue::simple("ConversionUiOpfVersionRecommendation"));
    }
    Ok(())
}
