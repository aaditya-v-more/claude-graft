//! A Word document made from the blocks of an artifact's drawn page.
//!
//! A Mac hands the drawn page to the system's own Word writer. A Linux desktop
//! has none to hand it to, and asking a host office suite would mean starting
//! a program nobody asked for, so the document is written here: the few parts
//! a `.docx` must have and nothing a word processor would have to fetch.

use serde::Deserialize;
use std::io::Write;

#[derive(Debug, Clone, Default, Deserialize, PartialEq)]
pub struct Run {
    pub text: String,
    #[serde(default)]
    pub bold: bool,
    #[serde(default)]
    pub italic: bool,
    #[serde(default)]
    pub strike: bool,
    #[serde(default)]
    pub code: bool,
    #[serde(default)]
    pub link: Option<String>,
}

#[derive(Debug, Clone, Deserialize, PartialEq)]
pub struct Cell {
    #[serde(default)]
    pub header: bool,
    #[serde(default)]
    pub runs: Vec<Run>,
}

#[derive(Debug, Clone, Deserialize, PartialEq)]
#[serde(tag = "type", rename_all = "lowercase")]
pub enum Block {
    Heading {
        level: u8,
        runs: Vec<Run>,
        #[serde(default)]
        quote: bool,
    },
    Paragraph {
        runs: Vec<Run>,
        #[serde(default)]
        quote: bool,
    },
    Item {
        ordered: bool,
        level: u8,
        runs: Vec<Run>,
        #[serde(default)]
        quote: bool,
    },
    Code {
        text: String,
        #[serde(default)]
        quote: bool,
    },
    Rule {
        #[serde(default)]
        quote: bool,
    },
    Table {
        rows: Vec<Vec<Cell>>,
        #[serde(default)]
        quote: bool,
    },
}

#[derive(Debug, Clone, Deserialize, PartialEq)]
pub struct Page {
    #[serde(default)]
    pub title: String,
    pub blocks: Vec<Block>,
}

pub fn escape(text: &str) -> String {
    let mut out = String::with_capacity(text.len());
    for ch in text.chars() {
        match ch {
            '&' => out.push_str("&amp;"),
            '<' => out.push_str("&lt;"),
            '>' => out.push_str("&gt;"),
            '"' => out.push_str("&quot;"),
            // Characters XML 1.0 cannot carry at all.
            c if (c as u32) < 0x20 && c != '\t' && c != '\n' && c != '\r' => {}
            c => out.push(c),
        }
    }
    out
}

struct Writer {
    body: String,
    links: Vec<String>,
    /// One numbering instance per ordered list, so each one counts from one.
    ordered_lists: usize,
}

impl Writer {
    fn runs(&mut self, runs: &[Run]) {
        for run in runs {
            let mut props = String::new();
            if run.bold {
                props.push_str("<w:b/>");
            }
            if run.italic {
                props.push_str("<w:i/>");
            }
            if run.strike {
                props.push_str("<w:strike/>");
            }
            if run.code {
                props.push_str("<w:rStyle w:val=\"CodeChar\"/>");
            }
            if run.link.is_some() {
                props.push_str("<w:color w:val=\"0563C1\"/><w:u w:val=\"single\"/>");
            }
            let mut inner = String::new();
            for (i, line) in run.text.split('\n').enumerate() {
                if i > 0 {
                    inner.push_str("<w:br/>");
                }
                if !line.is_empty() {
                    inner.push_str(&format!("<w:t xml:space=\"preserve\">{}</w:t>", escape(line)));
                }
            }
            let element = format!("<w:r><w:rPr>{props}</w:rPr>{inner}</w:r>");
            match &run.link {
                Some(link) => {
                    self.links.push(link.clone());
                    let id = format!("rIdLink{}", self.links.len());
                    self.body.push_str(&format!("<w:hyperlink r:id=\"{id}\">{element}</w:hyperlink>"));
                }
                None => self.body.push_str(&element),
            }
        }
    }

    fn paragraph(&mut self, style: Option<&str>, extra: &str, quote: bool, runs: &[Run]) {
        self.body.push_str("<w:p><w:pPr>");
        if let Some(style) = style {
            self.body.push_str(&format!("<w:pStyle w:val=\"{style}\"/>"));
        }
        self.body.push_str(extra);
        if quote {
            self.body.push_str("<w:ind w:left=\"720\"/>");
        }
        self.body.push_str("</w:pPr>");
        self.runs(runs);
        self.body.push_str("</w:p>");
    }
}

pub fn document(page: &Page) -> Vec<u8> {
    let mut writer = Writer { body: String::new(), links: Vec::new(), ordered_lists: 0 };
    let mut in_ordered = false;
    for block in &page.blocks {
        let continues_list = matches!(block, Block::Item { ordered: true, .. });
        if continues_list && !in_ordered {
            writer.ordered_lists += 1;
        }
        in_ordered = continues_list || (in_ordered && matches!(block, Block::Item { .. }));
        match block {
            Block::Heading { level, runs, quote } => {
                let style = format!("Heading{}", (*level).clamp(1, 6));
                writer.paragraph(Some(&style), "", *quote, runs);
            }
            Block::Paragraph { runs, quote } => writer.paragraph(None, "", *quote, runs),
            Block::Item { ordered, level, runs, quote } => {
                let num = if *ordered { 1 + writer.ordered_lists } else { 1 };
                let extra = format!("<w:numPr><w:ilvl w:val=\"{}\"/><w:numId w:val=\"{num}\"/></w:numPr>", (*level).min(8));
                writer.paragraph(Some("ListParagraph"), &extra, *quote, runs);
            }
            Block::Code { text, quote } => {
                for line in text.split('\n') {
                    writer.paragraph(Some("Code"), "", *quote, &[Run { text: line.to_string(), ..Default::default() }]);
                }
            }
            Block::Rule { quote } => writer.paragraph(
                None,
                "<w:pBdr><w:bottom w:val=\"single\" w:sz=\"6\" w:space=\"1\" w:color=\"999999\"/></w:pBdr>",
                *quote,
                &[],
            ),
            Block::Table { rows, .. } => {
                let width = rows.iter().map(Vec::len).max().unwrap_or(0);
                if width == 0 {
                    continue;
                }
                writer.body.push_str(
                    "<w:tbl><w:tblPr><w:tblStyle w:val=\"TableGrid\"/><w:tblW w:w=\"0\" w:type=\"auto\"/>\
                     <w:tblBorders><w:top w:val=\"single\" w:sz=\"4\"/><w:left w:val=\"single\" w:sz=\"4\"/>\
                     <w:bottom w:val=\"single\" w:sz=\"4\"/><w:right w:val=\"single\" w:sz=\"4\"/>\
                     <w:insideH w:val=\"single\" w:sz=\"4\"/><w:insideV w:val=\"single\" w:sz=\"4\"/></w:tblBorders>\
                     </w:tblPr><w:tblGrid>",
                );
                for _ in 0..width {
                    writer.body.push_str("<w:gridCol/>");
                }
                writer.body.push_str("</w:tblGrid>");
                for row in rows {
                    writer.body.push_str("<w:tr>");
                    for index in 0..width {
                        writer.body.push_str("<w:tc><w:tcPr><w:tcW w:w=\"0\" w:type=\"auto\"/></w:tcPr>");
                        let (header, runs) = match row.get(index) {
                            Some(cell) => (cell.header, cell.runs.clone()),
                            None => (false, Vec::new()),
                        };
                        let runs: Vec<Run> =
                            runs.into_iter().map(|r| Run { bold: r.bold || header, ..r }).collect();
                        writer.paragraph(None, "", false, &runs);
                        writer.body.push_str("</w:tc>");
                    }
                    writer.body.push_str("</w:tr>");
                }
                writer.body.push_str("</w:tbl>");
            }
        }
    }
    package(&writer, &page.title)
}

const W: &str = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
const R: &str = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

fn package(writer: &Writer, title: &str) -> Vec<u8> {
    let document = format!(
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\
         <w:document xmlns:w=\"{W}\" xmlns:r=\"{R}\"><w:body>{}\
         <w:sectPr><w:pgSz w:w=\"12240\" w:h=\"15840\"/>\
         <w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"720\" w:footer=\"720\" w:gutter=\"0\"/>\
         </w:sectPr></w:body></w:document>",
        writer.body
    );
    let mut rels = String::from(
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\
         <Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">\
         <Relationship Id=\"rIdStyles\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>\
         <Relationship Id=\"rIdNumbering\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering\" Target=\"numbering.xml\"/>",
    );
    for (index, link) in writer.links.iter().enumerate() {
        rels.push_str(&format!(
            "<Relationship Id=\"rIdLink{}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink\" Target=\"{}\" TargetMode=\"External\"/>",
            index + 1,
            escape(link)
        ));
    }
    rels.push_str("</Relationships>");

    let mut numbering = format!(
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:numbering xmlns:w=\"{W}\">{}{}",
        abstract_list(0, "bullet"),
        abstract_list(1, "decimal")
    );
    numbering.push_str("<w:num w:numId=\"1\"><w:abstractNumId w:val=\"0\"/></w:num>");
    for list in 1..=writer.ordered_lists {
        numbering.push_str(&format!(
            "<w:num w:numId=\"{}\"><w:abstractNumId w:val=\"1\"/>\
             <w:lvlOverride w:ilvl=\"0\"><w:startOverride w:val=\"1\"/></w:lvlOverride></w:num>",
            list + 1
        ));
    }
    numbering.push_str("</w:numbering>");

    let core = format!(
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\
         <cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" \
         xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>{}</dc:title></cp:coreProperties>",
        escape(title)
    );

    let parts: [(&str, String); 7] = [
        (
            "[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\
             <Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">\
             <Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>\
             <Default Extension=\"xml\" ContentType=\"application/xml\"/>\
             <Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>\
             <Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>\
             <Override PartName=\"/word/numbering.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml\"/>\
             <Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>\
             </Types>"
                .to_string(),
        ),
        (
            "_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\
             <Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">\
             <Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>\
             <Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>\
             </Relationships>"
                .to_string(),
        ),
        ("word/document.xml", document),
        ("word/_rels/document.xml.rels", rels),
        ("word/styles.xml", styles()),
        ("word/numbering.xml", numbering),
        ("docProps/core.xml", core),
    ];
    let mut zip = zip::ZipWriter::new(std::io::Cursor::new(Vec::new()));
    let options = zip::write::SimpleFileOptions::default().compression_method(zip::CompressionMethod::Deflated);
    for (name, content) in parts {
        if zip.start_file(name, options).is_err() || zip.write_all(content.as_bytes()).is_err() {
            return Vec::new();
        }
    }
    zip.finish().map(|c| c.into_inner()).unwrap_or_default()
}

fn abstract_list(id: u32, format: &str) -> String {
    let mut out = format!("<w:abstractNum w:abstractNumId=\"{id}\"><w:multiLevelType w:val=\"hybridMultilevel\"/>");
    for level in 0..9u32 {
        let text = if format == "bullet" {
            ["\u{2022}", "\u{25e6}", "\u{25aa}"][level as usize % 3].to_string()
        } else {
            format!("%{}.", level + 1)
        };
        out.push_str(&format!(
            "<w:lvl w:ilvl=\"{level}\"><w:start w:val=\"1\"/><w:numFmt w:val=\"{format}\"/>\
             <w:lvlText w:val=\"{text}\"/><w:lvlJc w:val=\"left\"/>\
             <w:pPr><w:ind w:left=\"{}\" w:hanging=\"360\"/></w:pPr></w:lvl>",
            720 * (level + 1)
        ));
    }
    out.push_str("</w:abstractNum>");
    out
}

fn styles() -> String {
    let mut out = format!(
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:styles xmlns:w=\"{W}\">\
         <w:docDefaults><w:rPrDefault><w:rPr><w:sz w:val=\"22\"/></w:rPr></w:rPrDefault>\
         <w:pPrDefault><w:pPr><w:spacing w:after=\"160\" w:line=\"264\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>\
         <w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/></w:style>\
         <w:style w:type=\"paragraph\" w:styleId=\"ListParagraph\"><w:name w:val=\"List Paragraph\"/><w:basedOn w:val=\"Normal\"/>\
         <w:pPr><w:spacing w:after=\"60\"/></w:pPr></w:style>\
         <w:style w:type=\"paragraph\" w:styleId=\"Code\"><w:name w:val=\"Code\"/><w:basedOn w:val=\"Normal\"/>\
         <w:pPr><w:spacing w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/><w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"F2F2F2\"/></w:pPr>\
         <w:rPr><w:rFonts w:ascii=\"Courier New\" w:hAnsi=\"Courier New\" w:cs=\"Courier New\"/><w:sz w:val=\"20\"/></w:rPr></w:style>\
         <w:style w:type=\"character\" w:styleId=\"CodeChar\"><w:name w:val=\"Code Char\"/>\
         <w:rPr><w:rFonts w:ascii=\"Courier New\" w:hAnsi=\"Courier New\" w:cs=\"Courier New\"/></w:rPr></w:style>\
         <w:style w:type=\"table\" w:styleId=\"TableGrid\"><w:name w:val=\"Table Grid\"/></w:style>"
    );
    for (level, size) in [(1, 36), (2, 30), (3, 26), (4, 24), (5, 22), (6, 22)] {
        out.push_str(&format!(
            "<w:style w:type=\"paragraph\" w:styleId=\"Heading{level}\"><w:name w:val=\"heading {level}\"/>\
             <w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:qFormat/>\
             <w:pPr><w:keepNext/><w:spacing w:before=\"240\" w:after=\"120\"/><w:outlineLvl w:val=\"{}\"/></w:pPr>\
             <w:rPr><w:b/><w:sz w:val=\"{size}\"/></w:rPr></w:style>",
            level - 1
        ));
    }
    out.push_str("</w:styles>");
    out
}
