use html5gum::{DefaultEmitter, HtmlString, Token, Tokenizer};
use std::io;

pub(crate) fn run() -> crate::Result<()> {
    let html: String =
        serde_json::from_reader(io::stdin().lock()).map_err(|_| "invalid-request")?;
    crate::write_json(&extract_text(&html))
}

#[derive(Default)]
struct Text {
    value: String,
    // Delay separators until another word or preformatted run actually follows.
    separator: Option<char>,
}

impl Text {
    fn flush_separator(&mut self) {
        if let Some(separator) = self.separator.take()
            && !self.value.is_empty()
            && !self.value.ends_with('\n')
        {
            self.value.push(separator);
        }
    }

    fn append(&mut self, bytes: &[u8], preformatted: bool) {
        let text = String::from_utf8_lossy(bytes);
        if preformatted {
            self.flush_separator();
            self.value.push_str(&text);
            return;
        }
        for ch in text.chars() {
            if matches!(ch, ' ' | '\t' | '\n' | '\r' | '\u{c}') {
                self.separator.get_or_insert(' ');
            } else {
                self.flush_separator();
                self.value.push(ch);
            }
        }
    }
}

fn extract_text(html: &str) -> String {
    let mut emitter = DefaultEmitter::default();
    emitter.naively_switch_states(true); // Tokenize script, style and textarea in their HTML text modes.
    let mut text = Text::default();
    let mut suppressed: Option<(HtmlString, usize)> = None;
    let mut preformatted = 0usize;
    for token in Tokenizer::new_with_emitter(html, emitter) {
        // The in-memory UTF-8 reader is infallible. HTML parse errors are recoverable tokens.
        match token.unwrap_or_else(|never| match never {}) {
            Token::StartTag(tag) => {
                let name = tag.name.as_slice();
                if let Some((outer, depth)) = &mut suppressed {
                    if outer.as_slice() == name {
                        *depth += 1;
                    }
                    continue;
                }
                if matches!(
                    name,
                    b"head" | b"title" | b"script" | b"style" | b"template" | b"iframe"
                ) || tag.attributes.contains_key(b"hidden".as_slice())
                {
                    if !matches!(
                        name,
                        b"area"
                            | b"base"
                            | b"br"
                            | b"col"
                            | b"embed"
                            | b"hr"
                            | b"img"
                            | b"input"
                            | b"link"
                            | b"meta"
                            | b"param"
                            | b"source"
                            | b"track"
                            | b"wbr"
                    ) {
                        suppressed = Some((tag.name, 1));
                    }
                } else if name == b"br" {
                    text.separator = None;
                    text.value.push('\n');
                } else if name == b"img" {
                    if let Some(alt) = tag.attributes.get(b"alt".as_slice()) {
                        text.append(alt, preformatted > 0);
                    }
                } else {
                    if is_block(name) {
                        text.separator = Some('\n');
                    }
                    if matches!(name, b"pre" | b"textarea") {
                        preformatted += 1;
                    }
                }
            }
            Token::EndTag(tag) => {
                let name = tag.name.as_slice();
                if let Some((outer, depth)) = &mut suppressed {
                    if outer.as_slice() == name {
                        *depth -= 1;
                        if *depth == 0 {
                            suppressed = None;
                        }
                    }
                    continue;
                }
                if matches!(name, b"pre" | b"textarea") {
                    preformatted = preformatted.saturating_sub(1);
                }
                if is_block(name) {
                    text.separator = Some('\n');
                } else if matches!(name, b"td" | b"th") {
                    text.separator = Some('\t');
                }
            }
            Token::String(content) if suppressed.is_none() => {
                text.append(&content, preformatted > 0)
            }
            _ => {}
        }
    }
    text.value
}

fn is_block(name: &[u8]) -> bool {
    matches!(
        name,
        b"address"
            | b"article"
            | b"aside"
            | b"blockquote"
            | b"caption"
            | b"dd"
            | b"div"
            | b"dl"
            | b"dt"
            | b"fieldset"
            | b"figcaption"
            | b"figure"
            | b"footer"
            | b"form"
            | b"h1"
            | b"h2"
            | b"h3"
            | b"h4"
            | b"h5"
            | b"h6"
            | b"header"
            | b"hr"
            | b"li"
            | b"main"
            | b"nav"
            | b"ol"
            | b"p"
            | b"pre"
            | b"section"
            | b"table"
            | b"tr"
            | b"ul"
    )
}

#[cfg(test)]
mod tests {
    use super::extract_text;

    #[test]
    fn decodes_entities_and_preserves_inline_and_paragraph_boundaries() {
        assert_eq!(
            extract_text("<p> 中<b>文</b>  &amp; &#x1f600;&nbsp;</p><p>A<br><br>B</p>"),
            "中文 & 😀\u{a0}\nA\n\nB"
        );
    }

    #[test]
    fn suppresses_scripts_comments_and_nested_hidden_elements() {
        let html = concat!(
            "<head><title>Page</title></head><script>if (a < b) write('<div>')</script>",
            "<style>p { color: red }</style><!-- note --><div hidden><div>hidden</div></div>",
            "<img hidden src='x'><span>A</span><img alt='&amp;'><b>B</b>"
        );
        assert_eq!(extract_text(html), "A&B");
    }

    #[test]
    fn preserves_preformatted_text_and_separates_table_cells() {
        let html = concat!(
            "<pre>  x\n  y </pre><table><tr><th>A</th><td>B</td></tr>",
            "<tr><td>C</td><td>D</td></tr></table><textarea> &lt;p&gt; </textarea>"
        );
        assert_eq!(extract_text(html), "  x\n  y \nA\tB\nC\tD\n <p> ");
    }
}
