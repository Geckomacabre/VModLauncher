import { host } from "../bridge.js";
import { h, ic, clear, fmtNum, lazyImg, toast, toastError } from "../ui.js";
import { state, changed, SITES, currentEdition } from "../state.js";
import { openDetail } from "../detail.js";
import { COLLECTIONS } from "../collections.js";
import { installFramework } from "./tools.js";
import { panelArt } from "../art.js";

// Browse keeps its results while you visit other pages.
// B.site: a real site ("gta5mods" | "lcpdfr") or a pseudo tab ("favorites" | "collections").
const B = {
  site: null,
  meta: {},          // site -> { categories, sorts }
  category: {},      // site -> category id
  sort: {},          // site -> sort id
  mode: "list",      // "list" | "search"
  allSites: false,   // search both sites at once
  query: "",
  items: [],
  page: 0,
  pages: null,
  hasMore: false,
  multi: null,       // all-sites search paging: site -> { page, hasMore, error }
  searchPageUrl: null, // GTA5-Mods search: the site's own results page
  loading: false,
  error: null,
  token: 0,
  cards: new Map(),  // url -> card element (reused across repaints so images don't flicker)
};

const TABS = [
  { id: "gta5mods", name: "GTA5-Mods", dot: true },
  { id: "lcpdfr", name: "LCPDFR", dot: true },
  { id: "favorites", name: "Favourites", icon: "star" },
  { id: "collections", name: "Collections", icon: "layers" },
];
const isRealSite = (s) => s === "gta5mods" || s === "lcpdfr";

let refs = null;

/** Reads the starred mods once at start (favorites.list). */
export async function loadFavorites() {
  try {
    const list = await host.call("favorites.list", {});
    state.favorites = new Map((list || []).map((it) => [it.url, it]));
    changed("favorites");
  } catch {
    // an older host without favourites: the stars just stay empty
  }
}

export function isFavorite(url) {
  return state.favorites.has(url);
}

/** Stars / unstars a mod; updates every star on screen. */
export async function toggleFavorite(item) {
  const strip = { ...item };
  delete strip.installed;
  try {
    const r = await host.call("favorites.toggle", { item: strip });
    if (r.favorite) state.favorites.set(item.url, strip); else state.favorites.delete(item.url);
    for (const b of document.querySelectorAll(`.fav-star[data-url="${CSS.escape(item.url)}"]`)) paintStar(b, r.favorite);
    toast(r.favorite ? `Starred ${item.title}.` : `Removed ${item.title} from favourites.`, "ok", 1800);
    if (B.site === "favorites") changed("favorites");
  } catch (e) {
    toastError(e);
  }
}

export function starButton(item, cls = "") {
  const b = h(`button.fav-star${cls ? "." + cls : ""}`, { type: "button", dataset: { url: item.url } });
  paintStar(b, isFavorite(item.url));
  b.addEventListener("click", (e) => { e.stopPropagation(); toggleFavorite(item); });
  return b;
}

function paintStar(b, on) {
  clear(b).append(ic(on ? "star" : "starOutline", "ico-sm"));
  b.classList.toggle("on", on);
  b.setAttribute("aria-pressed", on ? "true" : "false");
  b.setAttribute("aria-label", on ? "Remove from favourites" : "Add to favourites");
  b.title = on ? "In your favourites" : "Add to favourites";
}

// ---------------------------------------------------------------- page

export function renderBrowse(root) {
  if (!B.site) B.site = state.settings.defaultSite in SITES ? state.settings.defaultSite : "gta5mods";
  clear(root);

  const searchLabel = isRealSite(B.site) && !B.allSites ? `Search ${SITES[B.site].name}…` : "Search both sites…";
  const searchInput = h("input.input", {
    type: "search", placeholder: searchLabel, "aria-label": "Search mods", value: B.mode === "search" ? B.query : "",
  });
  searchInput.addEventListener("keydown", (e) => {
    if (e.key === "Enter") {
      const q = searchInput.value.trim();
      if (q) startSearch(q); else clearSearch();
    }
  });
  searchInput.addEventListener("search", () => { if (!searchInput.value.trim() && B.mode === "search") clearSearch(); });

  const allBox = h("input", { type: "checkbox", "aria-label": "Search both sites" });
  allBox.checked = B.allSites || !isRealSite(B.site);
  allBox.disabled = !isRealSite(B.site);
  allBox.addEventListener("change", () => {
    B.allSites = allBox.checked;
    searchInput.placeholder = B.allSites ? "Search both sites…" : `Search ${SITES[B.site].name}…`;
    if (B.mode === "search" && B.query) load(true);
  });

  const tabs = h("div.tabs", { role: "tablist", "aria-label": "Mod site" },
    TABS.map((t) => h(`button.tab${t.id === B.site ? ".active" : ""}`, {
      type: "button", role: "tab", "aria-selected": t.id === B.site ? "true" : "false",
      onclick: () => switchSite(t.id),
    }, t.dot ? h(`span.site-dot.${t.id}`) : ic(t.icon, "ico-sm"), t.name,
      t.id === "favorites" && state.favorites.size ? h("span.tab-count", String(state.favorites.size)) : null)));

  refs = {
    chips: h("div.chips", { role: "group", "aria-label": "Category" }),
    sort: h("select.select", { "aria-label": "Sort by" }),
    note: h("div.result-note"),
    body: h("div"),
    more: h("div.load-more"),
    searchInput,
  };
  refs.sort.addEventListener("change", () => {
    B.sort[B.site] = refs.sort.value;
    load(true);
  });

  const filters = h("div.browse-filters", refs.chips, refs.sort);
  root.append(h("div.page",
    h("div.page-head",
      h("div", h("h1.page-title", "Browse mods"), h("p.page-sub", "Straight from GTA5-Mods.com and LCPDFR.com. Pick one and the launcher installs it for you.")),
      h("div.browse-bar", tabs,
        h("label.search", ic("search"), searchInput),
        h("label.check-chip", { title: "Search GTA5-Mods and LCPDFR together" }, allBox, h("span", "Both sites")))),
    filters,
    refs.note,
    refs.body,
    refs.more));

  if (B.site === "collections" && B.mode !== "search") {
    filters.hidden = true;
    paintCollections();
    return;
  }
  if (B.site === "favorites" && B.mode !== "search") {
    filters.hidden = true;
    paintFavorites();
    return;
  }
  if (!isRealSite(B.site)) filters.hidden = true;

  paintFilters();
  paintResults();

  if (B.mode === "search") {
    if (!B.items.length && !B.loading && !B.error) load(true);
  } else if (!B.meta[B.site]) loadMeta(B.site);
  else if (!B.items.length && !B.loading && !B.error) load(true);
}

async function loadMeta(site) {
  try {
    const meta = await host.call("site.categories", { site });
    B.meta[site] = meta;
    if (!B.category[site]) B.category[site] = meta.categories[0] && meta.categories[0].id;
    if (!B.sort[site]) B.sort[site] = meta.sorts[0] && meta.sorts[0].id;
    if (site !== B.site) return;
    paintFilters();
    load(true);
  } catch (e) {
    if (site !== B.site) return;
    B.error = e;
    paintResults();
  }
}

function switchSite(site) {
  if (site === B.site && B.mode !== "search") return;
  B.site = site;
  B.mode = "list";
  B.query = "";
  B.items = [];
  B.error = null;
  B.multi = null;
  B.token++;
  B.loading = false;
  const main = document.getElementById("main");
  renderBrowse(main);
}

function searchingAll() {
  return B.allSites || !isRealSite(B.site);
}

function startSearch(q) {
  B.mode = "search";
  B.query = q;
  if (!isRealSite(B.site)) {
    // searching from Favourites / Collections: re-render shows results in place of the tab and starts the search
    B.items = [];
    B.error = null;
    B.loading = false;
    B.token++;
    renderBrowse(document.getElementById("main"));
    return;
  }
  paintFilters();
  load(true);
}

function clearSearch() {
  B.mode = "list";
  B.query = "";
  if (refs) refs.searchInput.value = "";
  if (!isRealSite(B.site)) { renderBrowse(document.getElementById("main")); return; }
  paintFilters();
  load(true);
}

function paintFilters() {
  if (!refs) return;
  const meta = B.meta[B.site];
  clear(refs.chips);
  clear(refs.sort);
  if (!isRealSite(B.site)) return;
  if (!meta) {
    for (let i = 0; i < 6; i++) refs.chips.append(h("span.chip", { style: { width: `${60 + i * 9}px`, opacity: .35 } }));
    refs.sort.disabled = true;
    return;
  }
  for (const c of meta.categories) {
    const active = B.mode === "list" && c.id === B.category[B.site];
    refs.chips.append(h(`button.chip${active ? ".active" : ""}`, {
      type: "button", "aria-pressed": active ? "true" : "false",
      onclick: () => {
        B.category[B.site] = c.id;
        if (B.mode === "search") { B.mode = "list"; B.query = ""; refs.searchInput.value = ""; }
        paintFilters();
        load(true);
      },
    }, c.name));
  }
  for (const s of meta.sorts) {
    const o = h("option", { value: s.id }, s.name);
    if (s.id === B.sort[B.site]) o.selected = true;
    refs.sort.append(o);
  }
  refs.sort.disabled = B.mode === "search";
  refs.sort.title = B.mode === "search" ? "Search results come in the site's own order" : "";
}

async function load(reset) {
  const token = ++B.token;
  if (reset) {
    B.items = [];
    B.page = 0;
    B.pages = null;
    B.hasMore = false;
    B.multi = null;
    B.searchPageUrl = null;
    B.error = null;
    const main = document.getElementById("main");
    if (main && refs && main.scrollTop > 200) main.scrollTop = 0;
  }
  B.loading = true;
  paintResults();
  if (B.mode === "search" && searchingAll()) return loadAll(token);
  const next = B.page + 1;
  try {
    const res = B.mode === "search"
      ? await host.call("site.search", { site: B.site, query: B.query, page: next })
      : await host.call("site.list", { site: B.site, category: B.category[B.site], sort: B.sort[B.site], page: next });
    if (token !== B.token) return;
    addItems(res.items);
    B.page = res.page || next;
    B.pages = res.pages ?? null;
    B.hasMore = !!res.hasMore;
    if (res.searchPageUrl) B.searchPageUrl = res.searchPageUrl;
  } catch (e) {
    if (token !== B.token) return;
    B.error = e;
  }
  B.loading = false;
  paintResults();
}

/** Searches both sites and interleaves the results; "Load more" asks whichever site has more. */
async function loadAll(token) {
  if (!B.multi) B.multi = { gta5mods: { page: 0, hasMore: true, error: null }, lcpdfr: { page: 0, hasMore: true, error: null } };
  const sites = Object.keys(B.multi).filter((s) => B.multi[s].hasMore && !B.multi[s].error);
  const results = await Promise.all(sites.map(async (site) => {
    try {
      return { site, res: await host.call("site.search", { site, query: B.query, page: B.multi[site].page + 1 }) };
    } catch (e) {
      return { site, error: e };
    }
  }));
  if (token !== B.token) return;
  const lists = [];
  for (const r of results) {
    const m = B.multi[r.site];
    if (r.error) { m.error = r.error; m.hasMore = false; continue; }
    m.page = r.res.page || m.page + 1;
    m.hasMore = !!r.res.hasMore;
    lists.push((r.res.items || []).map((it) => ({ ...it, site: it.site || r.site })));
  }
  const merged = [];
  for (let i = 0; lists.some((l) => i < l.length); i++) for (const l of lists) if (i < l.length) merged.push(l[i]);
  addItems(merged);
  B.hasMore = Object.values(B.multi).some((m) => m.hasMore);
  const failed = Object.entries(B.multi).filter(([, m]) => m.error);
  if (failed.length === 2) B.error = failed[0][1].error;
  B.loading = false;
  paintResults();
}

function addItems(items) {
  const seen = new Set(B.items.map((i) => i.url));
  for (const it of items || []) if (!seen.has(it.url)) { B.items.push(it); seen.add(it.url); }
}

function isChallenge(e) {
  return e && (e.code === "CHALLENGE" || /^CHALLENGE:/.test(e.message || ""));
}

function paintResults() {
  if (!refs) return;
  clear(refs.note);
  clear(refs.body);
  clear(refs.more);
  const all = B.mode === "search" && searchingAll();
  const siteName = all ? "both sites" : SITES[B.site] ? SITES[B.site].name : "";

  if (B.mode === "search") {
    // native append() would print a null argument as the text "null": drop empty slots
    refs.note.append(...[
      h("span", B.loading && !B.items.length
        ? `Searching ${siteName} for “${B.query}”…`
        : `${B.items.length}${B.hasMore ? "+" : ""} result${B.items.length === 1 && !B.hasMore ? "" : "s"} for “${B.query}” on ${siteName}`),
      B.searchPageUrl && !all
        ? h("button.btn.btn-sm.btn-ghost", { type: "button", onclick: () => host.call("site.open", { url: B.searchPageUrl }).catch(toastError) }, ic("external", "ico-sm"), "Open results on the site")
        : null,
      h("button.btn.btn-sm.btn-ghost", { type: "button", onclick: clearSearch }, ic("x", "ico-sm"), "Clear search"),
    ].filter(Boolean));
    if (all && B.multi) {
      for (const [site, m] of Object.entries(B.multi)) {
        if (m.error && B.items.length) refs.note.append(h("span.badge.badge-warn", { title: m.error.message }, `${SITES[site].name} couldn't be searched`));
      }
    }
  } else if (B.pages && B.items.length) {
    refs.note.append(h("span", `Page ${B.page} of ${B.pages}`));
  }

  if (B.error && !B.items.length) {
    refs.body.append(isChallenge(B.error) ? challengePanel() : errorPanel(B.error));
    return;
  }

  if (!B.items.length && !B.loading) {
    refs.body.append(h("div.empty", ic("search"),
      h("h3", B.mode === "search" ? "Nothing found" : "Nothing here yet"),
      h("p", B.mode === "search" ? `No mods on ${siteName} match “${B.query}”.` : "This category is empty right now.")));
    return;
  }

  const grid = h("div.grid");
  for (const item of B.items) grid.append(card(item, all));
  if (B.loading) for (let i = 0; i < (B.items.length ? 4 : 12); i++) grid.append(skeletonCard());
  refs.body.append(grid);

  if (B.error && B.items.length && !all) {
    refs.more.append(isChallenge(B.error) ? challengePanel() : h("button.btn", { type: "button", onclick: () => load(false) }, ic("refresh"), "Couldn't load more — try again"));
  } else if (B.hasMore && !B.loading) {
    refs.more.append(h("button.btn", { type: "button", onclick: () => load(false) }, ic("plus"), "Load more"));
  }
}

function card(item, showSite) {
  const key = item.url + (showSite ? "|s" : "");
  let el = B.cards.get(key);
  if (el && el.__item === item) {
    const star = el.querySelector(".fav-star");
    if (star) paintStar(star, isFavorite(item.url));
    return el;
  }
  const badges = h("div.mod-card-badges");
  for (const ed of item.editions || []) badges.append(h(`span.badge.badge-${ed}`, ed === "enhanced" ? "Enhanced" : "Legacy"));
  if (item.installed) badges.append(h(`span.badge.badge-installed${item.installed.enabled ? "" : ".badge-off"}`, item.installed.enabled ? "Installed" : "Disabled"));

  const stats = h("div.mod-card-stats");
  if (item.downloads != null) stats.append(h("span", { title: `${item.downloads.toLocaleString()} downloads` }, ic("download"), fmtNum(item.downloads)));
  if (item.likes != null) stats.append(h("span", { title: `${item.likes.toLocaleString()} likes` }, ic("thumb"), fmtNum(item.likes)));
  if (item.rating != null) stats.append(h("span.rating", { title: `${item.rating} out of 5` }, ic("star"), Number(item.rating).toFixed(1)));
  if (item.fileSize) stats.append(h("span", { style: { marginLeft: "auto" } }, item.fileSize));

  const main = h("button.mod-card", { type: "button", onclick: () => openDetail(item), "aria-label": `${item.title}${item.author ? " by " + item.author : ""}` },
    h("div.mod-card-img", h("div.noimg", ic("car", "ico-xl")), lazyImg(item.image, ""), badges),
    h("div.mod-card-body",
      h("div.mod-card-title", { title: item.title }, item.title),
      h("div.mod-card-meta",
        showSite && SITES[item.site] ? h(`span.site-tag.${item.site}`, SITES[item.site].name) : null,
        item.author ? h("span.author", item.author) : null,
        item.version ? h("span", `· v${String(item.version).replace(/^v/i, "")}`) : null),
      stats));
  el = h("div.mod-card-wrap", main, starButton(item));
  el.__item = item;
  B.cards.set(key, el);
  if (B.cards.size > 600) B.cards.delete(B.cards.keys().next().value);
  return el;
}

function skeletonCard() {
  return h("div.skeleton", { "aria-hidden": "true" }, h("div.skel-img"), h("div.skel-line"), h("div.skel-line.short"));
}

function challengePanel() {
  const site = SITES[B.site] || SITES.lcpdfr;
  return h("div.challenge",
    ic("shield"),
    h("div",
      h("h3", `${site.name} wants to check your browser`),
      h("p", "Open the site once in the launcher's browser window and let its check finish (tick the box if it shows one). Then come back and press Retry.")),
    h("div.challenge-actions",
      h("button.btn", { type: "button", onclick: () => host.call("site.open", { url: site.home }).catch(toastError) }, ic("globe"), `Open ${site.name}`),
      h("button.btn.btn-primary", { type: "button", onclick: () => load(!B.items.length) }, ic("refresh"), "Retry")));
}

function errorPanel(e) {
  const name = SITES[B.site] ? SITES[B.site].name : "the sites";
  return h("div.empty", ic("alert"),
    h("h3", `Couldn't reach ${name}`),
    h("p", e.message || String(e)),
    h("button.btn", { type: "button", onclick: () => (isRealSite(B.site) && !B.meta[B.site] ? loadMeta(B.site) : load(true)) }, ic("refresh"), "Try again"));
}

// ---------------------------------------------------------------- favourites

function paintFavorites() {
  const items = [...state.favorites.values()];
  if (!items.length) {
    refs.body.append(h("div.empty",
      panelArt("favorites-empty", "empty-art"),
      ic("star"),
      h("h3", "No favourites yet"),
      h("p", "Star mods on GTA5-Mods or LCPDFR (the star on each card or in a mod's page) to keep them here for later.")));
    return;
  }
  refs.note.append(h("span", `${items.length} starred mod${items.length === 1 ? "" : "s"}`));
  const grid = h("div.grid");
  for (const item of items) grid.append(card(item, true));
  refs.body.append(grid);
}

// ---------------------------------------------------------------- collections

function paintCollections() {
  const edition = currentEdition();
  refs.note.append(h("span", `Starter packs picked for GTA V ${edition}. Each item opens its page (to install through the site) or installs from the official release.`));
  const wrap = h("div.collections");
  for (const c of COLLECTIONS) {
    const items = c.items.filter((it) => !it.editions || it.editions.includes(edition));
    wrap.append(h("section.collection",
      panelArt(`collection-${c.id}`, "collection-art"),
      h("div.collection-head",
        h("h2.collection-title", c.title),
        h("p", c.blurb)),
      h("div.collection-items", items.map(collectionItem))));
  }
  refs.body.append(wrap);
}

function collectionItem(it) {
  let action;
  if (it.kind === "site") {
    action = h("button.btn.btn-sm", { type: "button", onclick: () => openDetail({ site: it.site, url: it.url, title: it.name, image: null, editions: it.editions ? it.editions.map((e) => e.toLowerCase()) : [] }) }, ic("eye", "ico-sm"), "View");
  } else if (it.kind === "framework") {
    action = h("button.btn.btn-sm.btn-primary", { type: "button", onclick: () => installFramework({ id: it.framework, name: it.name }) }, ic("download", "ico-sm"), "Install");
  } else {
    action = h("button.btn.btn-sm", { type: "button", onclick: () => host.call("shell.openUrl", { url: it.url }).catch(toastError), title: it.url }, ic("external", "ico-sm"), "Website");
  }
  const where = it.kind === "site" ? SITES[it.site].name : it.kind === "framework" ? "Official release" : hostOf(it.url);
  return h("div.collection-item",
    h("div.grow",
      h("b", it.name),
      h("div.dim.small", it.why, h("span.where", ` · ${where}`))),
    action);
}

function hostOf(url) {
  try { return new URL(url).hostname.replace(/^www\./, ""); } catch { return "website"; }
}

/** Called when the library changes so cards can show Installed badges on the next fetch. */
export function forgetBrowseCards() {
  B.cards.clear();
}
