// Bottom-right tray for downloads (download.progress events).
import { host } from "./bridge.js";
import { h, ic, clear, fmtBytes } from "./ui.js";

const items = new Map(); // key -> latest data
let collapsed = false;
const tray = () => document.getElementById("tray");

const STATE_TEXT = {
  waiting: "Waiting for you to click Download in the site window",
  downloading: "Downloading",
  done: "Downloaded — getting the install ready",
  failed: "Download failed",
  cancelled: "Cancelled",
};

export function initTray() {
  host.on("download.progress", (d) => {
    if (!d || !d.key) return;
    const prev = items.get(d.key);
    items.set(d.key, { ...prev, ...d, at: Date.now() });
    if (d.state === "done" || d.state === "cancelled") scheduleRemoval(d.key, d.state === "done" ? 6000 : 4000);
    paint();
  });
  host.on("plan.failed", (d) => {
    if (d && d.key && items.has(d.key)) {
      items.set(d.key, { ...items.get(d.key), state: "failed", error: d.error });
      paint();
    }
  });
}

function scheduleRemoval(key, ms) {
  const stamp = items.get(key).at;
  setTimeout(() => {
    const it = items.get(key);
    if (it && it.at === stamp && (it.state === "done" || it.state === "cancelled")) {
      items.delete(key);
      paint();
    }
  }, ms);
}

function paint() {
  const t = tray();
  clear(t);
  if (!items.size) { t.hidden = true; return; }
  t.hidden = false;
  t.classList.toggle("collapsed", collapsed);
  const active = [...items.values()].filter((i) => i.state === "waiting" || i.state === "downloading").length;
  t.append(h("div.tray-head",
    ic("download", "ico-sm"),
    h("span.grow", active ? `Downloads (${active} active)` : "Downloads"),
    h("button.btn.btn-sm.btn-ghost.btn-icon", { type: "button", "aria-label": collapsed ? "Expand" : "Collapse", onclick: () => { collapsed = !collapsed; paint(); } },
      ic(collapsed ? "chevrons" : "minus", "ico-sm"))));
  const list = h("div.tray-list");
  for (const it of [...items.values()].reverse()) list.append(row(it));
  t.append(list);
}

function row(it) {
  const frac = it.total ? Math.min(1, it.received / it.total) : null;
  let sub = STATE_TEXT[it.state] || it.state;
  if (it.state === "downloading") {
    sub = it.total ? `${fmtBytes(it.received)} of ${fmtBytes(it.total)}` : `${fmtBytes(it.received || 0)}`;
  }
  if (it.state === "failed" && it.error) sub = `Failed: ${it.error}`;
  const indeterminate = it.state === "waiting" || (it.state === "downloading" && frac == null);
  const width = it.state === "done" ? 100 : frac != null ? frac * 100 : 0;
  return h(`div.dl.${it.state}`,
    h("div.dl-top",
      h("div.dl-title", { title: it.title || it.key }, it.title || (it.file ? it.file.split(/[\\/]/).pop() : "Download")),
      it.state === "failed" || it.state === "done" || it.state === "cancelled"
        ? h("button.btn.btn-sm.btn-ghost.btn-icon", { type: "button", "aria-label": "Dismiss", onclick: () => { items.delete(it.key); paint(); } }, ic("x", "ico-sm"))
        : null),
    h("div.dl-sub", sub),
    h(`div.progress${indeterminate ? ".indeterminate" : ""}`, h("div", { style: { width: indeterminate ? "" : `${width}%` } })));
}
