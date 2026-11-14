        const SKIP = new Set(["SCRIPT", "STYLE", "SVG", "BUTTON", "IFRAME", "CANVAS", "NOSCRIPT", "TEMPLATE",
                              "HEAD", "INPUT", "SELECT", "TEXTAREA", "VIDEO", "AUDIO", "OBJECT", "EMBED", "MATH"]);
        const BLOCK = new Set(["DIV", "SECTION", "ARTICLE", "MAIN", "HEADER", "FOOTER", "NAV", "ASIDE", "FORM",
                               "FIGURE", "FIGCAPTION", "DETAILS", "SUMMARY", "DL", "DT", "DD", "ADDRESS",
                               "FIELDSET", "BODY", "CENTER"]);
        const tagOf = (node) => node.nodeType === 1 ? node.tagName.toUpperCase() : "";
        const hidden = (node) => {
            const style = getComputedStyle(node);
            return style.display === "none" || style.visibility === "hidden";
        };
        const escape = (text) => text.replace(/([\\`*_~<\[\]])/g, "\\$1");
        const words = (text) => escape(text.replace(/\s+/g, " "));
        const line = (text) => text.replace(/^([#>+-])/, "\\$1").replace(/^(\d+)\./, "$1\\.");

        function inline(node) {
            let out = "";
            for (const child of node.childNodes) out += inlineNode(child);
            return out;
        }

        function inlineNode(child) {
            if (child.nodeType === 3) return words(child.nodeValue);
            if (child.nodeType !== 1) return "";
            const tag = tagOf(child);
            if (SKIP.has(tag) || hidden(child)) return "";
            const wrap = (mark) => {
                const text = inline(child).trim();
                return text ? mark + text + mark : "";
            };
            switch (tag) {
            case "BR": return "  \n";
            case "STRONG": case "B": return wrap("**");
            case "EM": case "I": return wrap("*");
            case "DEL": case "S": case "STRIKE": return wrap("~~");
            case "CODE": case "KBD": case "SAMP": {
                const text = child.textContent;
                if (!text) return "";
                const fence = text.includes("`") ? "`` " : "`";
                return fence + text + fence.split("").reverse().join("");
            }
            case "A": {
                const text = inline(child).trim();
                const href = child.getAttribute("href") || "";
                if (!href || href.startsWith("#") || /^javascript:/i.test(href)) return text;
                return "[" + (text || escape(href)) + "](" + href.replace(/ /g, "%20").replace(/\)/g, "%29") + ")";
            }
            case "IMG": {
                const alt = child.getAttribute("alt") || "";
                const src = child.getAttribute("src") || "";
                if (src && !src.startsWith("data:")) return "![" + escape(alt) + "](" + src.replace(/ /g, "%20") + ")";
                return escape(alt);
            }
            default: return inline(child);
            }
        }

        function list(element) {
            const ordered = tagOf(element) === "OL";
            let number = parseInt(element.getAttribute("start") || "1", 10) || 1;
            const lines = [];
            for (const item of element.children) {
                if (tagOf(item) !== "LI" || hidden(item)) continue;
                const box = [...item.children].find((c) => tagOf(c) === "INPUT" && c.type === "checkbox");
                let text = "";
                const nested = [];
                for (const child of item.childNodes) {
                    const tag = tagOf(child);
                    if (tag === "UL" || tag === "OL") nested.push(list(child));
                    else text += inlineNode(child);
                }
                const marker = ordered ? (number++) + "." : "-";
                const check = box ? (box.checked ? "[x] " : "[ ] ") : "";
                lines.push(marker + " " + check + text.replace(/\s*\n\s*/g, " ").trim());
                const indent = " ".repeat(marker.length + 1);
                for (const block of nested) lines.push(block.split("\n").map((l) => indent + l).join("\n"));
            }
            return lines.join("\n");
        }

        function table(element) {
            const rows = [...element.querySelectorAll("tr")].map((row) =>
                [...row.children].filter((cell) => ["TD", "TH"].includes(tagOf(cell)))
                    .map((cell) => inline(cell).replace(/\s+/g, " ").trim().replace(/\|/g, "\\|")));
            const width = Math.max(0, ...rows.map((row) => row.length));
            if (!width) return "";
            const pad = (row) => "| " + [...row, ...Array(width - row.length).fill("")].join(" | ") + " |";
            const lines = [pad(rows[0]), "|" + " --- |".repeat(width)];
            for (const row of rows.slice(1)) lines.push(pad(row));
            return lines.join("\n");
        }

        function blocks(node, out) {
            let paragraph = "";
            const flush = () => {
                const text = paragraph.replace(/[ \t]+/g, " ").replace(/ *\n */g, "\n").trim();
                if (text) out.push(line(text));
                paragraph = "";
            };
            for (const child of node.childNodes) {
                if (child.nodeType === 3) { paragraph += words(child.nodeValue); continue; }
                if (child.nodeType !== 1) continue;
                const tag = tagOf(child);
                if (SKIP.has(tag) || hidden(child)) continue;
                if (/^H[1-6]$/.test(tag)) {
                    flush();
                    const text = inline(child).replace(/\s+/g, " ").trim();
                    if (text) out.push("#".repeat(Number(tag[1])) + " " + text);
                } else if (tag === "P") {
                    flush();
                    paragraph = inline(child);
                    flush();
                } else if (tag === "PRE") {
                    flush();
                    const text = child.textContent.replace(/\n$/, "");
                    const fence = text.includes("```") ? "~~~~" : "```";
                    out.push(fence + "\n" + text + "\n" + fence);
                } else if (tag === "UL" || tag === "OL") {
                    flush();
                    const text = list(child);
                    if (text) out.push(text);
                } else if (tag === "BLOCKQUOTE") {
                    flush();
                    const inner = [];
                    blocks(child, inner);
                    if (inner.length) out.push(inner.join("\n\n").split("\n").map((l) => l ? "> " + l : ">").join("\n"));
                } else if (tag === "HR") {
                    flush();
                    out.push("---");
                } else if (tag === "TABLE") {
                    flush();
                    const text = table(child);
                    if (text) out.push(text);
                } else if (BLOCK.has(tag)) {
                    flush();
                    blocks(child, out);
                } else {
                    paragraph += inlineNode(child);
                }
            }
            flush();
        }

        const out = [];
        if (document.body) blocks(document.body, out);
        return out.join("\n\n").replace(/\n{3,}/g, "\n\n").trim() + "\n";