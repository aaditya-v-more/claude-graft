# Website publishing

Follow [CLAUDE.md](CLAUDE.md) for repository guidance.

Keep the public website usable at phone widths, in landscape and on desktop.
Preserve existing source, download, license and attribution links. The website
footer must include accessible, wrapping creator links to:

- https://aadityamore.com/
- https://github.com/aaditya-v-more
- https://www.linkedin.com/in/aadityavmore/

Use `data-creator-links` on that navigation. Verify links and mobile layout
before publishing website changes. Commit only the intended files; preserve
unrelated local work.

## Shared appearance

Keep the sun and moon icon buttons, with no dropdown, and both color palettes. Load
`docs/appearance.js` in the head before rendering so the choice stays in sync
with aadityamore.com and its project sites. Keep the runtime and control styles
aligned with `scripts/appearance.js` and `scripts/appearance.css` in the website
repository. New visitors follow their device setting until they make a choice.
Verify saved preference, incoming appearance links, and both palettes on mobile
when changing this control.
The runtime shares the choice through a parent-domain cookie on
`*.aadityamore.com` and carries it through an `appearance` URL parameter when
navigating to or from the linked website on a separate domain.
