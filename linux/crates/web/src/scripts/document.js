// The page as drawn, as the blocks a Word document is made of. What the
// Markdown export leaves out is left out here too: scripts, styles, drawings,
// controls and anything hidden. Pictures are kept as their description, since
// a picture from the web would have to be fetched to be kept at all.
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

function runs(node, mark, out) {
    for (const child of node.childNodes) {
        if (child.nodeType === 3) {
            const text = child.nodeValue.replace(/\s+/g, " ");
            if (text) out.push(Object.assign({text}, mark));
            continue;
        }
        if (child.nodeType !== 1) continue;
        const tag = tagOf(child);
        if (SKIP.has(tag) || hidden(child)) continue;
        switch (tag) {
        case "BR": out.push({text: "\n"}); break;
        case "STRONG": case "B": runs(child, Object.assign({}, mark, {bold: true}), out); break;
        case "EM": case "I": runs(child, Object.assign({}, mark, {italic: true}), out); break;
        case "DEL": case "S": case "STRIKE": runs(child, Object.assign({}, mark, {strike: true}), out); break;
        case "CODE": case "KBD": case "SAMP":
            out.push(Object.assign({text: child.textContent}, mark, {code: true})); break;
        case "A": {
            const href = child.getAttribute("href") || "";
            const link = /^https?:|^mailto:/i.test(href) ? href : undefined;
            runs(child, Object.assign({}, mark, link ? {link} : {}), out);
            break;
        }
        case "IMG": {
            const alt = child.getAttribute("alt") || "";
            if (alt) out.push(Object.assign({text: alt}, mark, {italic: true}));
            break;
        }
        default: runs(child, mark, out);
        }
    }
    return out;
}

const trimmed = (list) => {
    while (list.length && !list[0].text.trim()) list.shift();
    while (list.length && !list[list.length - 1].text.trim()) list.pop();
    if (list.length) {
        list[0].text = list[0].text.replace(/^\s+/, "");
        list[list.length - 1].text = list[list.length - 1].text.replace(/\s+$/, "");
    }
    return list;
};

function list(element, level, out) {
    const ordered = tagOf(element) === "OL";
    for (const item of element.children) {
        if (tagOf(item) !== "LI" || hidden(item)) continue;
        const own = [];
        const nested = [];
        for (const child of item.childNodes) {
            const tag = tagOf(child);
            if (tag === "UL" || tag === "OL") nested.push(child);
            else runs({childNodes: [child]}, {}, own);
        }
        const box = [...item.children].find((c) => tagOf(c) === "INPUT" && c.type === "checkbox");
        if (box) own.unshift({text: box.checked ? "\u2612 " : "\u2610 "});
        out.push({type: "item", ordered, level, runs: trimmed(own)});
        for (const child of nested) list(child, level + 1, out);
    }
}

function blocks(node, out) {
    let paragraph = [];
    const flush = () => {
        const text = trimmed(paragraph);
        if (text.length) out.push({type: "paragraph", runs: text});
        paragraph = [];
    };
    for (const child of node.childNodes) {
        if (child.nodeType === 3) {
            const text = child.nodeValue.replace(/\s+/g, " ");
            if (text.trim()) paragraph.push({text});
            continue;
        }
        if (child.nodeType !== 1) continue;
        const tag = tagOf(child);
        if (SKIP.has(tag) || hidden(child)) continue;
        if (/^H[1-6]$/.test(tag)) {
            flush();
            const text = trimmed(runs(child, {}, []));
            if (text.length) out.push({type: "heading", level: Number(tag[1]), runs: text});
        } else if (tag === "P") {
            flush();
            paragraph = runs(child, {}, []);
            flush();
        } else if (tag === "PRE") {
            flush();
            out.push({type: "code", text: child.textContent.replace(/\n$/, "")});
        } else if (tag === "UL" || tag === "OL") {
            flush();
            list(child, 0, out);
        } else if (tag === "BLOCKQUOTE") {
            flush();
            const inner = [];
            blocks(child, inner);
            for (const block of inner) out.push(Object.assign(block, {quote: true}));
        } else if (tag === "HR") {
            flush();
            out.push({type: "rule"});
        } else if (tag === "TABLE") {
            flush();
            const rows = [...child.querySelectorAll("tr")].map((row) =>
                [...row.children].filter((cell) => ["TD", "TH"].includes(tagOf(cell)))
                    .map((cell) => ({header: tagOf(cell) === "TH", runs: trimmed(runs(cell, {}, []))})));
            if (rows.some((row) => row.length)) out.push({type: "table", rows});
        } else if (BLOCK.has(tag)) {
            flush();
            blocks(child, out);
        } else {
            runs({childNodes: [child]}, {}, paragraph);
        }
    }
    flush();
}

const out = [];
if (document.body) blocks(document.body, out);
return JSON.stringify({title: document.title || "", blocks: out});
