#!/bin/bash
# Draws docs/diagrams/*.mmd into docs/assets, one SVG per theme.
#
# The README embeds these as images rather than as ```mermaid``` blocks, because
# GitHub wraps a mermaid block in its own pan and zoom toolbar and offers no way
# to turn it off. An image has no chrome, and rendering here rather than in the
# browser is also what lets the corners be rounded and the palette be chosen.
#
# Not part of build.sh or release.sh: this needs node, which nothing else here
# does, and the diagrams change about as often as the README does.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
IN="$ROOT/docs/diagrams"
OUT="$ROOT/docs/assets"

for src in "$IN"/*.mmd; do
    name="$(basename "$src" .mmd)"
    for theme in light dark; do
        svg="$OUT/$name-$theme.svg"
        # A distinct id per drawing. Mermaid scopes its stylesheet to the svg's
        # id and gives every render the same one, so two of these inlined into
        # one page have the second's colours applied to both. As images they are
        # separate documents and it never shows, which is the sort of thing that
        # only surfaces once somebody inlines them.
        npx -y @mermaid-js/mermaid-cli \
            -i "$src" -o "$svg" --svgId "graft-$name-$theme" \
            -c "$IN/theme-$theme.json" -b transparent >/dev/null
        # Mermaid draws every box with square corners and offers no setting for
        # it, so the radius goes on afterwards. The clusters are rounded harder
        # than the nodes they hold, or the two read as the same object.
        python3 - "$svg" <<'PY'
import re, sys
p = sys.argv[1]
s = open(p, encoding="utf-8").read()
def round_rects(chunk, radius):
    return re.sub(r'<rect(?![^>]*\brx=)', f'<rect rx="{radius}" ry="{radius}"', chunk)
s = re.sub(r'<g class="cluster[^"]*"[^>]*>.*?</g>',
           lambda m: round_rects(m.group(0), 12), s, flags=re.S)
s = re.sub(r'<g class="node[^"]*"[^>]*>.*?</g>',
           lambda m: round_rects(m.group(0), 8), s, flags=re.S)
open(p, "w", encoding="utf-8").write(s)
PY
        echo "drew $(basename "$svg")"
    done
done
