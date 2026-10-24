// Tools tab: Add-on packs — every pack in the dlclist the game actually reads, what's wrong with them
// (files gone, listed twice, never registered, dropped by NaturalVision's own list) and a switch per
// pack. The pack builder sits on top. Rockstar's own packs are shown, never changed. bridge.md "Wave 3".
import { host } from "../bridge.js";
import { h, ic, clear, fmtBytes, toast, toastError, confirmDialog, copyText } from "../ui.js";
import { state } from "../state.js";
import { packBuilderCard } from "./packbuilder.js";

const cache = { gameId: null, report: null, loading: false, busy: false, quietUntil: 0, query: "", rockstarOpen: false };
let mounted = null;   // the pack manager's own box (the builder card above it is left alone)
let listBox = null;   // the part the search box repaints

/** Another game was selected: its packs are another story. */
function syncGame() {
  if (cache.gameId === state.selectedGameId) return;
  cache.gameId = state.selectedGameId;
  cache.report = null;
  cache.query = "";
  cache.rockstarOpen = false;
}

// Something else installed or removed a mod (Browse, Library, the builder above): read the list again.
host.on("library.changed", () => {
  if (cache.busy || Date.now() < cache.quietUntil) return; // our own change: its answer carries the new list
  cache.report = null;
  if (mounted?.isConnected) load(false);
});

export function renderPacks(body) {
  syncGame();
  body.append(packBuilderCard());
  mounted = h("div.packs");
  body.append(mounted);
  paint();
  if (!cache.report) load(false);
}

async function load(force) {
  if (cache.loading || (cache.report && !force)) return;
  cache.loading = true;
  const gameId = state.selectedGameId;
  paint();
  try {
    const report = await host.call("packs.list", {});
    if (gameId === state.selectedGameId) cache.report = report;
  } catch (e) {
    if (gameId === state.selectedGameId) cache.report = { error: e };
  }
  cache.loading = false;
  paint();
}

function paint() {
  if (!mounted?.isConnected) return;
  clear(mounted);
  const report = cache.report;
  mounted.append(h("h2.section-title", "Pack manager"));
  mounted.append(h("div.tools-bar",
    h("p.muted", report && !report.error ? readsLine(report.list) : "Every add-on pack the game loads, and the ones it doesn't."),
    h("button.btn", { type: "button", disabled: cache.loading || cache.busy || null, onclick: () => load(true) },
      cache.loading ? h("div.spinner", { style: { width: "14px", height: "14px" } }) : ic("refresh"), cache.loading ? "Reading…" : "Read again")));

  if (!report) {
    mounted.append(h("div.row.muted", { style: { padding: "26px 0" } }, h("div.spinner"), "Reading the dlclist…"));
    return;
  }
  if (report.error) {
    mounted.append(h("div.alert.danger", ic("alert"), h("div.alert-body", h("b", "Couldn't read the pack list. "), report.error.message)));
    return;
  }

  const problems = report.problems || [];
  if (problems.length) mounted.append(h("div", { style: { display: "grid", gap: "8px", margin: "10px 0 14px" } }, problems.map(problem)));

  const search = h("input.input", { type: "search", placeholder: "Find a pack or spawn name", "aria-label": "Find a pack", value: cache.query });
  search.addEventListener("input", () => { cache.query = search.value; paintList(); });
  mounted.append(h("div.row", { style: { margin: "4px 0 2px" } }, h("div.search", ic("search", "ico-sm"), search)));

  listBox = h("div");
  mounted.append(listBox);
  paintList();
}

function readsLine(list) {
  const what = list.kind === "onigiri" ? "NaturalVision's own list"
    : list.kind === "mods" ? "the mods copy of update.rpf"
    : "its own update.rpf (no mods copy yet)";
  return h("span", "The game reads ", h("b", what), " ", h("span.mono.dim.small", list.path));
}

const FIX_LABEL = {
  duplicates: "Remove the extra lines",
  "onigiri-sync": "Add them to NaturalVision's list",
};

function problem(p) {
  const level = String(p.level || "info").toLowerCase();
  let button = null;
  if (p.fix) {
    const label = FIX_LABEL[p.fix] || (p.fix.startsWith("register:") ? "Register it" : p.fix.startsWith("missing:") ? "Take it off the list" : "Fix");
    button = h("button.btn.btn-sm", { type: "button", disabled: cache.busy || null, onclick: () => fix(p.fix) }, ic("wrench", "ico-sm"), label);
  }
  return h(`div.notice.lvl-${level}`,
    ic(level === "block" ? "block" : level === "warn" ? "alert" : "info", "ico-sm"),
    h("div.grow", p.text),
    button);
}

function paintList() {
  if (!listBox || !cache.report?.packs) return;
  clear(listBox);
  const q = cache.query.trim().toLowerCase();
  const match = (r) => !q || r.name.toLowerCase().includes(q) || r.item.toLowerCase().includes(q)
    || (r.owner && r.owner.name.toLowerCase().includes(q)) || (r.spawnNames || []).some((n) => n.toLowerCase().includes(q));
  const packs = cache.report.packs;
  const mine = packs.filter((r) => r.source !== "rockstar");
  const rockstar = packs.filter((r) => r.source === "rockstar");
  const mineShown = mine.filter(match);
  const rockstarShown = rockstar.filter(match);

  listBox.append(h("h2.section-title", `Your add-on packs · ${mine.length}`));
  if (!mine.length) {
    listBox.append(h("p.muted", "No add-on packs yet. Install one from Browse, or build one from loose files above."));
  } else if (!mineShown.length) {
    listBox.append(h("p.muted", `None of your packs match "${cache.query.trim()}".`));
  } else {
    listBox.append(h("div.table", mineShown.map(packRow)));
  }

  const det = h("details.skipped", { open: cache.rockstarOpen || (q && rockstarShown.length) || null },
    h("summary", ic("chevron", "ico-sm"), `Rockstar's packs · ${q ? `${rockstarShown.length} of ${rockstar.length}` : rockstar.length}`),
    h("p.muted.small", "The game's own DLC, in load order. Shown so you can see what loads, never changed: switching one off can break saves and online content."),
    rockstarShown.length ? h("div.table", rockstarShown.map(rockstarRow)) : null);
  det.addEventListener("toggle", () => { if (!q) cache.rockstarOpen = det.open; });
  listBox.append(h("div", { style: { marginTop: "18px" } }, det));
}

const STATUS = {
  missing: ["badge-warn", "Files gone", "It's listed, but its dlc.rpf isn't there any more."],
  duplicate: ["badge-warn", "Listed twice", "The list names it more than once. It still loads; the extra lines are just clutter."],
  disabled: ["badge-off", "Off", "Switched off here: it's out of the list, its files are kept."],
  notRegistered: ["badge-warn", "Not registered", "Its files are there, but no list loads it."],
  notInActiveList: ["badge-warn", "Dropped by NaturalVision", "It's in update.rpf's list, but NaturalVision's list is the one the game reads."],
};

function packRow(r) {
  const status = STATUS[r.status];
  const canSwitch = r.status === "ok" || r.status === "duplicate" || r.status === "disabled" || r.status === "notInActiveList";
  const on = r.status === "ok" || r.status === "duplicate";

  let control = null;
  if (canSwitch) {
    const sw = h("input", { type: "checkbox", role: "switch", "aria-label": `${r.name} on or off` });
    sw.checked = on;
    sw.disabled = cache.busy;
    sw.addEventListener("change", () => setEnabled(r, sw.checked, sw));
    control = h("label.switch", { title: on ? "Take it out of the list (its files stay)" : "Put it in the list the game reads" }, sw, h("span.switch-track"));
  } else if (r.status === "notRegistered") {
    control = h("button.btn.btn-sm", { type: "button", disabled: cache.busy || null, onclick: () => fix("register:" + r.name) }, ic("plus", "ico-sm"), "Register");
  } else if (r.status === "missing") {
    control = h("button.btn.btn-sm.btn-ghost", { type: "button", disabled: cache.busy || null, onclick: () => fix("missing:" + r.item) }, ic("trash", "ico-sm"), "Take off list");
  }

  const meta = [
    r.owner ? h("span", "from ", h("b", r.owner.name)) : h("span", "not installed by the launcher"),
    r.folder ? h("span.mono.ellipsis", { title: r.folder }, r.folder) : null,
    r.sizeBytes ? h("span", fmtBytes(r.sizeBytes)) : null,
  ];
  return h("div.table-row", { style: on || !canSwitch ? null : { opacity: ".75" } },
    h("div.health-icon", ic(r.status === "ok" ? "check" : r.status === "disabled" ? "minus" : "alert")),
    h("div.grow",
      h("div.row", { style: { gap: "8px" } }, h("b", r.name), h("span.mono.dim.small", r.item),
        status ? h(`span.badge.${status[0]}`, { title: status[2] }, status[1]) : null),
      h("div.dim.small.row", { style: { gap: "12px", flexWrap: "wrap", marginTop: "2px" } }, meta),
      spawnRow(r)),
    control);
}

function rockstarRow(r) {
  return h("div.table-row",
    ic("lock", "ico-sm"),
    h("div.grow.row", { style: { gap: "10px" } }, h("b", r.name), h("span.mono.dim.small", r.item)),
    r.sizeBytes ? h("span.dim", fmtBytes(r.sizeBytes)) : h("span.dim.small", "built into the game"));
}

/** What trainers spawn by name, as on the Library page: each name copies on click. */
function spawnRow(r) {
  const names = r.spawnNames || [];
  if (!names.length) return null;
  const shown = names.slice(0, 10);
  return h("div.spawn-row",
    h("span.spawn-label", { title: "Type these into a trainer's spawn-by-name box" }, "Spawn"),
    shown.map((n) => h("button.spawn-chip", { type: "button", title: `Copy ${n}`, onclick: () => copyText(n, `"${n}" copied`) }, n)),
    names.length > shown.length ? h("span.dim.small", `+${names.length - shown.length}`) : null,
    names.length > 1 ? h("button.btn.btn-sm.btn-ghost", { type: "button", onclick: () => copyText(names.join("\n"), `${names.length} spawn names copied`) }, ic("copy", "ico-sm"), "Copy all") : null);
}

// ---------------------------------------------------------------- changes

async function change(cmd, args, done) {
  if (cache.busy) return false;
  cache.busy = true;
  const gameId = state.selectedGameId;
  paint();
  try {
    const report = await host.call(cmd, args);
    if (gameId === state.selectedGameId) cache.report = report;
    if (done) toast(done, "ok");
    return true;
  } catch (e) {
    toastError(e);
    return false;
  } finally {
    cache.busy = false;
    cache.quietUntil = Date.now() + 1000; // its library.changed may land just after the answer
    paint();
  }
}

async function setEnabled(r, enabled, sw) {
  const ok = await change("packs.setEnabled", { item: r.item, enabled },
    enabled ? `${r.name} is back in the list the game reads.` : `${r.name} is switched off. Its files are kept; switch it back on any time.`);
  if (!ok && sw?.isConnected) sw.checked = !enabled;
}

async function fix(f) {
  if (f === "onigiri-sync") {
    const yes = await confirmDialog({
      title: "Add the dropped packs to NaturalVision's list?",
      text: "They're added to onigiri\\common\\data\\dlclist.xml and copied into onigiri\\dlcpacks, which is where NaturalVision loads packs from. Uninstalling \"Pack manager changes\" in the Library undoes it.",
      ok: "Add them",
    });
    if (!yes) return;
  } else if (f.startsWith("missing:")) {
    const name = f.slice(8).replace(/\/+$/, "").split("/").pop();
    const yes = await confirmDialog({
      title: `Take ${name} off the list?`,
      text: "Its files are already gone, so it doesn't load anyway. This only removes its line from the dlclist.",
      ok: "Take it off",
    });
    if (!yes) return;
  }
  const done = f === "duplicates" ? "Extra lines removed. Each pack is listed once, in its first place."
    : f === "onigiri-sync" ? "NaturalVision's list has every pack now."
    : f.startsWith("register:") ? `${f.slice(9)} is registered. It loads the next time the game starts.`
    : "Taken off the list.";
  await change("packs.fix", { fix: f }, done);
}
