use super::{Block, Document, Item, Row};
use std::collections::{HashMap, HashSet};
use std::fmt::Write;
use unicode_general_category::{GeneralCategory, get_general_category};

const NL: &str = "\r\n";

pub(super) fn render(document: &Document) -> String {
    let mut renderer = Renderer {
        document,
        notes: Vec::new(),
        source_ids: HashMap::new(),
        used_ids: HashSet::new(),
    };
    let mut out = String::new();
    let first = document
        .blocks
        .iter()
        .find(|block| !matches!(block, Block::Footnote { .. }));
    let title_exists = matches!(first, Some(Block::Heading { level: 1, text }) if fold_case(text) == fold_case(&document.title));
    if !document.title.trim().is_empty() && !title_exists {
        heading(&mut out, 1, &document.title, "");
    }
    renderer.blocks(&mut out, &document.blocks, 0);
    if !renderer.notes.is_empty() {
        trim_lines(&mut out);
        out.push_str("\r\n\r\n");
        // Rendering nested notes may append definitions to this same list.
        let mut index = 0;
        while index < renderer.notes.len() {
            let (ref id, blocks) = renderer.notes[index];
            write!(out, "[^{id}]:{NL}").unwrap();
            renderer.blocks(&mut out, blocks, 1);
            index += 1;
        }
    }
    out
}

struct Renderer<'a> {
    document: &'a Document,
    notes: Vec<(String, &'a [Block])>,
    source_ids: HashMap<&'a str, String>,
    used_ids: HashSet<String>,
}

impl<'a> Renderer<'a> {
    fn blocks(&mut self, out: &mut String, blocks: &'a [Block], depth: usize) {
        for block in blocks {
            self.block(out, block, depth);
        }
    }

    fn block(&mut self, out: &mut String, block: &'a Block, depth: usize) {
        let indent = "    ".repeat(depth);
        match block {
            Block::Heading { level, text } => heading(out, *level, text, &indent),
            Block::Paragraph { text } => {
                write!(out, "{indent}{}{NL}{NL}", escape(text)).unwrap();
            }
            Block::List { ordered, items } => self.list(out, *ordered, items, depth, &indent),
            Block::Table { rows } => self.table(out, rows, &indent),
            Block::Image {
                resource_id,
                alternative_text,
                caption,
            } => {
                let link = self.image(resource_id, alternative_text.as_deref(), caption.as_deref());
                write!(out, "{indent}{link}{NL}").unwrap();
                if let Some(text) = caption.as_ref().filter(|s| !s.trim().is_empty()) {
                    write!(out, "{indent}*{}*{NL}", escape(text)).unwrap();
                }
                out.push_str(NL);
            }
            Block::PageBreak => {
                write!(out, "{indent}<!-- page-break -->{NL}{indent}<div style=\"page-break-after: always;\"></div>{NL}{NL}").unwrap();
            }
            Block::Footnote { id, blocks } => {
                trim_lines(out);
                write!(out, "[^{}]{NL}{NL}", self.note(id, blocks)).unwrap();
            }
        }
    }

    fn list(
        &mut self,
        out: &mut String,
        ordered: bool,
        items: &'a [Item],
        depth: usize,
        indent: &str,
    ) {
        for (i, item) in items.iter().enumerate() {
            out.push_str(indent);
            if ordered {
                write!(out, "{}. ", i + 1).unwrap();
            } else {
                out.push_str("- ");
            }
            let Some((first, rest)) = item.blocks.split_first() else {
                out.push_str(NL);
                continue;
            };
            match first {
                Block::Paragraph { text } => {
                    write!(out, "{}{NL}", escape(text)).unwrap();
                }
                Block::Heading { text, .. } => {
                    write!(out, "**{}**{NL}", escape(text)).unwrap();
                }
                Block::Image {
                    resource_id,
                    alternative_text,
                    caption,
                } => {
                    write!(
                        out,
                        "{}{NL}",
                        self.image(resource_id, alternative_text.as_deref(), caption.as_deref())
                    )
                    .unwrap();
                }
                Block::Footnote { id, blocks } => {
                    write!(out, "[^{}]{NL}", self.note(id, blocks)).unwrap();
                }
                other => {
                    out.push_str(NL);
                    self.block(out, other, depth + 1);
                }
            }
            self.blocks(out, rest, depth + 1);
        }
        out.push_str(NL);
    }

    fn table(&mut self, out: &mut String, rows: &'a [Row], indent: &str) {
        let width = rows.iter().map(|row| row.cells.len()).max().unwrap_or(0);
        if width == 0 {
            return;
        }
        if rows
            .iter()
            .flat_map(|row| &row.cells)
            .any(|c| c.column_span > 1 || c.row_span > 1)
        {
            write!(out, "{indent}<table>{NL}{indent}  <tbody>{NL}").unwrap();
            for row in rows {
                write!(out, "{indent}    <tr>{NL}").unwrap();
                for cell in &row.cells {
                    write!(out, "{indent}      <td").unwrap();
                    if cell.column_span > 1 {
                        write!(out, " colspan=\"{}\"", cell.column_span).unwrap();
                    }
                    if cell.row_span > 1 {
                        write!(out, " rowspan=\"{}\"", cell.row_span).unwrap();
                    }
                    write!(out, ">{}</td>{NL}", html(&self.flatten(&cell.blocks, true))).unwrap();
                }
                write!(out, "{indent}    </tr>{NL}").unwrap();
            }
            write!(out, "{indent}  </tbody>{NL}{indent}</table>{NL}{NL}").unwrap();
            return;
        }
        for (i, row) in rows.iter().enumerate() {
            write!(out, "{indent}|").unwrap();
            for column in 0..width {
                let value = row
                    .cells
                    .get(column)
                    .map(|cell| self.flatten(&cell.blocks, false))
                    .unwrap_or_default();
                write!(out, " {} |", value.replace('|', "\\|")).unwrap();
            }
            out.push_str(NL);
            if i == 0 {
                write!(out, "{indent}|{}{NL}", " --- |".repeat(width)).unwrap();
            }
        }
        out.push_str(NL);
    }

    fn flatten(&mut self, blocks: &'a [Block], plain: bool) -> String {
        let mut values = Vec::new();
        for block in blocks {
            let value = match block {
                Block::Heading { text, .. } | Block::Paragraph { text } => {
                    if plain {
                        text.clone()
                    } else {
                        escape(text)
                    }
                }
                Block::Image {
                    resource_id,
                    alternative_text,
                    caption,
                } => {
                    if plain {
                        alternative_text
                            .as_deref()
                            .or(caption.as_deref())
                            .unwrap_or(&self.document.image_label)
                            .to_owned()
                    } else {
                        self.image(resource_id, alternative_text.as_deref(), caption.as_deref())
                    }
                }
                Block::Footnote { id, blocks } => {
                    if plain {
                        self.flatten(blocks, true)
                    } else {
                        format!("[^{}]", self.note(id, blocks))
                    }
                }
                Block::PageBreak => if plain { "---" } else { "<!-- page-break -->" }.to_owned(),
                Block::List { items, .. } => items
                    .iter()
                    .map(|x| self.flatten(&x.blocks, plain))
                    .collect::<Vec<_>>()
                    .join("; "),
                Block::Table { rows } => rows
                    .iter()
                    .map(|r| {
                        r.cells
                            .iter()
                            .map(|c| self.flatten(&c.blocks, plain))
                            .collect::<Vec<_>>()
                            .join(" / ")
                    })
                    .collect::<Vec<_>>()
                    .join("; "),
            };
            if !value.trim().is_empty() {
                values.push(value);
            }
        }
        values.join(if plain { "\n" } else { "<br>" })
    }

    fn image(&self, id: &str, alt: Option<&str>, caption: Option<&str>) -> String {
        let label = alt
            .or(caption)
            .unwrap_or(&self.document.image_label)
            .replace('\\', "\\\\")
            .replace(']', "\\]")
            .replace(['\r', '\n'], " ");
        let link = self
            .document
            .links
            .get(id)
            .cloned()
            .unwrap_or_else(|| escape_uri(id));
        format!("![{label}]({link})")
    }

    fn note(&mut self, source: &'a str, blocks: &'a [Block]) -> String {
        if let Some(existing) = self.source_ids.get(source) {
            return existing.clone();
        }
        let mut base: String = source
            .chars()
            .filter(|&c| {
                // The established Markdown IDs use .NET Char.IsLetterOrDigit on UTF-16 code units.
                c.len_utf16() == 1
                    && (matches!(c, '-' | '_' | '.')
                        || matches!(
                            get_general_category(c),
                            GeneralCategory::UppercaseLetter
                                | GeneralCategory::LowercaseLetter
                                | GeneralCategory::TitlecaseLetter
                                | GeneralCategory::ModifierLetter
                                | GeneralCategory::OtherLetter
                                | GeneralCategory::DecimalNumber
                        ))
            })
            .collect();
        if base.is_empty() {
            base.push_str("footnote");
        }
        let mut id = base.clone();
        let mut suffix = 2;
        while !self.used_ids.insert(fold_case(&id)) {
            id = format!("{base}-{suffix}");
            suffix += 1;
        }
        self.source_ids.insert(source, id.clone());
        self.notes.push((id.clone(), blocks));
        id
    }
}

fn trim_lines(out: &mut String) {
    out.truncate(out.trim_end_matches(['\r', '\n']).len());
}

fn heading(out: &mut String, level: i32, text: &str, indent: &str) {
    write!(
        out,
        "{indent}{} {}{NL}{NL}",
        "#".repeat(level.clamp(1, 6) as usize),
        escape(text)
    )
    .unwrap();
}

fn escape(text: &str) -> String {
    let mut out = String::new();
    let mut chars = text.chars().peekable();
    while let Some(c) = chars.next() {
        match c {
            '\r' => {
                if chars.peek() == Some(&'\n') {
                    chars.next();
                }
                out.push_str("<br>");
            }
            '\n' => out.push_str("<br>"),
            '\\' | '`' | '*' | '_' | '[' | ']' | '<' | '>' => {
                out.push('\\');
                out.push(c);
            }
            _ => out.push(c),
        }
    }
    out
}

fn escape_uri(text: &str) -> String {
    let mut out = String::new();
    for byte in text.bytes() {
        if byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_' | b'.' | b'~') {
            out.push(char::from(byte));
        } else {
            write!(out, "%{byte:02X}").unwrap();
        }
    }
    out
}

fn html(text: &str) -> String {
    let mut out = String::new();
    for c in text.chars() {
        match c {
            '&' => out.push_str("&amp;"),
            '<' => out.push_str("&lt;"),
            '>' => out.push_str("&gt;"),
            '"' => out.push_str("&quot;"),
            '\'' => out.push_str("&#39;"),
            '\n' => out.push_str("<br>"),
            '\u{a0}'..='\u{ff}' | '\u{10000}'.. => {
                write!(out, "&#{};", c as u32).unwrap();
            }
            _ => out.push(c),
        }
    }
    out
}

// Ordinal comparison never expands one letter into multiple scalars.
fn fold_case(text: &str) -> String {
    text.chars()
        .map(|c| {
            let mut upper = c.to_uppercase();
            let first = upper.next().unwrap();
            if upper.next().is_some() || c == 'ı' || c == 'ſ' {
                c
            } else {
                first
            }
        })
        .collect()
}
