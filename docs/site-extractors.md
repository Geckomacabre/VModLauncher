# Site extractors

`src/VModLauncher/SiteScripts/gta5mods.js` and `lcpdfr.js` read GTA5-Mods.com and LCPDFR.com for the launcher's Browse page.

## How the app uses them

- The app keeps one hidden WebView2 per site, sitting on that site's origin (`https://www.gta5-mods.com` or `https://www.lcpdfr.com`). It lands there only after the site's own browser check has cleared by itself, the same way it clears in Edge.
- It injects the script text once with `ExecuteScriptAsync`. Injecting again is harmless: the script returns early if `window.__vml` already has the same `site` and a `version` at least as new.
- It calls methods on `window.__vml` inside an async wrapper and posts the result back:

```js
(async () => {
  try {
    const r = await window.__vml.list({ category: 'scripts', sort: 'most-downloaded', page: 1 });
    chrome.webview.postMessage(JSON.stringify({ id: 'x', ok: true, result: r }));
  } catch (e) {
    chrome.webview.postMessage(JSON.stringify({ id: 'x', ok: false, error: String(e && e.message || e) }));
  }
})()
```

Each script:
- reads pages with same-origin `fetch(url, { credentials: 'include' })` plus `DOMParser`, and never navigates the page;
- runs its fetches one at a time, at least **1100 ms apart**. LCPDFR's robots.txt asks bots for at most one request per second, and GTA5-Mods gets the same treatment.
- throws an `Error` whose message starts with **`CHALLENGE:`** (followed by the URL) when it gets a browser-check or captcha page. Signs of one are a title like "Checking your browser" or "Just a moment", or `.h-captcha`, `.g-recaptcha`, `.powstatus` or a Cloudflare challenge iframe in the page. The app should then show the real site to the user in the visible browser. The scripts never try to solve or skip a challenge.
- throws `HTTP <status> for <url>` on any other non-2xx answer.

## API (same for both sites)

| Call | Returns |
|---|---|
| `categories()` | `[{ id, name }]` |
| `sorts()` | `[{ id, name }]`; the first entry is the site's default order |
| `list({ category, sort, page })` | `{ items: ModSummary[], page, pages, hasMore }` |
| `search({ query, page })` | `{ items, page, pages, hasMore, searchPageUrl }`; call it **only for an explicit user search** (see robots notes) |
| `searchPageUrl(query)` | the site's own search-results URL, for opening in the visible browser instead |
| `detail(url)` | `ModDetail`; takes an absolute URL or a site-relative path |

Fields that are missing come back as `null`, never `undefined`. `page` starts at 1.

### ModSummary

```
site        "gta5mods" | "lcpdfr"
id          stable id: G5M "scripts/<slug>" (the URL path), LCPDFR the numeric file id as a string ("47768")
url         absolute https URL of the mod page
title       string
author      string | null
version     string | null           (LCPDFR listings don't show it: null)
image       absolute https thumbnail | null
downloads   number | null
likes       number | null           (LCPDFR has none: null; G5M hides 0)
rating      number 0-5 | null       (0 / unrated -> null)
category    category id (e.g. "scripts", "vehiclemodels") | null
tags        string[]                (visible tag text)
editions    subset of ["legacy","enhanced"]
updated     ISO string | null       (G5M listings don't show it: null)
fileSize    string like "691.8 MB" | null   (G5M listings: null)
blurb       short description | null        (LCPDFR only)
views       number | null           LCPDFR extra
comments    number | null           LCPDFR extra
```

### ModDetail

This has every ModSummary field, plus:

```
descriptionHtml   sanitized HTML (see below)
images            full-size gallery URLs, absolute https
downloadPageUrl   the page to open in the visible site browser, where the human goes through the
                  site's real download flow. null means the site offers no download
                  (e.g. Menyoo on G5M is deprecated and has no button).
versions          [{ version, date }]   G5M also adds downloads and fileSize per version
requirementsText  string | null
uploaded          ISO | null
loginRequired     boolean   (LCPDFR: true when the fetched page is a guest page; G5M: always false)
```

`descriptionHtml` keeps only `p br b strong i em u ul ol li a img h1-h4 blockquote code pre span`. Links keep only an absolute https `href`. Images keep an absolute https `src`, preferring `data-src` and `data-original` over `src`, plus `alt`. Every other attribute is dropped, including `style`, `class` and all `on*` handlers. `script`, `style`, `iframe`, `noscript`, `object`, `embed`, form controls, `svg`, `video`, `audio`, `canvas`, `link` and `meta` are removed together with their content. Any other tag is unwrapped and its text kept. Before using the HTML, the app should still set `target`/`rel` on links and send clicks to the system browser.

## GTA5-Mods.com

| | |
|---|---|
| Categories | `all, scripts, vehicles, player, weapons, maps, paintjobs, tools, misc` |
| Sorts | `latest-versions` (default, no path), `latest-uploads`, `most-downloaded`, `most-liked`, `highest-rated` |
| List URL | `/<cat>[/<sort>][/<page>]`, e.g. `/scripts`, `/scripts/2`, `/vehicles/highest-rated/2` |
| Paging | the largest number in `.pagination`; 20 cards per page |
| Cards | `.file-list-obj`: `a.preview` (href + img), `ul.categories li` (tags), `.stats span[title]` ("N Downloads", "N Likes", "x star rating"), `.details .name a`, `.details .version`, `.details .bottom a` (author) |
| Detail | `#file h1` with `span.version`, `a.btn-download` gives `/<cat>/<slug>/download/<id>`, `.file-stat.file-downloads` and `.file-likes`, `.comment-average-rating[data-rating]`, JSON-LD `CreativeWork` (author, datePublished, dateModified, comment_count), `a.mfp-image` (full `q95` gallery), the `.panel.hidden-xs .file-version-container` list, and `.file-description .description-body` (the copy outside `.visible-xs-block`) |
| Search | the site's search box goes to `/search/<query>` (spaces become `+`, `/` becomes `-`). `/search?q=` is not a search: it redirects to a tag page. |
| Editions | tags `Enhanced` / `Legacy`, plus bracketed title notes such as `[LEGACY & ENHANCED]` or `(Enhanced)`. Bare title words like "Enhanced Native Trainer" are ignored. |
| Dates | version dates such as "2 hours ago" or "May 14, 2025" are turned into ISO. Anything else is passed through as written. |

## LCPDFR.com (GTA5 Mods section)

| | |
|---|---|
| Categories | `scripts` (Scripts & Plugins), `vehiclemodels`, `vehiclestextures` (Vehicle Skins), `character`, `audio`, `datafile` (Visual & Data File), `misc`, `essential-mods`, `g17media` (LSPDFR itself) |
| Sorts | `updated` (default, no query), `date`, `file_downloads`, `rating`, `views`, `title` (asc) |
| List URL | default sort: `/downloads/gta5mods/<cat>/` and `/downloads/gta5mods/<cat>/page/<n>/`; other sorts: `/downloads/gta5mods/<cat>/?sortby=<id>&sortdirection=desc&page=<n>` (the format the site's own sort links use) |
| Paging | `.ipsPagination[data-pages]`; 16 rows per page (`essential-mods` and `g17media` have one page) |
| Rows | `li.ipsData__item[data-rowid]`: `h4 a`, `.ipsData__image img` (protocol-relative `//s3-screenshots...`, turned into https), `.ipsTags__tag[data-tag-label]` (`game_version:legacy` / `game_version:enhanced` give the editions), `.ipsData__desc`, `.ipsRating_collective[data-v]`, `li[data-stattype=downloads|filesize|views|comments] [data-stat-value]`, `.ipsData__last` (author, `time[datetime]`) |
| Detail | `h1` with the version in `span.i-color_soft`; description in `#ipsTabs_file_desc_panel [data-role=commentContent]`; info list `li > strong.ipsList__label` (Views, Downloads, Submitted, Published, Updated, File Size, Compatibility, Requires, Minimum/Maximum LSPDFR Version, Script/Plugin Type, ELS Compatibility, ...); gallery `a.ipsThumb[href]` (full size, not `.thumb.`); `[data-role=versionTitle]` in the "What's New in Version" box |
| requirementsText | info-list rows whose label mentions require, minimum, maximum, compatib, plugin type or dependenc. N/A values and staff-only rows are skipped. Example: `Compatibility: Newer Versions\nRequires: RAGE Plugin Hook\nScript/Plugin Type: LSPDFR plugin`. |
| downloadPageUrl | the primary "Download this file" button (`?do=download`), never `?do=downloadMM` (the "Download with LML" one). `csrfKey` is removed because it's tied to the guest session and breaks after the user logs in. |
| versions | from the "What's New" box. First releases have no box, so the title's version and the Updated/Submitted date are used instead. |
| loginRequired | Invision puts `data-ips-guest` on `<html>` for guests. Downloads need an account, and the user logs in themselves in the visible browser. |
| Search | `/search/?q=<q>&type=downloads_file&quick=1[&page=n]`. Results are found by collecting links that match `/downloads/gta5mods/<cat>/<id>-<slug>/`, grouped by id, with the title, image, author, time and snippet taken from the enclosing `li` or `article`. |

## Robots notes (why some calls are user-only)

- GTA5-Mods robots.txt disallows `/search/*` and every `/<cat>/*/download/*` for all agents. `search()` hits `/search/<q>`, so call it only when the user presses Search. The other option is to open `searchPageUrl(q)` in the visible browser. Neither script ever fetches a download page; `downloadPageUrl` is only read from the DOM.
- LCPDFR robots.txt disallows `/search/`, `/*?sortby=`, `/*?do=*` and `/tags/` for all agents, and asks for 1 request per second. The scripts only fetch those on a direct user action: a non-default sort, or search. The `?do=download` page is only opened by the human in the visible browser.

## What was verified live (2026-10-22, built-in browser pane)

GTA5-Mods (about 15 requests, never more than one per 1.1 s; two back-to-back list calls took 2.06 s):
- `list scripts/most-downloaded` pages 1 and 2: 20 + 20 cards, no overlap, `pages` 195. Downloads, likes and ratings came through as numbers (Menyoo 16,501,423 downloads, 11,753 likes, 4.55). Every URL and image was absolute https.
- `list all` pages 1 and 2 (default sort): 20 + 20, no overlap, `pages` 3864. Categories were mixed (misc, vehicles, scripts, player, paintjobs, maps). Editions were found, e.g. "Essential Menu - [LEGACY & ENHANCED]" gave legacy+enhanced and "Blip Limit Fix [Enhanced]" gave enhanced.
- `list vehicles/latest-uploads`: 20 cards, `pages` 880.
- `detail`, three pages:
  - Menyoo: 18 images, `downloadPageUrl` null (the site has no button), dates from JSON-LD.
  - Western Star 49X (a new upload): `downloadPageUrl` `/vehicles/.../download/203276`, size 32.2 MB, 1 version.
  - Essential Menu: 11 versions with sizes, 9 images, editions legacy+enhanced, 121 comments, rating 4.73.
  - No sanitized description contained `<script`, `on*=`, `<iframe` or `style=`.

LCPDFR (about 15 requests, guest session, after the PoW check cleared by itself):
- `list scripts` pages 1 and 2 (default sort): 16 + 16 rows, no overlap, `pages` 59. Every row had an https image, an author, an updated time and a download count. Editions came from the `game_version` tags.
- `list` vehiclemodels (16 rows, 390 pages), character (16 rows, 244 pages), g17media (2 rows, LSPD First Response first) and essential-mods (10 rows, 1 page).
- `detail`, three pages:
  - LSPDFR 7792: version "0.4.9 (Build 9695) - GTA 3889", 16 full-size images, `loginRequired` true, editions legacy.
  - MTFO 47768: requirementsText filled in, 115 comments.
  - Clayton County Mustang 55696: `requirementsText` "ELS Compatibility: ELS". Its download button carried a `csrfKey`, which is now stripped.

Not verified live, on purpose:
- `search()` on both sites (robots.txt disallows `/search/` for automated agents) and LCPDFR's non-default sorts (`?sortby=` is disallowed too).
- Their URL formats come from each site's own JavaScript and sort links.
- The LCPDFR search-result parser is generic (it collects file links), so check it once by hand in the app.
- A logged-in LCPDFR page was not checked: no credentials were used. `loginRequired` relies on Invision's `data-ips-guest` marker.
