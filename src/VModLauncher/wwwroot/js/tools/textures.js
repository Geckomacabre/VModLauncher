// Tools tab: Textures. Finds oversized textures in the player's add-on packs (4K/8K liveries are the usual
// cause of texture loss) and shrinks them by dropping their top mip levels. Every change is a "Smaller
// textures: <pack>" record in Library, so Uninstall there puts the originals back.
import { host } from "../bridge.js";
import { h, ic, clear, fmtNum, fmtBytes, toast, toastError, confirmDialog } from "../ui.js";
import { state } from "../state.js";
import { navigate } from "../router.js";
import { runWithProgress } from "../progress.js";

const SIZES = [[1024, "1024 px"], [2048, "2048 px"], [4096, "4096 px"]];
const T = { gameId: null, maxSize: 2048, scan: null, error: null, selected: new Set(), last: null };
let host_el = null;

function syncGame() {
  if (T.gameId === state.selectedGameId) return;
  T.gameId = state.selectedGameId;
  T.scan = T.error = T.last = null;
  T.selected.clear();
}

export function renderTextures(body) {
  syncGame();
  host_el = h("div.textures");
  body.append(host_el);
  paint();
}

function repaint() {
  if (host_el && host_el.isConnected) paint();
}

function paint() {
  clear(host_el);
  const el = host_el;

  el.append(h("div.tools-bar", { style: { flexWrap: "wrap" } },
    h("p.muted", { style: { flex: "1 1 320px" } }, "Big add-on cars often carry 4K–8K textures that run the game out of video memory (blurry or missing textures). This shrinks them to the size you pick by dropping their largest mip levels: nothing is re-compressed, and the game already shows those smaller levels from a little way off."),
    h("div.row", { style: { gap: "10px", flexShrink: 0 } },
      h("div.segmented", { role: "radiogroup", "aria-label": "Largest texture size" },
        SIZES.map(([size, label]) => h(`button${size === T.maxSize ? ".active" : ""}`, {
          type: "button", role: "radio", "aria-checked": size === T.maxSize ? "true" : "false",
          onclick: () => { if (T.maxSize === size) return; T.maxSize = size; T.scan = null; T.selected.clear(); scan(); },
        }, label, size === 2048 ? h("span.rec", "Recommended") : null))),
      h("button.btn", { type: "button", onclick: () => scan() }, ic("search"), T.scan ? "Scan again" : "Scan"))));

  if (T.last) el.append(resultBox(T.last));

  if (T.error) {
    el.append(h("div.alert.danger", ic("alert"), h("div.alert-body", h("b", "Couldn't read your packs. "), T.error.message || String(T.error))));
    return;
  }
  if (!T.scan) {
    el.append(h("div.empty", ic("box"),
      h("h3", "Scan your add-on packs"),
      h("p", `Lists every add-on pack the game loads, how much of it is textures, and what a ${T.maxSize} px limit would save. Nothing changes until you choose.`),
      h("button.btn.btn-primary", { type: "button", onclick: () => scan() }, "Scan")));
    return;
  }

  const packs = T.scan.packs || [];
  if (!packs.length) {
    el.append(h("div.empty", ic("box"), h("h3", "No add-on packs"),
      h("p", "The game doesn't load any add-on packs from the mods folder (or NaturalVision's onigiri folder) yet. Rockstar's own packs are never touched.")));
    return;
  }

  el.append(h("p.muted.small", { style: { margin: "14px 0 6px" } },
    `${packs.length} pack${packs.length === 1 ? "" : "s"} · ${fmtBytes(T.scan.totalBytes)} of textures · `,
    h("b", `${fmtBytes(T.scan.savableBytes)} can go`), ` at ${T.scan.maxSize} px.`));

  const list = h("div.fw-list");
  el.append(list);
  for (const p of packs) list.append(packRow(p));

  const chosen = packs.filter((p) => T.selected.has(p.id));
  const saving = chosen.reduce((s, p) => s + (p.savableBytes || 0), 0);
  el.append(h("div.row", { style: { justifyContent: "flex-end", gap: "12px", marginTop: "14px" } },
    chosen.length ? h("span.muted", `${chosen.length} selected · saves about ${fmtBytes(saving)}`) : h("span.muted", "Tick the packs to shrink."),
    h("button.btn.btn-primary", { type: "button", disabled: !chosen.length || null, onclick: () => optimize(chosen) },
      ic("bolt"), `Optimize selected${chosen.length ? ` (${chosen.length})` : ""}`)));
}

function canPick(p) {
  if (p.skipped || !p.savableBytes) return false;
  return !(p.alreadyOptimized && p.alreadyOptimized.maxSize === T.scan.maxSize);
}

function packRow(p) {
  const pick = canPick(p);
  const box = h("input", {
    type: "checkbox", "aria-label": `Shrink ${p.name}`, disabled: !pick || null, checked: (pick && T.selected.has(p.id)) || null,
    style: { accentColor: "var(--v)", width: "16px", height: "16px", margin: "0", flex: "none", cursor: pick ? "pointer" : "default" },
    onchange: (e) => { if (e.target.checked) T.selected.add(p.id); else T.selected.delete(p.id); paint(); },
  });

  const lines = [];
  lines.push(h("div.dim.small", p.owner ? `Installed with ${p.owner.name}` : "Not installed by the launcher (OpenIV or by hand)"));
  if (p.skipped) {
    lines.push(h("div.small", { style: { color: "var(--yellow)" } }, p.skipped));
  } else {
    lines.push(h("div.small",
      `${fmtNum(p.textures)} texture${p.textures === 1 ? "" : "s"} · `,
      p.oversized ? h("b", `${fmtNum(p.oversized)} over ${T.scan.maxSize} px`) : `none over ${T.scan.maxSize} px`,
      ` · ${fmtBytes(p.bytes)}`,
      p.savableBytes ? ` → ${fmtBytes(p.bytes - p.savableBytes)}` : ""));
    if (p.kept)
      lines.push(h("div.dim.small", `${p.kept === 1 ? "1 stays as it is" : `${fmtNum(p.kept)} stay as they are`}`
        + ((p.keptReasons || []).length ? ` (${p.keptReasons.join("; ")}).` : ".")));
    if (p.worst && p.worst.length && p.oversized)
      lines.push(h("div.dim.small.mono", { title: p.worst.map((w) => `${w.ytd} › ${w.texture}`).join("\n") },
        "Biggest: " + p.worst.slice(0, 3).map((w) => `${w.texture} ${w.width}×${w.height} ${w.format}`).join(", ")));
  }

  const right = [];
  if (p.alreadyOptimized) {
    right.push(h("span.badge.badge-installed", `Optimized to ${p.alreadyOptimized.maxSize} px`));
    right.push(h("button.btn.btn-sm.btn-ghost", { type: "button", title: "Its record in Library undoes it", onclick: () => navigate("library") }, ic("undo", "ico-sm"), "Undo in Library"));
  }
  if (p.savableBytes) right.push(h("span.badge.badge-warn", `−${fmtBytes(p.savableBytes)}`));

  const faded = p.skipped || (!p.savableBytes && !p.alreadyOptimized);
  return h(`label.fw-row${faded ? ".other-edition" : ""}`, { style: { cursor: pick ? "pointer" : "default" } },
    box,
    h("div.fw-icon", ic("box")),
    h("div.grow", h("b.fw-name", p.name), ...lines),
    ...right);
}

function resultBox(r) {
  const saved = r.optimized.reduce((s, o) => s + o.savedBytes, 0);
  const count = r.optimized.reduce((s, o) => s + o.textures, 0);
  const box = h("div", { style: { margin: "12px 0" } });
  if (r.optimized.length)
    box.append(h("div.alert.info", ic("check"), h("div.alert-body",
      h("b", `Shrunk ${fmtNum(count)} texture${count === 1 ? "" : "s"} in ${r.optimized.length} pack${r.optimized.length === 1 ? "" : "s"}: ${fmtBytes(saved)} less video memory. `),
      "The pack files keep their size on disk. Each pack has a \"Smaller textures\" record in Library: uninstall it there to get the originals back.")));
  if (r.problems && r.problems.length)
    box.append(h("div.alert", ic("alert"), h("div.alert-body", h("b", "Some packs weren't changed. "), r.problems.join(" · "))));
  return box;
}

async function scan() {
  T.error = null;
  try {
    T.scan = await runWithProgress({
      title: "Reading your add-on packs",
      sub: "Big packs take a moment the first time; later scans are instant.",
      planId: "textures",
      call: () => host.call("tex.scan", { maxSize: T.maxSize }),
    });
    // keep only picks that are still possible at this size
    for (const id of [...T.selected]) {
      const p = T.scan.packs.find((x) => x.id === id);
      if (!p || !canPick(p)) T.selected.delete(id);
    }
  } catch (e) {
    T.error = e;
  }
  repaint();
}

async function optimize(chosen) {
  const ok = await confirmDialog({
    title: `Shrink textures in ${chosen.length} pack${chosen.length === 1 ? "" : "s"}?`,
    text: `Textures bigger than ${T.maxSize} px get their largest mip levels dropped. Close the game first. You can undo it any time: each pack gets a "Smaller textures" record in Library, and uninstalling it puts the original textures back.`,
    ok: "Shrink textures",
  });
  if (!ok) return;
  try {
    T.last = await runWithProgress({
      title: "Shrinking textures",
      planId: "textures",
      call: () => host.call("tex.optimize", { packs: chosen.map((p) => p.id), maxSize: T.maxSize }),
    });
    T.selected.clear();
    if (T.last.optimized.length) toast("Textures shrunk. Undo any time from Library.", "ok");
  } catch (e) {
    toastError(e);
  }
  await scan();
}
