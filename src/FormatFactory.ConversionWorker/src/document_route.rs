use crate::{Result, write_json};
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::io;

const NORMALIZED: &str = "normalized-document";
const UNSUPPORTED: &str = "ConversionUiRouteNotImplemented";
const CAJ: [&str; 4] = ["caj", "hn", "c8", "kdh"];

#[derive(Deserialize)]
#[serde(tag = "kind", rename_all = "lowercase")]
enum Request {
    Catalog,
    Plan {
        source: String,
        target: String,
        image: bool,
        fixed: bool,
        tools: Vec<String>,
    },
}

#[derive(Serialize)]
#[serde(rename_all = "PascalCase")]
struct Step {
    engine_id: &'static str,
    source: String,
    target: String,
}
#[derive(Serialize)]
#[serde(rename_all = "PascalCase")]
struct Response {
    steps: Vec<Step>,
    error: Option<&'static str>,
}

fn route(
    source: &str,
    target: &str,
    image: bool,
    fixed: bool,
    tools: &[String],
) -> std::result::Result<Vec<Step>, &'static str> {
    let available = |id: &str| tools.iter().any(|s| s == id);
    let mut steps = Vec::new();
    let mut from = source.to_owned();
    let mut add = |engine_id: &'static str, to: &str| {
        steps.push(Step {
            engine_id,
            source: std::mem::replace(&mut from, to.to_owned()),
            target: to.to_owned(),
        });
    };
    let kindle = matches!(target, "mobi" | "azw3");
    let book = target == "epub" || kindle;
    let normalized = book || matches!(target, "md" | "txt");
    let office = || {
        if available("MicrosoftWord") {
            Ok("word")
        } else if available("LibreOffice") {
            Ok("libreoffice")
        } else {
            Err("ConversionUiDocToPdfEngineRequired")
        }
    };
    if source == "docx" && fixed && kindle {
        return Err("DocumentCompatibilityFixedEpubOnly");
    }
    if CAJ.contains(&source) && (normalized || target == "pdf") {
        if !available("Caj2Pdf") {
            return Err("ConversionUiRequiresCaj2Pdf");
        }
        add("caj2pdf", "pdf");
        if target == "pdf" {
            return Ok(steps);
        }
        add(
            if fixed && book {
                "pdf-renderer"
            } else {
                "pdf-extractor"
            },
            NORMALIZED,
        );
    } else {
        match (source, target) {
            ("docx", "epub") if fixed => {
                add(office()?, "pdf");
                add("pdf-renderer", NORMALIZED);
            }
            ("doc" | "docx", "pdf") => {
                add(office()?, "pdf");
                return Ok(steps);
            }
            ("docx", _) if normalized => add("docx-reader", NORMALIZED),
            ("doc", _) if normalized => {
                if available("LibreOffice") {
                    add("libreoffice", "docx");
                    add("docx-reader", NORMALIZED);
                } else {
                    add("windows-ifilter", NORMALIZED);
                }
            }
            ("pdf", "pdf") => {
                add("pdf-ocr", "pdf");
                return Ok(steps);
            }
            ("pdf", _) if normalized || target == "docx" => add(
                if fixed && book {
                    "pdf-renderer"
                } else {
                    "pdf-extractor"
                },
                NORMALIZED,
            ),
            ("epub", _) if kindle => {
                if !available("Calibre") {
                    return Err("ConversionUiRequiresCalibre");
                }
                add("calibre", target);
                return Ok(steps);
            }
            (_, "pdf") if image => {
                add("image-decoder", "image-pages");
                add("image-pdf", "pdf");
                return Ok(steps);
            }
            (_, _) if image && normalized => add("image-ocr", NORMALIZED),
            _ => return Err(UNSUPPORTED),
        }
    }
    match target {
        "txt" => add("text-writer", "txt"),
        "md" => add("markdown-writer", "md"),
        "docx" => add("docx-writer", "docx"),
        _ => add("epub-writer", "epub"),
    }
    if kindle {
        if !available("Calibre") {
            return Err("ConversionUiRequiresCalibre");
        }
        add("calibre", target);
    }
    Ok(steps)
}

pub(crate) fn run() -> Result<()> {
    let request: Request =
        serde_json::from_reader(io::stdin().lock()).map_err(|_| "invalid-request")?;
    match request {
        Request::Catalog => {
            let all = ["MicrosoftWord", "LibreOffice", "Calibre", "Caj2Pdf"].map(str::to_owned);
            let mut catalog = BTreeMap::new();
            for source in ["doc", "docx", "epub", "pdf", "image"]
                .into_iter()
                .chain(CAJ)
            {
                let targets: Vec<_> = ["pdf", "docx", "txt", "md", "epub", "mobi", "azw3"]
                    .into_iter()
                    .filter(|target| route(source, target, source == "image", false, &all).is_ok())
                    .collect();
                catalog.insert(source, targets);
            }
            write_json(&catalog)
        }
        Request::Plan {
            source,
            target,
            image,
            fixed,
            tools,
        } => {
            let response = match route(&source, &target, image || source == "image", fixed, &tools)
            {
                Ok(steps) => Response { steps, error: None },
                Err(error) => Response {
                    steps: Vec::new(),
                    error: Some(error),
                },
            };
            write_json(&response)
        }
    }
}
