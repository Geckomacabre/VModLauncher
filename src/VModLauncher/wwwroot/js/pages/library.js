import { host } from "../bridge.js";
import { h, ic, clear, fmtDate, lazyImg, toast, toastError, confirmDialog, isSiteUrl, copyText } from "../ui.js";
import { panelArt } from "../art.js";
import { state, changed, selectedGame } from "../state.js";
import { navigate } from "../router.js";
import { importFile } from "./home.js";

const openFiles = new Map(); // mod id -> entries (expanded "Files" sections)
const updates = new Map();   // mod id -> { latestVersion, hasUpdate, error } from library.checkUpdates
let checking = false;
let loading = false;
let filter = "";

export async function loadLibrary() {
  if (!selectedGame()) { state.library = []; state.libraryLoaded = true; return; }
  loading = true;
  try {
    state.library = await host.call("library.list", {});
  } catch (e) {
    toastError(e, "Couldn't read the mod list");
  } finally {
    // marked loaded even on failure so a broken host can't cause a reload loop
    state.libraryLoaded = true;
    loading = false;
  }
}

/** Reloads the list from the host and tells every view. */
export async function refreshLibrary() {
  await loadLibrary();
  changed("library");
}

export function renderLibrary(root) {
  clear(root);
  const page = h("div.page");
  root.append(page);
  const game = selectedGame();

  const search = h("input.input", { type: "search", placeholder: "Filter installed mods…", "aria-label": "Filter installed mods", value: filter });
  search.addEventListener("input", () => { filter = search.value; paintList(); });

  page.append(h("div.page-head",
    h("div", h("h1.page-title", "Library"), h("p.page-sub", game ? `Mods installed into ${game.name} by the launcher.` : "No game selected.")),
    h("div.lib-toolbar",
      h("label.search", ic("search"), search),
      h("button.btn", {
        type: "button", disabled: checking || !game || !state.library.some((m) => m.pageUrl) || null,
        onclick: () => checkUpdates(root),
        title: "Reads each mod's page on its site (about a second per mod)",
      }, checking ? h("div.spinner", { style: { width: "14px", height: "14px" } }) : ic("refresh"), checking ? "Checking…" : "Check for updates"),
      h("button.btn", { type: "button", onclick: importFile }, ic("plus"), "Install from file…"))));

  const listBox = h("div");
  page.append(listBox);

  function paintList() {
    clear(listBox);
    if (!game) {
      listBox.append(h("div.empty", ic("car"), h("h3", "No game selected"), h("p", "Pick or add a GTA V install on the Home page first.")));
      return;
    }
    if (!state.libraryLoaded) {
      listBox.append(h("div.row.muted", { style: { padding: "30px 0" } }, h("div.spinner"), "Reading the mod list…"));
      return;
    }
    const mods = state.library.filter((m) => !filter || `${m.name} ${m.author || ""} ${m.summary || ""}`.toLowerCase().includes(filter.toLowerCase()));
    if (!state.library.length) {
      listBox.append(h("div.empty", panelArt("library-empty", "empty-art"), ic("library"),
        h("h3", "No mods yet"),
        h("p", "Mods you install through the launcher show up here, where you can switch them off or remove them cleanly."),
        h("button.btn.btn-primary", { type: "button", onclick: () => navigate("browse") }, ic("grid"), "Browse mods")));
      return;
    }
    if (!mods.length) {
      listBox.append(h("div.empty", ic("search"), h("h3", "No match"), h("p", `Nothing installed matches “${filter}”.`)));
      return;
    }
    const enabled = state.library.filter((m) => m.enabled).length;
    const conflicted = state.library.filter((m) => m.enabled && (m.conflicts || []).length).length;
    listBox.append(h("p.result-note", `${state.library.length} mod${state.library.length === 1 ? "" : "s"} · ${enabled} on`,
      conflicted ? h("span.badge.badge-warn", { title: "Mods that change the same files: the one installed last wins" }, `${conflicted} overlapping`) : null));
    listBox.append(h("div.lib-list", mods.map(item)));
  }

  paintList();
  if (game && !state.libraryLoaded && !loading) loadLibrary().then(() => { if (state.page === "library") renderLibrary(root); });
}

function item(m) {
  const sw = h("input", { type: "checkbox", role: "switch", "aria-label": `${m.name} on or off` });
  sw.checked = m.enabled;
  const canToggle = m.enabled || m.canReEnable;
  sw.disabled = !canToggle;
  const swLabel = h("label.switch", { title: canToggle ? (m.enabled ? "Turn off (takes it out of the game, keeps it here)" : "Turn back on") : "The launcher no longer has this mod's download. Install it again from its page." }, sw, h("span.switch-track"));
  sw.addEventListener("change", () => setEnabled(m, sw.checked, sw));

  const filesBox = h("div.lib-files");
  const filesShown = openFiles.has(m.id);
  if (filesShown) fillFiles(filesBox, openFiles.get(m.id));
  filesBox.hidden = !filesShown;

  const filesBtn = h("button.btn.btn-sm.btn-ghost", { type: "button", "aria-expanded": filesShown ? "true" : "false", onclick: () => toggleFiles(m, filesBox, filesBtn) }, ic("list", "ico-sm"), "Files");

  const source = m.source === "gta5mods" ? "GTA5-Mods" : m.source === "lcpdfr" ? "LCPDFR" : "Local file";
  const kind = KIND[m.kind] || null;
  return h(`div.lib-item${m.enabled ? "" : ".disabled"}`,
    h("div.lib-row",
      h("div.lib-img", lazyImg(m.imageUrl, ""), kind ? h("span.lib-kind", { title: kind.label }, ic(kind.icon, "ico-sm")) : null),
      h("div", { style: { minWidth: 0 } },
        h("div.lib-name", h("span", { title: m.name }, m.name), m.version ? h("span.dim", { style: { fontWeight: 500 } }, `v${String(m.version).replace(/^v/i, "")}`) : null,
          m.enabled ? null : h("span.badge.badge-off", "Off"),
          updatePill(m)),
        h("div.lib-meta",
          h("span.badge.badge-site", source),
          kind ? h("span", kind.label) : null,
          m.summary ? h("span", m.summary) : null,
          m.author ? h("span", `by ${m.author}`) : null,
          h("span", `installed ${fmtDate(m.installedAt)}`),
          m.changeCount != null ? h("span", `${m.changeCount} change${m.changeCount === 1 ? "" : "s"}`) : null),
        cleanupRow(m),
        spawnRow(m),
        conflictRow(m)),
      h("div.lib-actions",
        filesBtn,
        m.pageUrl ? h("button.btn.btn-sm.btn-ghost", { type: "button", onclick: () => host.call(isSiteUrl(m.pageUrl) ? "site.open" : "shell.openUrl", { url: m.pageUrl }).catch(toastError) }, ic("external", "ico-sm"), "Page") : null,
        h("button.btn.btn-sm.btn-danger", { type: "button", onclick: () => uninstall(m) }, ic("trash", "ico-sm"), "Uninstall"),
        swLabel)),
    filesBox);
}

const KIND = {
  vehicle: { icon: "car", label: "Vehicle" },
  ped: { icon: "user", label: "Ped" },
  weapon: { icon: "gun", label: "Weapon" },
  map: { icon: "map", label: "Map" },
  script: { icon: "code", label: "Script" },
  other: { icon: "package", label: "Other" },
};

/** What trainers spawn by name: each name copies on click, plus a "copy all". */
function spawnRow(m) {
  const names = m.spawnNames || [];
  if (!names.length) return null;
  const shown = names.slice(0, 12);
  return h("div.spawn-row",
    h("span.spawn-label", { title: "Type these into a trainer's spawn-by-name box" }, "Spawn"),
    shown.map((n) => h("button.spawn-chip", { type: "button", title: `Copy ${n}`, onclick: () => copyText(n, `"${n}" copied`) }, n)),
    names.length > shown.length ? h("span.dim.small", `+${names.length - shown.length}`) : null,
    names.length > 1 ? h("button.btn.btn-sm.btn-ghost", { type: "button", onclick: () => copyText(names.join("\n"), `${names.length} spawn names copied`) }, ic("copy", "ico-sm"), "Copy all") : null);
}

/** An uninstall / switch-off that couldn't undo everything (a locked file, …): say so and offer to finish it. */
function cleanupRow(m) {
  if (!m.needsCleanup) return null;
  const retry = h("button.btn.btn-sm.btn-danger", { type: "button" }, ic("refresh", "ico-sm"), "Retry");
  retry.addEventListener("click", async () => {
    retry.disabled = true;
    try {
      const r = await host.call("library.uninstall", { id: m.id });
      if (r && r.problems && r.problems.length) toast(`Still couldn't undo ${r.problems.length} change(s): ${r.problems.slice(0, 2).join("; ")}`, "warn", 12000);
      else toast(`${m.name} is fully removed now.`, "ok");
    } catch (e) {
      toastError(e);
    }
    await refreshLibrary();
  });
  return h("div.cleanup-row", ic("alert", "ico-sm"),
    h("span", h("b", "Uninstall didn't finish. "), "Some of its changes are still in the game (see Files) — usually a file that was in use. Close the game and retry."),
    retry);
}

function conflictRow(m) {
  const c = m.conflicts || [];
  if (!c.length) return null;
  return h("div.conflict-row", ic("alert", "ico-sm"),
    h("span", c.map((x, i) => h("span", { title: (x.paths || []).slice(0, 12).join("\n") },
      i ? "; " : "", `Shares ${x.paths ? x.paths.length : "some"} file${x.paths && x.paths.length === 1 ? "" : "s"} with `, h("b", x.modName)))),
    h("span.dim", " — the mod installed last wins."));
}

function updatePill(m) {
  const u = updates.get(m.id);
  if (!u) return null;
  if (u.hasUpdate) {
    return h("button.update-pill", { type: "button", onclick: () => update(m, u), title: "Download and install the new version (replaces this one)" },
      ic("download", "ico-sm"), u.latestVersion ? `Update to v${String(u.latestVersion).replace(/^v/i, "")}` : "Update available");
  }
  if (u.error) return h("span.badge.badge-warn", { title: u.error }, "Couldn't check");
  return h("span.badge.badge-legacy", { title: "Same version as on the site" }, "Up to date");
}

async function checkUpdates(root) {
  checking = true;
  if (state.page === "library") renderLibrary(root);
  try {
    const results = await host.call("library.checkUpdates", {});
    updates.clear();
    for (const r of results || []) updates.set(r.id, r);
    const n = (results || []).filter((r) => r.hasUpdate).length;
    const failed = (results || []).filter((r) => r.error).length;
    if (n) toast(`${n} update${n === 1 ? "" : "s"} available.`, "ok");
    else if (failed) toast(`Everything else is up to date. ${failed} mod page${failed === 1 ? "" : "s"} couldn't be read.`, "warn");
    else toast("Everything is up to date.", "ok");
  } catch (e) {
    toastError(e, "Couldn't check for updates");
  }
  checking = false;
  if (state.page === "library") renderLibrary(root);
}

async function update(m, u) {
  try {
    await host.call("mod.download", {
      site: m.source, url: m.pageUrl, title: m.name, version: u.latestVersion,
      author: m.author || null, image: m.imageUrl || null,
    });
    toast(`The ${m.source === "lcpdfr" ? "LCPDFR" : "GTA5-Mods"} download page is open. The new version replaces the old one when it installs.`, "info", 7000);
  } catch (e) {
    toastError(e);
  }
}

async function toggleFiles(m, box, btn) {
  if (!box.hidden) {
    box.hidden = true;
    openFiles.delete(m.id);
    btn.setAttribute("aria-expanded", "false");
    return;
  }
  box.hidden = false;
  btn.setAttribute("aria-expanded", "true");
  clear(box).append(h("div.row.muted", h("div.spinner"), "Loading…"));
  try {
    const r = await host.call("library.files", { id: m.id });
    openFiles.set(m.id, r.entries || []);
    fillFiles(box, r.entries || []);
  } catch (e) {
    clear(box).append(h("div.dim", e.message));
  }
}

const TYPE_LABEL = { file: "file", dir: "folder", "dlc-item": "dlclist", "rpf-entry": "archive", "rpf-copy": "copied", "rpf-new": "new rpf", external: "save" };

function fillFiles(box, entries) {
  clear(box);
  if (!entries.length) {
    box.append(h("div.dim", "No changes recorded (the mod is off)."));
    return;
  }
  for (const e of entries) {
    const text = e.type === "dlc-item" ? `${e.item}  (in ${e.path})` : e.inner ? `${e.path} › ${e.inner}` : e.path;
    box.append(h("div.lib-file", h("span.t", TYPE_LABEL[e.type] || e.type), h("span", text)));
  }
}

async function setEnabled(m, enabled, sw) {
  sw.disabled = true;
  try {
    const updated = await host.call("library.setEnabled", { id: m.id, enabled });
    replace(updated);
    toast(enabled ? `${m.name} is back on.` : `${m.name} is off. It's still in your library.`, enabled ? "ok" : "info");
  } catch (e) {
    sw.checked = !enabled;
    toastError(e);
  }
  sw.disabled = false;
  changed("library");
}

async function uninstall(m) {
  if (state.settings.confirmUninstall) {
    const ok = await confirmDialog({
      title: `Uninstall ${m.name}?`,
      text: "Everything it changed goes back to how it was before you installed it, and it leaves your library.",
      ok: "Uninstall", danger: true,
    });
    if (!ok) return;
  }
  try {
    const r = await host.call("library.uninstall", { id: m.id });
    state.library = state.library.filter((x) => x.id !== m.id);
    openFiles.delete(m.id);
    if (r && r.problems && r.problems.length) toast(`${m.name} removed, with ${r.problems.length} thing(s) left as they were: ${r.problems.slice(0, 2).join("; ")}`, "warn", 12000);
    else toast(`${m.name} uninstalled.`, "ok");
  } catch (e) {
    toastError(e);
  }
  changed("library");
}

function replace(updated) {
  const i = state.library.findIndex((x) => x.id === updated.id);
  if (i >= 0) state.library[i] = updated;
}
