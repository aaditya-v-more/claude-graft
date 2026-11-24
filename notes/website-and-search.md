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
- `docs/sitemap.xml` lists the canonical home page. Set its `lastmod` to the date
  of a meaningful update to that page. The suite checks that every HTML page
  at the top of `docs/` is listed.
- The home page names `https://graft.aadityamore.com/` as its canonical address
  and carries `WebSite` and `SoftwareApplication` structured data. The author is
  the portfolio's `https://aadityamore.com/#person`, so the two sites read as
  one person's. Nothing in it claims a rating, review or download count.
- The title, heading, social metadata and software markup name both Mac and
  Windows. The home page includes platform-specific install instructions so
  links from the portfolio can take visitors to the right download.

## What people search for

By 11 November 2026 Search Console had only ever shown the site for its own
name — "claude graft", "graft claude", "graft for claude": 237 impressions and
four clicks. Someone who needs the app does not know its name yet. What they
type instead, from Google's suggestions and results pages on 15 November 2026,
falls into two groups. One is about running more than one account: "multiple
claude accounts mac", "claude desktop multiple accounts", "two claude accounts
at the same time", "claude desktop multiple instances", "claude account
switcher mac", "claude code multiple accounts", a personal and a work account
on one Mac. The other is about running out: "claude code usage limit reached",
"claude limit reached how to continue", the five-hour and weekly limits and
when they reset, switching accounts in the middle of a session.

Those results belong to Reddit threads, a feature request on the
`anthropics/claude-code` repository asking for multiple accounts, a paid app
whose guide is titled "How to Run Multiple Claude Accounts on One Mac", and
personal blogs that copy `Claude.app` or set `CLAUDE_CONFIG_DIR`. Neither the
site nor the repository was on the first two pages for any of them. For the
bare name, the first page held a different Graft for Claude Code, a hook that
trims tokens and has articles of its own, with the portfolio's project page
fourth.

What changed on 15 November 2026:

- The title and the one `<h1>` lead with the name and then say "run multiple
  Claude accounts on one Mac", the phrase the paid app and the guides rank
  with. The slogan stays as the display line under the heading; the suite
  fails if the heading stops naming the app or the phrase.
- The description and the Shared usage heading say "usage limit" and "hits its
  limit", the words of the second group.
- A questions section answers those searches in plain sentences: two accounts
  at once, the usage limit, whether chats are shared too, whether it is a copy
  of `Claude.app`, price, the login, what it needs, Anthropic. Its `FAQPage`
  structured data repeats them word for word, and the suite holds the two
  together. Google shows FAQ results only for a few authoritative sites now, so
  the markup is for other engines; the visible answers are what match a long
  question typed into any of them.
- The repository's description is the GitHub page's title in Google
  ("GitHub - aaditya-v-more/claude-graft: …"), so it now starts with the same
  phrase, and its topics went to GitHub's limit of twenty with `claude-ai`,
  `menubar-app`, `multiple-accounts` and `usage-limits`.

None of this moves a generic search on its own. What does is other pages
linking to the site by what it does: the feature request and the Reddit threads
above, and the directories that list menu bar apps. Those are posts a person
makes, not changes to this repository.

Where things stood on Google on 15 November 2026, before the new wording was
crawled, to measure it against:

| Search | First page |
| --- | --- |
| claude graft | the portfolio's project page fourth; neither the site nor the repository |
| claude graft mac | the project page, the repository, a listing site, then the site fourth under its old title |
| multiple claude accounts mac | neither, on the first two pages |
| claude desktop multiple accounts | neither |
| claude code usage limit switch account | neither |

## Search Console

The `aadityamore.com` Domain property covers the apex and every subdomain, Graft
included, so nothing has to be verified again. What it does not do is find a new
sitemap: that is submitted once, by hand, in the Sitemaps report. A new page or a
new address also goes through URL Inspection and **Request indexing** on the day
it is published. Repeating the request does not speed anything up.

Submitted on 11 November 2026: `https://graft.aadityamore.com/sitemap.xml`, and
indexing requested for the home page. Record later submissions here and in the
portfolio's `docs/seo.md`, so neither list can fall behind the other again.

On 15 November 2026, once the new wording was live, URL Inspection reported the
home page indexed ("URL is on Google"), and indexing was requested again so the
new title, heading and questions are read; Google put it in its priority crawl
queue. The portfolio's project page, retitled "Claude Graft - Multiple Claude
Accounts on Mac" the same day, was indexed too and went into the same queue.

## The name

"Claude Graft" without anything after it is a hard search to win: the AI
overview and the first page belong to the other Graft, which has thousands of
stars and coverage of its own. "Claude Graft mac" already finds the repository
and the portfolio's project page. What moves the bare name is the site being
indexed under it and other pages linking to it by name, which is why the
portfolio's link reads "Claude Graft website" rather than "Website & download".
