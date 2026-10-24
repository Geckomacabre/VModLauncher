// Tools tab: Game files — finds edits made straight to the game's own archives (OpenIV without its
// mods folder) and mod packs dropped into the game's own folder, which a game update or a store's
// file check silently undoes. It only reads archive headers. bridge.md "Wave 3".
import { host } from "../bridge.js";
import { h, ic, clear, fmtBytes, toastError, confirmDialog } from "../ui.js";
import { state, selectedGame } from "../state.js";

const cache = { gameId: null, report: null, loading: false, busy: null, verify: null };
let mounted = null;

/** Another game was selected: what was found belongs to the old one. */
function syncGame() {
  if (cache.gameId === state.selectedGameId) return;
  cache.gameId = state.selectedGameId;
  cache.report = cache.verify = null;
}

export function renderGameFiles(body) {
  syncGame();
  mounted = h("div.gamefiles");
  body.append(mounted);
  paint();
  if (!cache.report) check(false);
  if (!cache.verify) loadVerify();
}

async function check(force) {
  if (cache.loading || (cache.report && !force)) return;
  cache.loading = true;
  const gameId = state.selectedGameId;
  paint();
  try {
    const report = await host.call("gamefiles.check", {});
    if (gameId === state.selectedGameId) cache.report = report;
  } catch (e) {
    if (gameId === state.selectedGameId) cache.report = { error: e };
  }
  cache.loading = false;
  paint();
}

async function loadVerify() {
  const gameId = state.selectedGameId;
  try {
    const v = await host.call("gamefiles.verify", {});
    if (gameId === state.selectedGameId) { cache.verify = v; paint(); }
  } catch { /* the guidance is a nicety */ }
}

function paint() {
  if (!mounted?.isConnected) return;
  clear(mounted);
  const report = cache.report;
  mounted.append(h("div.tools-bar",
    h("p.muted", "Reads the first bytes of each of the game's own archives: Rockstar ships them encrypted, and an archive edited in place is saved open. Nothing is changed by checking."),
    h("button.btn", { type: "button", disabled: cache.loading || cache.busy || null, onclick: () => check(true) },
      cache.loading ? h("div.spinner", { style: { width: "14px", height: "14px" } }) : ic("refresh"), cache.loading ? "Checking…" : "Check again")));

  if (!report) {
    mounted.append(h("div.row.muted", { style: { padding: "26px 0" } }, h("div.spinner"), "Checking the game's archives…"));
    return;
  }
  if (report.error) {
    mounted.append(h("div.alert.danger", ic("alert"), h("div.alert-body", h("b", "Couldn't check the game files. "), report.error.message)));
    return;
  }

  const archives = report.archives || [];
  const edited = archives.filter((a) => a.state === "edited");
  const unreadable = archives.filter((a) => a.state === "unreadable");
  const strays = report.strayPacks || [];
  const clean = !edited.length && !strays.length && !unreadable.length;

  if (clean) {
    mounted.append(h("div.empty", { style: { padding: "34px 20px" } },
      ic("shield"),
      h("h3", "Everything original"),
      h("p", `All ${archives.length} of the game's archives are as Rockstar ships them. Your mods live in the mods folder, where updates and file checks leave them alone.`)));
  } else {
    mounted.append(h("div", { style: { display: "grid", gap: "8px", margin: "10px 0 14px" } },
      (report.advice || []).map((text) => h(`div.notice.lvl-${edited.length || strays.length ? "warn" : "info"}`, ic(edited.length || strays.length ? "alert" : "info", "ico-sm"), h("div.grow", text)))));
  }

  if (edited.length) {
    mounted.append(h("h2.section-title", `Edited in place · ${edited.length}`));
    mounted.append(h("p.muted.small", "\"Move my edits into mods\" copies the edited archive into the mods folder, where the game keeps loading it through OpenIV.asi (Legacy) or OpenRPF (Enhanced). The game's own file isn't touched; restore it through your store afterwards."));
    mounted.append(h("div.table", edited.map((a) => h("div.table-row",
      ic("alert", "ico-sm"),
      h("div.grow.mono.ellipsis", { title: a.path }, a.path),
      h("span.dim", fmtBytes(a.size)),
      h("button.btn.btn-sm", { type: "button", disabled: !!cache.busy || null, onclick: () => moveToMods(a) },
        cache.busy === a.path ? h("div.spinner", { style: { width: "12px", height: "12px" } }) : ic("folder", "ico-sm"),
        cache.busy === a.path ? "Copying…" : "Move my edits into mods")))));
  }

  if (edited.length || unreadable.length) restoreCard();

  if (strays.length) {
    mounted.append(h("h2.section-title", `Mod packs in the game's own folder · ${strays.length}`));
    mounted.append(h("p.muted.small", "These packs aren't Rockstar's, but they sit in update\\x64\\dlcpacks, where a file check deletes them and an update can break them. Copy each folder to mods\\update\\x64\\dlcpacks, delete it here, then register it on the Add-on packs tab."));
    mounted.append(h("div.table", strays.map((s) => h("div.table-row",
      ic("package", "ico-sm"),
      h("b", s.name),
      h("div.grow.mono.dim.ellipsis", { title: s.path }, s.path)))));
  }

  if (unreadable.length) {
    mounted.append(h("h2.section-title", `Couldn't read · ${unreadable.length}`));
    mounted.append(h("div.table", unreadable.map((a) => h("div.table-row",
      ic("block", "ico-sm"),
      h("div.grow.mono.ellipsis", { title: a.path }, a.path),
      h("span.dim", fmtBytes(a.size))))));
  }

  if (archives.length) {
    mounted.append(h("div", { style: { marginTop: "18px" } }, h("details.skipped",
      h("summary", ic("chevron", "ico-sm"), `All archives checked · ${archives.length}`),
      h("div.table", archives.map((a) => h("div.table-row",
        ic(a.state === "original" ? "check" : a.state === "edited" ? "alert" : "block", "ico-sm"),
        h("div.grow.mono.ellipsis", { title: a.path }, a.path),
        h(`span.badge.${a.state === "original" ? "badge-off" : "badge-warn"}`, a.state),
        h("span.dim", fmtBytes(a.size))))))));
  }
}

/** How to get Rockstar's files back: the store's own file check. */
function restoreCard() {
  const v = cache.verify;
  const g = selectedGame();
  const store = g?.store && g.store !== "Custom" ? g.store === "Rockstar" ? "the Rockstar Games Launcher" : g.store : "your store";
  mounted.append(h("h2.section-title", `Restore the originals through ${store}`));
  mounted.append(h("div.alert.info", ic("shield"),
    h("div.alert-body", v ? v.how : "Use your store's file check: it puts Rockstar's original files back and leaves the mods folder alone."),
    v?.url ? h("button.btn.btn-sm", { type: "button", onclick: startVerify }, ic("external", "ico-sm"), "Start verification") : null));
}

async function startVerify() {
  const ok = await confirmDialog({
    title: "Verify the game files in Steam?",
    text: "Steam checks every file and downloads the originals back. Copy any edits you want to keep into mods first: edits left in the game's own archives are lost. Close the game before it starts.",
    ok: "Start verification",
  });
  if (!ok) return;
  try { await host.call("gamefiles.verify", { open: true }); } catch (e) { toastError(e); }
}

async function moveToMods(a) {
  if (cache.busy) return;
  const ok = await confirmDialog({
    title: `Copy ${a.path.split("\\").pop()} into the mods folder?`,
    text: `The edited archive is copied to mods\\${a.path}, where the game keeps loading it. The game's own file stays as it is until you restore it through your store.`,
    ok: "Copy into mods",
  });
  if (!ok) return;
  cache.busy = a.path;
  const gameId = state.selectedGameId;
  paint();
  try {
    const report = await host.call("gamefiles.moveToMods", { path: a.path });
    if (gameId === state.selectedGameId) cache.report = report;
  } catch (e) {
    toastError(e);
  } finally {
    cache.busy = null;
    paint();
  }
}
