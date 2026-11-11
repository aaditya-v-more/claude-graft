# Website and search

The product site at `graft.aadityamore.com`, served by GitHub Pages from `docs/`
on `main`, and what it takes for a search engine to find it. Read this before
adding a page to `docs/`, moving the site, or changing its `<head>`.

## Nothing tells Google a site exists but you

The site went up with the 1.x releases and two and a half months later Google
had indexed none of it. A `site:graft.aadityamore.com` search returned nothing,
and searching the product's own name brought up the GitHub repository and a
much larger, unrelated project called Graft, never the site. URL Inspection in
Search Console gave the reason: **Discovered – currently not indexed.** Google
knew the address from a few Reddit posts and a repository-stats page and had
never once fetched it. There was no `robots.txt`, no sitemap, and nobody had
submitted either, so nothing told Google the page was worth a crawl.

The portfolio repository keeps its own record of every sitemap submitted to
Search Console (`docs/seo.md` there). It listed the main site, Play, Learn, Lab
and Studio and never Graft, because Graft is the one site under `aadityamore.com`
that is not built in that repository. The checklist lived where Graft was not.

The README was a second leak. Its Website link still pointed at
`aaditya-v-more.github.io/claude-graft/`, which only redirects, so the most
visited page about the project sent its one link to the site through a hop
instead of to the address the site names as its own.

## What the site carries now

- `docs/robots.txt` names `https://graft.aadityamore.com/sitemap.xml` and keeps
  `release-notes/` out. Those files are fragments the update dialog shows, with
  no `<head>`, and they would turn up in results as untitled pages.
- `docs/sitemap.xml` lists every page at the top of `docs/`. Today that is the
  home page alone; the suite fails if an `.html` file sits there unlisted.
- The home page names `https://graft.aadityamore.com/` as its canonical address
  and carries `WebSite` and `SoftwareApplication` structured data. The author is
  the portfolio's `https://aadityamore.com/#person`, so the two sites read as
  one person's. Nothing in it claims a rating, review or download count.
- The title leads with the name and then says what the app does —
  "multiple Claude accounts, side by side on Mac" — because the name alone
  collides with the other Graft and the description is what a person searching
  without knowing the name types.

## Search Console

The `aadityamore.com` Domain property covers the apex and every subdomain, Graft
included, so nothing has to be verified again. What it does not do is find a new
sitemap: that is submitted once, by hand, in the Sitemaps report. A new page or a
new address also goes through URL Inspection and **Request indexing** on the day
it is published. Repeating the request does not speed anything up.

Submitted on 11 November 2026: `https://graft.aadityamore.com/sitemap.xml`, and
indexing requested for the home page. Record later submissions here and in the
portfolio's `docs/seo.md`, so neither list can fall behind the other again.

## The name

"Claude Graft" without anything after it is a hard search to win: the AI
overview and the first page belong to the other Graft, which has thousands of
stars and coverage of its own. "Claude Graft mac" already finds the repository
and the portfolio's project page. What moves the bare name is the site being
indexed under it and other pages linking to it by name, which is why the
portfolio's link reads "Claude Graft website" rather than "Website & download".
