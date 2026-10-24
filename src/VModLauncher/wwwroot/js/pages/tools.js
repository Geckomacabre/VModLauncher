// Tools: diagnostics (logs, crashes, files the launcher didn't install), frameworks from their
// official releases, launch options and storage — the pause menu's "settings" tabs, for modding.
import { host } from "../bridge.js";
import { h, ic, clear, fmtBytes, fmtDate, toast, toastError, confirmDialog } from "../ui.js";
import { state, selectedGame, currentEdition } from "../state.js";
import { navigate, render } from "../router.js";
import { launchOptionsCard } from "../launchopts.js";
import { panelArt } from "../art.js";
import { renderTextures } from "../tools/textures.js";
import { renderPacks } from "../tools/packs.js";
import { renderGameFiles } from "../tools/gamefiles.js";

const TABS = [
  ["diagnostics", "Diagnostics", "pulse"],
  ["textures", "Textures", "box"],
  ["packs", "Add-on packs", "layers"],
  ["gamefiles", "Game files", "shield"],
  ["frameworks", "Frameworks", "download"],
  ["launch", "Launch options", "sliders"],
  ["storage", "Storage", "disk"],
];
let tab = "diagnostics";
const cache = { report: null, quarantine: null, frameworks: null, storage: null };
const openLogs = new Set();
let loading = {};

/** Another game was selected: everything measured so far belongs to the old one. */
function syncGame() {
  if (cache.gameId === state.selectedGameId) return;
  cache.gameId = state.selectedGameId;
  cache.report = cache.quarantine = cache.frameworks = cache.storage = null;
  openLogs.clear();
}

/** Opens the Tools page on a tab ("diagnostics" | "textures" | "packs" | "gamefiles" | "frameworks" | "launch" | "storage"). */
export function openTools(which) {
  if (TABS.some((t) => t[0] === which)) tab = which;
  if (state.page === "tools") render(); else navigate("tools");
}

export function renderTools(root) {
  clear(root);
  const page = h("div.page");
  root.append(page);
  const game = selectedGame();
  syncGame();

  page.append(h("div.page-head",
    h("div", h("h1.page-title", "Tools"), h("p.page-sub", game ? `For ${game.name}. Find out why something doesn't load, keep frameworks current, and tidy up.` : "No game selected.")),
    h("div.tabs", { role: "tablist", "aria-label": "Tools" },
      TABS.map(([id, label, icon]) => h(`button.tab${id === tab ? ".active" : ""}`, {
        type: "button", role: "tab", "aria-selected": id === tab ? "true" : "false",
        onclick: () => { tab = id; render(); },
      }, ic(icon, "ico-sm"), label)))));

  if (!game) {
    page.append(h("div.empty", panelArt("tools-empty", "empty-art"), ic("car"), h("h3", "No game selected"), h("p", "Pick or add a GTA V install on the Home page first.")));
    return;
  }

  const body = h("div.tools-body");
  page.append(body);
  if (tab === "diagnostics") diagnostics(body);
  else if (tab === "textures") renderTextures(body);
  else if (tab === "packs") renderPacks(body);
  else if (tab === "gamefiles") renderGameFiles(body);
  else if (tab === "frameworks") frameworks(body);
  else if (tab === "launch") body.append(h("div.settings", launchOptionsCard()));
  else storage(body);
}

// ---------------------------------------------------------------- diagnostics

async function fetchReport(force) {
  if (loading.report) return;
  if (cache.report && !force) return;
  loading.report = true;
  try {
    const [report, quarantine] = await Promise.all([
      host.call("diag.report", {}),
      host.call("diag.quarantineList", {}).catch(() => []),
    ]);
    cache.report = report;
    cache.quarantine = quarantine || [];
  } catch (e) {
    cache.report = { error: e };
  }
  loading.report = false;
  if (state.page === "tools" && tab === "diagnostics") render();
}

function diagnostics(body) {
  const report = cache.report;
  body.append(h("div.tools-bar",
    h("p.muted", "Reads the logs Script Hook V, Script Hook V .NET, the ASI loader, RAGE Plugin Hook, OpenIV, ELS and Menyoo write, plus GTA V's crash logs."),
    h("button.btn", { type: "button", disabled: loading.report || null, onclick: () => fetchReport(true) },
      loading.report ? h("div.spinner", { style: { width: "14px", height: "14px" } }) : ic("refresh"), loading.report ? "Reading…" : "Read again")));
  if (!report) {
    body.append(h("div.row.muted", { style: { padding: "26px 0" } }, h("div.spinner"), "Reading logs…"));
    fetchReport(false);
    return;
  }
  if (report.error) {
    body.append(h("div.alert.danger", ic("alert"), h("div.alert-body", h("b", "Couldn't read the logs. "), report.error.message)));
    return;
  }

  // logs
  const logs = report.logs || [];
  const problems = logs.reduce((n, l) => n + (l.problems || []).length, 0);
  body.append(h("h2.section-title", `Logs · ${problems ? `${problems} problem line${problems === 1 ? "" : "s"}` : "nothing alarming"}`));
  body.append(h("div.log-list", logs.map(logItem)));

  // crashes
  const crashes = report.crashes || [];
  body.append(h("h2.section-title", `Crash logs · ${crashes.length}`));
  if (!crashes.length) {
    body.append(h("p.muted", "No crash logs from GTA V. Good."));
  } else {
    body.append(h("div.table", crashes.slice(0, 20).map((c) => h("div.table-row",
      ic("alert", "ico-sm"),
      h("div.grow.mono.ellipsis", { title: c.path }, c.path),
      h("span.dim", fmtDate(c.time)),
      h("span.dim", fmtBytes(c.size)),
      h("button.btn.btn-sm.btn-ghost", { type: "button", onclick: () => openLog(c.path) },
        ic(/\.dmp$/i.test(c.path) ? "folder" : "external", "ico-sm"), /\.dmp$/i.test(c.path) ? "Show file" : "Open")))));
  }

  // files nobody installed
  const unmanaged = report.unmanaged || [];
  body.append(h("h2.section-title", `Not installed by the launcher · ${unmanaged.length}`));
  body.append(h("p.muted.small", "Plugins and scripts in the game folder that the game doesn't ship and the launcher didn't install. Quarantine moves one out of the game (you can put it back) — handy when you're hunting for the mod that crashes the game."));
  if (!unmanaged.length) {
    body.append(h("p.muted", "Nothing unexpected."));
  } else {
    body.append(h("div.table", unmanaged.map((u) => h("div.table-row",
      h(`span.kind-tag.k-${u.kind}`, u.kind),
      h("div.grow", h("div.mono.ellipsis", { title: u.path }, u.path), u.note ? h("div.dim.small", u.note) : null),
      h("span.dim", fmtBytes(u.size)),
      h("span.dim", u.modified ? fmtDate(u.modified) : ""),
      h("button.btn.btn-sm", { type: "button", onclick: () => quarantine(u) }, ic("quarantine", "ico-sm"), "Quarantine")))));
  }

  const q = cache.quarantine || [];
  if (q.length) {
    body.append(h("h2.section-title", `Quarantine · ${q.length}`));
    body.append(h("div.table", q.map((item) => h("div.table-row",
      ic("quarantine", "ico-sm"),
      h("div.grow.mono.ellipsis", { title: item.path }, item.path),
      h("span.dim", `moved ${fmtDate(item.movedAt)}`),
      h("button.btn.btn-sm", { type: "button", onclick: () => restore(item) }, ic("undo", "ico-sm"), "Put back")))));
  }
}

function logItem(l) {
  const n = (l.problems || []).length;
  const worst = (l.problems || []).some((p) => /block|error|fatal/i.test(p.level)) ? "bad" : n ? "warn" : "ok";
  const open = openLogs.has(l.path);
  const tailBox = h("pre.log-tail", { tabindex: "0" });
  if (open) fillTail(tailBox, l);
  tailBox.hidden = !open;
  const toggle = h("button.btn.btn-sm.btn-ghost", { type: "button", disabled: !l.exists || null, "aria-expanded": open ? "true" : "false" },
    ic("terminal", "ico-sm"), open ? "Hide" : "Show end");
  toggle.addEventListener("click", () => {
    if (openLogs.has(l.path)) openLogs.delete(l.path); else openLogs.add(l.path);
    render();
  });
  return h(`div.log-item.${l.exists ? worst : "absent"}`,
    h("div.log-head",
      h("div.health-icon", ic(!l.exists ? "minus" : worst === "ok" ? "check" : "alert")),
      h("div.grow",
        h("b.log-name", l.name),
        h("div.dim.small", l.exists
          ? [fmtBytes(l.size || 0), l.modified ? `written ${fmtDate(l.modified)}` : null].filter(Boolean).join(" · ")
          : "Not there (the mod that writes it isn't installed or hasn't run)")),
      n ? h("span.badge.badge-warn", `${n} problem${n === 1 ? "" : "s"}`) : null,
      toggle,
      l.exists ? h("button.btn.btn-sm.btn-ghost", { type: "button", onclick: () => openLog(l.path) }, ic("external", "ico-sm"), "Notepad") : null),
    n ? h("ul.log-problems", (l.problems || []).slice(0, 6).map((p) => h(`li.lvl-${String(p.level || "").toLowerCase()}`, p.line ? h("span.dim", `line ${p.line}: `) : null, p.text))) : null,
    tailBox);
}

function fillTail(box, l) {
  clear(box);
  const lines = (l.tail || "").split(/\r?\n/);
  const needles = (l.problems || []).map((p) => (p.text || "").trim()).filter((t) => t.length > 3);
  for (const line of lines) {
    const hit = needles.some((n) => line.includes(n) || n.includes(line.trim()) && line.trim().length > 8)
      || /\b(error|exception|fatal|failed|crash)\b/i.test(line);
    box.append(h(hit ? "span.hit" : "span", line + "\n"));
  }
}

async function openLog(path) {
  try { await host.call("diag.openLog", { path }); } catch (e) { toastError(e); }
}

async function quarantine(u) {
  const ok = await confirmDialog({
    title: `Quarantine ${u.path.split(/[\\/]/).pop()}?`,
    text: "It's moved out of the game folder into the launcher's quarantine, so the game stops loading it. You can put it back from this page at any time.",
    ok: "Quarantine",
  });
  if (!ok) return;
  try {
    await host.call("diag.quarantine", { path: u.path });
    toast(`${u.path} is in quarantine.`, "ok");
  } catch (e) {
    toastError(e);
  }
  fetchReport(true);
}

async function restore(item) {
  try {
    await host.call("diag.restore", { id: item.id });
    toast(`${item.path} is back in the game folder.`, "ok");
  } catch (e) {
    toastError(e);
  }
  fetchReport(true);
}

// ---------------------------------------------------------------- frameworks

async function fetchFrameworks(force) {
  if (loading.frameworks || (cache.frameworks && !force)) return;
  loading.frameworks = true;
  try {
    cache.frameworks = await host.call("frameworks.list", {});
  } catch (e) {
    cache.frameworks = { error: e };
  }
  loading.frameworks = false;
  if (state.page === "tools" && tab === "frameworks") render();
}

function frameworks(body) {
  body.append(h("div.tools-bar",
    h("p.muted", "Downloaded straight from each project's official release, then installed like any other mod (you see the plan first)."),
    h("button.btn", { type: "button", disabled: loading.frameworks || null, onclick: () => fetchFrameworks(true) }, ic("refresh"), "Check again")));

  const edition = currentEdition();
  const rows = h("div.fw-list");
  body.append(rows);
  // Script Hook V has no public repository: link the official site
  rows.append(h("div.fw-row",
    h("div.fw-icon", ic("bolt")),
    h("div.grow", h("b.fw-name", "Script Hook V"), h("div.dim.small", "Alexander Blade's official site · needed by almost every mod · updated after each game patch")),
    h("span.badge.badge-site", "Both"),
    h("button.btn.btn-sm", { type: "button", onclick: () => host.call("shell.openUrl", { url: "http://www.dev-c.com/gtav/scripthookv/" }).catch(toastError) }, ic("external", "ico-sm"), "dev-c.com")));

  const list = cache.frameworks;
  if (!list) {
    rows.append(h("div.row.muted", { style: { padding: "16px 0" } }, h("div.spinner"), "Checking the latest releases…"));
    fetchFrameworks(false);
    return;
  }
  if (list.error) {
    rows.append(h("div.alert.danger", ic("alert"), h("div.alert-body", h("b", "Couldn't check the releases. "), list.error.message)));
    return;
  }
  for (const f of list) {
    const forThis = f.forEdition === "Both" || f.forEdition === edition;
    const upToDate = f.installedVersion && f.latestVersion && f.installedVersion === f.latestVersion;
    const status = !f.installedVersion ? h("span.badge.badge-off", "Not installed")
      : upToDate ? h("span.badge.badge-installed", "Up to date")
      : h("span.badge.badge-warn", `v${f.installedVersion}`);
    const label = !f.installedVersion ? "Install" : upToDate ? "Reinstall" : "Update";
    rows.append(h(`div.fw-row${forThis ? "" : ".other-edition"}`,
      h("div.fw-icon", ic("package")),
      h("div.grow",
        h("b.fw-name", f.name),
        h("div.dim.small", [f.latestVersion ? `Latest ${f.latestVersion}` : "Latest unknown", f.installedVersion ? `installed ${f.installedVersion}` : null].filter(Boolean).join(" · "),
          forThis ? "" : ` · made for ${f.forEdition}, not your ${edition} game`)),
      h(`span.badge.badge-${f.forEdition === "Enhanced" ? "enhanced" : f.forEdition === "Legacy" ? "legacy" : "site"}`, f.forEdition),
      status,
      h("button.btn.btn-sm.btn-ghost", { type: "button", onclick: () => host.call("shell.openUrl", { url: f.releaseUrl }).catch(toastError), title: f.releaseUrl }, ic("external", "ico-sm"), "Releases"),
      h(`button.btn.btn-sm${forThis && label !== "Reinstall" ? ".btn-primary" : ""}`, { type: "button", disabled: !f.canInstall || null, onclick: () => installFramework(f) }, ic("download", "ico-sm"), label)));
  }
}

export async function installFramework(f) {
  try {
    await host.call("frameworks.install", { id: f.id || f });
    toast(`Downloading ${f.name || f} from its official release. The install plan opens when it's done.`, "info", 6000);
  } catch (e) {
    toastError(e);
  }
}

// ---------------------------------------------------------------- storage

async function fetchStorage(force) {
  if (loading.storage || (cache.storage && !force)) return;
  loading.storage = true;
  try {
    cache.storage = await host.call("storage.report", {});
  } catch (e) {
    cache.storage = { error: e };
  }
  loading.storage = false;
  if (state.page === "tools" && tab === "storage") render();
  if (state.page === "settings") render();
}

/** The storage card (also shown in Settings). */
export function storageCard() {
  syncGame();
  const card = h("section.card", h("h2.card-title", "Storage",
    h("button.btn.btn-sm.btn-ghost.btn-icon", { type: "button", title: "Measure again", "aria-label": "Measure storage again", onclick: () => fetchStorage(true) }, ic("refresh", "ico-sm"))));
  const s = cache.storage;
  if (!s) {
    card.append(h("div.row.muted", h("div.spinner"), "Measuring…"));
    fetchStorage(false);
    return card;
  }
  if (s.error) {
    card.append(h("p.muted", `Couldn't measure: ${s.error.message}`));
    return card;
  }
  const items = [
    ["modsFolder", "Game's mods folder", "The archive copies your mods live in. Removing mods doesn't shrink it on purpose (other tools may use it).", null],
    ["payloads", "Kept downloads", "So mods can be switched back on or reinstalled without downloading again.", null],
    ["downloads", "Unfinished downloads", "Files still waiting to be installed.", "downloads"],
    ["backups", "Backups", "What mods replaced — needed to uninstall them cleanly.", null],
    ["cache", "Cache", "Game art and the replace-mod index. Rebuilt when needed.", "cache"],
  ];
  const max = Math.max(1, ...items.map(([k]) => s[k] || 0));
  card.append(h("div.storage", items.map(([k, label, help, clean]) => h("div.storage-row",
    h("div.storage-text", h("b", label), h("span", help)),
    h("div.storage-bar", h("div", { style: { width: `${Math.max(1.5, ((s[k] || 0) / max) * 100).toFixed(1)}%` } })),
    h("b.num.storage-size", fmtBytes(s[k] || 0)),
    clean ? h("button.btn.btn-sm", { type: "button", onclick: () => cleanStorage(clean, label) }, ic("trash", "ico-sm"), "Clean") : h("span.storage-pad")))));
  card.append(h("div.row", { style: { marginTop: "12px", justifyContent: "flex-end" } },
    h("button.btn.btn-sm", { type: "button", onclick: () => cleanStorage("staging", "Leftover unpacked files") }, ic("trash", "ico-sm"), "Clear unpacked leftovers")));
  return card;
}

async function cleanStorage(what, label) {
  const ok = await confirmDialog({ title: `Clean ${label.toLowerCase()}?`, text: what === "downloads" ? "Files waiting to be installed are deleted. Installed mods aren't affected." : "Nothing installed is affected; the launcher rebuilds what it needs.", ok: "Clean" });
  if (!ok) return;
  try {
    cache.storage = await host.call("storage.clean", { what });
    toast("Cleaned.", "ok", 2000);
  } catch (e) {
    toastError(e);
  }
  if (state.page === "tools" || state.page === "settings") render();
}

function storage(body) {
  body.append(h("div.settings", storageCard()));
}
