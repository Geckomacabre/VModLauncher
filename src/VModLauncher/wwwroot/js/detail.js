// Mod detail slide-over: gallery, description, stats, Install.
import { host } from "./bridge.js";
import { h, ic, clear, fmtNum, fmtDate, toast, toastError, isSiteUrl } from "./ui.js";
import { SITES } from "./state.js";
import { starButton } from "./pages/browse.js";

const drawer = () => document.getElementById("drawer");
const backdrop = () => document.getElementById("drawer-backdrop");
let token = 0;
let lastFocus = null;

export function isDetailOpen() {
  return !drawer().hidden;
}

export function closeDetail() {
  token++;
  drawer().hidden = true;
  backdrop().hidden = true;
  clear(drawer());
  if (lastFocus && document.contains(lastFocus)) lastFocus.focus();
}

export async function openDetail(item) {
  const my = ++token;
  lastFocus = document.activeElement;
  const d = drawer();
  clear(d);
  d.hidden = false;
  backdrop().hidden = false;
  backdrop().onclick = closeDetail;

  // first paint from the card's data, then fill in from the page
  paint(d, item, null, true);
  try {
    const detail = await host.call("site.detail", { site: item.site, url: item.url });
    if (my !== token) return;
    paint(d, { ...item, ...detail, installed: detail.installed ?? item.installed ?? null }, null, false);
  } catch (e) {
    if (my !== token) return;
    paint(d, item, e, false);
  }
}

function paint(d, mod, error, loading) {
  clear(d);
  const images = (mod.images && mod.images.length ? mod.images : [mod.image]).filter(Boolean);
  let current = 0;

  const top = h("div.drawer-top",
    h("button.btn.btn-sm", { type: "button", onclick: () => openPage(mod.url), title: "Open the mod's page" }, ic("external", "ico-sm"), "Open page"),
    mod.url ? starButton(summaryOf(mod), "drawer-star") : null,
    h("button.btn.btn-sm.btn-icon", { type: "button", onclick: closeDetail, "aria-label": "Close" }, ic("x", "ico-sm")));

  const scroll = h("div.drawer-scroll");
  d.append(top, scroll);

  // gallery
  if (images.length) {
    const main = h("img.detail-main-img", { alt: mod.title, src: images[0], referrerpolicy: "no-referrer" });
    main.addEventListener("click", () => openLightbox(images, current));
    main.addEventListener("error", () => { main.style.visibility = "hidden"; });
    const gallery = h("div.detail-gallery", main);
    scroll.append(gallery);
    if (images.length > 1) {
      const thumbs = h("div.detail-thumbs", { role: "group", "aria-label": "Screenshots" });
      images.slice(0, 24).forEach((src, i) => {
        const b = h(`button${i === 0 ? ".active" : ""}`, { type: "button", "aria-label": `Screenshot ${i + 1}`, onclick: () => {
          current = i;
          main.style.visibility = "";
          main.src = src;
          thumbs.querySelectorAll("button").forEach((x, j) => x.classList.toggle("active", j === i));
        } }, h("img", { src, alt: "", loading: "lazy", referrerpolicy: "no-referrer" }));
        thumbs.append(b);
      });
      scroll.append(thumbs);
    }
  }

  const body = h("div.detail-body");
  scroll.append(body);

  body.append(h("h2.detail-title", mod.title, mod.version ? h("span.ver", `v${String(mod.version).replace(/^v/i, "")}`) : null));
  body.append(h("div.detail-by", mod.author ? `by ${mod.author}` : "", mod.author ? " · " : "", SITES[mod.site] ? SITES[mod.site].name : mod.site));

  const badges = h("div.detail-badges");
  for (const ed of mod.editions || []) badges.append(h(`span.badge.badge-${ed}`, ed === "enhanced" ? "Enhanced" : "Legacy"));
  if (mod.installed) badges.append(h("span.badge.badge-installed", mod.installed.enabled ? `Installed${mod.installed.version ? " v" + mod.installed.version : ""}` : "Installed (disabled)"));
  for (const t of (mod.tags || []).slice(0, 8)) badges.append(h("span.badge.badge-site", t));
  body.append(badges);

  // stats
  const stats = [
    ["Downloads", mod.downloads != null ? fmtNum(mod.downloads) : null],
    ["Likes", mod.likes != null ? fmtNum(mod.likes) : null],
    ["Rating", mod.rating != null ? `${mod.rating.toFixed(1)} / 5` : null],
    ["Size", mod.fileSize],
    ["Updated", mod.updated ? fmtDate(mod.updated) : null],
    ["Comments", mod.comments != null ? fmtNum(mod.comments) : null],
  ].filter(([, v]) => v);
  if (stats.length) body.append(h("div.detail-stats", stats.map(([k, v]) => h("div.detail-stat", h("small", k), h("b", v)))));

  // actions
  const canInstall = !loading && !error && !!mod.downloadPageUrl;
  const update = mod.installed && mod.version && mod.installed.version && mod.installed.version !== mod.version;
  const installBtn = h("button.btn.btn-primary.btn-lg", {
    type: "button", disabled: !canInstall || null,
    onclick: () => startDownload(mod, installBtn),
  }, ic("download"), loading ? "Loading…" : update ? "UPDATE" : mod.installed ? "REINSTALL" : "INSTALL");
  body.append(h("div.detail-actions", installBtn,
    loading ? h("div.spinner", { "aria-label": "Loading" }) : null));

  if (!loading && !error && !mod.downloadPageUrl) {
    body.append(h("div.detail-note", ic("info"), h("div", "This mod's page has no download button, so there's nothing to install from here. Open the page to see why.")));
  }
  if (mod.site === "lcpdfr" && mod.loginRequired) {
    body.append(h("div.detail-note", ic("lock"),
      h("div", "LCPDFR only lets members download. You'll sign in to LCPDFR in the next window — the launcher never sees your password.")));
  }
  if (mod.site === "gta5mods" && canInstall) {
    body.append(h("div.detail-note", ic("info"),
      h("div", "The GTA5-Mods download page opens in a window. Wait for its countdown (or press its Download button); the launcher takes the file from there.")));
  }

  if (error) {
    body.append(h("div.alert.danger", ic("alert"), h("div.alert-body", h("b", "Couldn't load the page. "), error.message || String(error))));
    if (error.code === "CHALLENGE" || /^CHALLENGE:/.test(error.message || "")) {
      body.append(h("div", { style: { marginTop: "10px" } },
        h("button.btn", { type: "button", onclick: () => openPage(SITES[mod.site].home) }, ic("globe"), `Open ${SITES[mod.site].name} to clear its check`)));
    }
  }

  if (mod.requirementsText) {
    body.append(h("div.requirements", h("h4", "Requirements"), h("pre", mod.requirementsText)));
  }

  if (loading) {
    body.append(h("div.desc-head", "Description"), h("div.skeleton", { style: { height: "160px" } }));
  } else if (mod.descriptionHtml) {
    const rich = h("div.rich", { html: mod.descriptionHtml });
    rich.querySelectorAll("a").forEach((a) => { a.setAttribute("rel", "noreferrer"); a.removeAttribute("target"); });
    rich.querySelectorAll("img").forEach((img) => { img.setAttribute("referrerpolicy", "no-referrer"); img.setAttribute("loading", "lazy"); });
    rich.addEventListener("click", (e) => {
      const a = e.target.closest("a");
      if (!a) return;
      e.preventDefault();
      const href = a.getAttribute("href");
      if (href && /^https?:/i.test(href)) openPage(href);
    });
    body.append(h("div.desc-head", "Description"), rich);
  } else if (mod.blurb) {
    body.append(h("div.desc-head", "Description"), h("div.rich", h("p", mod.blurb)));
  }

  if (mod.versions && mod.versions.length) {
    body.append(h("div.desc-head", "Versions"),
      h("div.versions", mod.versions.slice(0, 8).map((v) => h("div.version-row", h("b", v.version), h("span", v.date ? fmtDate(v.date) : "")))));
  }
}

/** The card-sized part of a mod (what favourites store), without the long description and gallery. */
function summaryOf(mod) {
  const keep = ["site", "id", "url", "title", "author", "version", "image", "downloads", "likes", "rating", "category", "tags", "editions", "updated", "fileSize", "blurb"];
  const out = {};
  for (const k of keep) if (mod[k] !== undefined) out[k] = mod[k];
  if (!out.image && mod.images && mod.images.length) out.image = mod.images[0];
  return out;
}

function openPage(url) {
  if (!url) return;
  host.call(isSiteUrl(url) ? "site.open" : "shell.openUrl", { url }).catch(toastError);
}

async function startDownload(mod, btn) {
  btn.disabled = true;
  try {
    await host.call("mod.download", {
      site: mod.site, url: mod.url, title: mod.title,
      version: mod.version || null, author: mod.author || null, image: mod.image || (mod.images && mod.images[0]) || null,
    });
    toast(mod.site === "lcpdfr"
      ? "LCPDFR's download page is open. Sign in if it asks, then click Download — the launcher takes it from there."
      : "The download page is open. Let it count down; the launcher installs the file when it arrives.", "info", 7000);
    closeDetail();
  } catch (e) {
    btn.disabled = false;
    toastError(e);
  }
}

// ---------------------------------------------------------------- lightbox

export function openLightbox(images, start = 0) {
  const lb = document.getElementById("lightbox");
  let i = start;
  clear(lb);
  const img = h("img", { alt: "Screenshot", src: images[i], referrerpolicy: "no-referrer" });
  const show = (n) => { i = (n + images.length) % images.length; img.src = images[i]; };
  lb.append(img,
    h("button.btn.btn-icon.close", { type: "button", "aria-label": "Close", onclick: (e) => { e.stopPropagation(); closeLightbox(); } }, ic("x")));
  if (images.length > 1) {
    lb.append(
      h("button.btn.btn-icon.prev", { type: "button", "aria-label": "Previous", onclick: (e) => { e.stopPropagation(); show(i - 1); } }, ic("left")),
      h("button.btn.btn-icon.next", { type: "button", "aria-label": "Next", onclick: (e) => { e.stopPropagation(); show(i + 1); } }, ic("right")));
  }
  img.addEventListener("click", (e) => { e.stopPropagation(); if (images.length > 1) show(i + 1); });
  lb.onclick = closeLightbox;
  lb.onkeydown = null;
  lb.__nav = show;
  lb.__index = () => i;
  lb.hidden = false;
}

export function closeLightbox() {
  const lb = document.getElementById("lightbox");
  lb.hidden = true;
  clear(lb);
}

export function lightboxKey(e) {
  const lb = document.getElementById("lightbox");
  if (lb.hidden) return false;
  if (e.key === "Escape") { closeLightbox(); return true; }
  if (e.key === "ArrowRight" && lb.__nav) { lb.__nav(lb.__index() + 1); return true; }
  if (e.key === "ArrowLeft" && lb.__nav) { lb.__nav(lb.__index() - 1); return true; }
  return false;
}
