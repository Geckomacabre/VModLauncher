// Install plan review → install → done. Plans that arrive while one is open wait in a queue.
import { host } from "./bridge.js";
import { h, ic, clear, openModal, toast, toastError } from "./ui.js";
import { refreshLibrary } from "./pages/library.js";
import { openGetUrl } from "./pages/home.js";
import { navigate } from "./router.js";
import { panelArt } from "./art.js";

const queue = [];
let cur = null; // { plan, phase: "review"|"installing"|"done", error, progress, installed, readme, showAll, modal, box }

const KIND = {
  CopyFile: { icon: "file", label: "File" },
  DlcPack: { icon: "package", label: "Add-on pack" },
  RpfFile: { icon: "archive", label: "Inside a game archive" },
  Oiv: { icon: "box", label: "OpenIV package" },
  SaveGame: { icon: "save", label: "Save game" },
  Skip: { icon: "minus", label: "Not installed" },
};
const OPS_SHOWN = 60;

export function enqueuePlan(plan) {
  if (!plan || !plan.planId) return;
  if (cur && cur.plan.planId === plan.planId) { cur.plan = plan; paint(); return; }
  const i = queue.findIndex((p) => p.planId === plan.planId);
  if (i >= 0) { queue[i] = plan; return; }
  queue.push(plan);
  if (!cur) showNext();
  else paint();
}

export function initPlans() {
  host.on("install.progress", (d) => {
    if (!cur || !d || d.planId !== cur.plan.planId) return;
    cur.progress = d;
    paintProgress();
  });
}

function showNext() {
  const plan = queue.shift();
  if (!plan) { cur = null; return; }
  const box = h("div", { style: { display: "flex", flexDirection: "column", minHeight: 0, flex: 1 } });
  cur = { plan, phase: "review", error: null, progress: null, installed: null, readme: null, showAll: false, box, placing: null, placeError: null, skippedOpen: false };
  cur.modal = openModal(box, { onClose: () => onClosed(cur) });
  paint();
}

function onClosed(entry) {
  if (!entry) return;
  if (entry.phase === "review") {
    host.call("plan.cancel", { planId: entry.plan.planId }).catch(() => {});
  }
  if (cur === entry) cur = null;
  setTimeout(() => { if (!cur) showNext(); }, 180);
}

function close() {
  if (cur && cur.modal) cur.modal.close();
}

// ---------------------------------------------------------------- painting

function paint() {
  if (!cur) return;
  const { plan, box } = cur;
  const oldBody = box.querySelector(".modal-body");
  const scroll = oldBody ? oldBody.scrollTop : 0;
  clear(box);
  box.append(head(plan));

  if (cur.phase === "installing" || cur.phase === "scanning") {
    box.append(installingView());
    return;
  }
  if (cur.phase === "done") {
    box.append(doneView());
    return;
  }

  const body = h("div.modal-body");
  box.append(body);
  body.append(...reviewSections(plan));
  if (cur.error) {
    box.append(h("div.alert.danger.inline-error", ic("alert"),
      h("div.alert-body", h("b", cur.error.code === "BLOCKED" ? "Can't do that yet. " : "That didn't work. "), cur.error.message)));
  }
  box.append(foot(plan));
  body.scrollTop = scroll;
}

function head(plan) {
  return h("div.modal-head",
    plan.image ? h("img.modal-head-img", { src: plan.image, alt: "", referrerpolicy: "no-referrer" }) : h("div.modal-head-img", { style: { display: "grid", placeItems: "center", color: "var(--dim)" } }, ic("package", "ico-lg")),
    h("div.modal-head-text",
      h("div.modal-kicker", `${plan.summary || "Mod"} · for ${plan.gameName || "GTA V"}`),
      h("div.modal-title", { title: plan.title || plan.modName }, plan.title || plan.modName),
      h("div.modal-sub", [plan.version ? `v${String(plan.version).replace(/^v/i, "")}` : null, plan.author ? `by ${plan.author}` : null, siteName(plan.site)].filter(Boolean).join(" · "))));
}

function siteName(site) {
  return site === "gta5mods" ? "GTA5-Mods" : site === "lcpdfr" ? "LCPDFR" : site === "local" || !site ? "From a file" : site;
}

function reviewSections(plan) {
  const out = [];

  if (plan.warnings && plan.warnings.length) {
    out.push(h("div.plan-section",
      h("div.warn-list", plan.warnings.map((w) => {
        const lvl = (w.level || "Info").toLowerCase();
        return h(`div.notice.lvl-${lvl}`, ic(lvl === "block" ? "block" : lvl === "warn" ? "alert" : "info"), h("div", w.message));
      }))));
  }

  if (plan.needsScan) {
    out.push(h("div.plan-section",
      h("div.alert.info", { style: { marginTop: 0 } }, ic("search"),
        h("div.alert-body", "Some files replace parts of the game. The launcher can scan your game once (about 30 s) to put them in the copy the game actually loads."),
        h("button.btn.btn-sm", { type: "button", onclick: locate }, ic("search", "ico-sm"), "Scan my game"))));
  }

  if (plan.requires && plan.requires.length) {
    out.push(h("div.plan-section",
      h("h4", "Needs"),
      h("div.req-list", plan.requires.map((r) => {
        const cls = r.state === "Ok" ? "ok" : r.state === "Wrong" ? "wrong" : "missing";
        return h(`span.req.${cls}`, { title: r.detail || "" },
          ic(r.state === "Ok" ? "check" : r.state === "Wrong" ? "alert" : "minus"),
          r.name,
          r.state !== "Ok" && r.getUrl ? h("button.btn.btn-sm", { type: "button", onclick: () => openGetUrl(r.getUrl) }, "Get") : null);
      })),
      plan.requires.some((r) => r.state !== "Ok")
        ? h("p.hint", { style: { margin: "8px 0 0" } }, ic("info", "ico-sm"), "You can install now and add the missing pieces after; the mod just won't load until they're there.")
        : null));
  }

  for (const g of plan.variants || []) {
    out.push(h("div.plan-section.variant",
      h("div.variant-title", g.title),
      h("div.segmented", { role: "radiogroup", "aria-label": g.title },
        g.options.map((o) => h(`button${o.id === g.selected ? ".active" : ""}`, {
          type: "button", role: "radio", "aria-checked": o.id === g.selected ? "true" : "false",
          onclick: () => o.id !== g.selected && selectVariant(g.id, o.id),
        }, o.label, o.recommended ? h("span.rec", "recommended") : null)))));
  }

  const active = plan.ops.filter((o) => o.active);
  const inactive = plan.ops.filter((o) => !o.active);

  const opsBox = h("div.ops");
  const shown = cur.showAll ? active : active.slice(0, OPS_SHOWN);
  for (const op of shown) opsBox.append(opRow(op, true));
  if (active.length > shown.length) {
    opsBox.append(h("div.ops-more", `${active.length - shown.length} more files · `,
      h("button", { type: "button", onclick: () => { cur.showAll = true; paint(); } }, "Show all")));
  }
  if (!active.length) opsBox.append(h("div.ops-more", "Nothing selected to install."));
  out.push(h("div.plan-section",
    h("h4", "Will install", h("span.count", `${plan.activeCount ?? active.length} item${(plan.activeCount ?? active.length) === 1 ? "" : "s"}`)),
    opsBox));

  if (inactive.length) {
    // files the player could place by hand come first, so they're not buried under readmes
    const placeable = (op) => op.kind === "Skip" && /^(Not recognised|Game file)/i.test(op.reason || "");
    const ordered = [...inactive.filter(placeable), ...inactive.filter((o) => !placeable(o))];
    const nPlace = ordered.filter(placeable).length;
    const sk = h("div.ops");
    for (const op of ordered.slice(0, 200)) sk.append(opRow(op, false));
    if (ordered.length > 200) sk.append(h("div.ops-more", `and ${ordered.length - 200} more`));
    const det = h("details.skipped",
      h("summary", ic("chevron", "ico-sm"), `Not installed (${inactive.length}) — readmes, pictures, other choices`,
        nPlace ? h("span.badge.badge-warn", { style: { marginLeft: "8px" } }, `${nPlace} need a place`) : null),
      sk);
    det.open = cur.skippedOpen || cur.placing != null;
    det.addEventListener("toggle", () => { cur.skippedOpen = det.open; });
    out.push(h("div.plan-section", det));
  }

  if (plan.readmes && plan.readmes.length) {
    out.push(readmeSection(plan));
  }
  return out;
}

function opRow(op, active) {
  const k = KIND[op.kind] || KIND.CopyFile;
  const toggleable = op.kind !== "Skip" && (active || !op.variant || !op.enabled);
  const cb = h("input", { type: "checkbox", "aria-label": `Install ${op.sourceRel}` });
  cb.checked = !!op.enabled && op.kind !== "Skip";
  cb.disabled = !toggleable || op.kind === "Skip";
  cb.addEventListener("change", () => toggleOp(op.index, cb.checked));

  const reason = !active && op.kind !== "Skip" && op.enabled && op.variant ? "Part of a choice you didn't pick" : op.reason;
  const path = op.kind === "Skip"
    ? h("div.op-path", { title: op.sourceRel }, op.sourceRel)
    : h("div.op-path", { title: `${op.sourceRel} → ${targetText(op)}` }, op.sourceRel, h("span.arrow", "→"), h("span.to", targetText(op)));

  if (op.kind === "Skip") {
    const placeable = /^(Not recognised|Game file)/i.test(op.reason || "");
    return h("div.op.skip", h(`div.op-kind.k-${op.kind}`, { title: k.label }, ic(k.icon)),
      h("div.op-text", path, h("div.op-reason", { title: reason }, reason), placeable ? placeControl(op) : null));
  }
  return h(`div.op${active ? "" : ".off"}`,
    cb,
    h(`div.op-kind.k-${op.kind}`, { title: k.label }, ic(k.icon)),
    h("div.op-text", path, h("div.op-reason", { title: reason }, reason)));
}

// ---------------------------------------------------------------- placing a file by hand (plan.setTarget)

const FOLDERS = [
  ["", "Game folder"],
  ["scripts", "scripts"],
  ["plugins", "plugins"],
  ["plugins\\LSPDFR", "plugins\\LSPDFR"],
  ["lspdfr\\audio\\scanner", "lspdfr\\audio\\scanner"],
  ["menyooStuff\\Spooner", "menyooStuff\\Spooner"],
  ["ELS\\pack_default", "ELS\\pack_default"],
  ["mods", "mods"],
  ["__custom", "Other folder…"],
];

function placeControl(op) {
  const open = cur.placing === op.index;
  if (!open) {
    return h("button.place-link", { type: "button", onclick: () => { cur.placing = op.index; cur.placeError = null; paint(); } },
      ic("target", "ico-sm"), "Choose where it goes");
  }
  const sel = h("select.select.select-sm", { "aria-label": "Folder in the game" },
    FOLDERS.map(([v, label]) => h("option", { value: v }, label)));
  const custom = h("input.input.input-sm.mono", { type: "text", placeholder: "e.g. scripts\\MyMod", "aria-label": "Folder in the game", hidden: true });
  sel.addEventListener("change", () => { custom.hidden = sel.value !== "__custom"; if (!custom.hidden) custom.focus(); });
  const folder = op.sourceRel.includes("/") ? op.sourceRel.slice(0, op.sourceRel.lastIndexOf("/")) : "";
  const whole = h("input", { type: "checkbox" });
  const place = h("button.btn.btn-sm.btn-primary", { type: "button" }, "Place");
  place.addEventListener("click", () => {
    const value = sel.value === "__custom" ? custom.value.trim().replace(/\//g, "\\").replace(/^\\+|\\+$/g, "") : sel.value;
    if (sel.value === "__custom" && !value) { custom.focus(); return; }
    setTarget(op.index, value, whole.checked);
  });
  return h("div.place",
    h("span.dim.small", "Put it in"),
    sel, custom,
    folder ? h("label.place-whole", whole, h("span", `also the rest of ${folder.split("/").pop()}/`)) : null,
    place,
    h("button.btn.btn-sm.btn-ghost", { type: "button", onclick: () => { cur.placing = null; cur.placeError = null; paint(); } }, "Cancel"),
    cur.placeError ? h("div.place-error", ic("alert", "ico-sm"), cur.placeError) : null);
}

async function setTarget(index, folder, wholeFolder) {
  const entry = cur;
  try {
    const plan = await host.call("plan.setTarget", { planId: entry.plan.planId, index, folder, wholeFolder });
    if (cur !== entry) return;
    cur.plan = plan;
    cur.placing = null;
    cur.placeError = null;
  } catch (e) {
    if (cur !== entry) return;
    cur.placeError = e.message || String(e);
  }
  paint();
}

function targetText(op) {
  switch (op.kind) {
    case "DlcPack": return `dlcpacks:/${op.target}/`;
    case "RpfFile": return op.target.replace("|", " › ");
    case "SaveGame": return `save slot ${op.target}`;
    case "Oiv": return "runs the package";
    default: return op.target;
  }
}

function readmeSection(plan) {
  const tabs = h("div.readme-tabs");
  const boxEl = h("div.readme-box", { tabindex: "0" }, cur.readme ? cur.readme.text : "Pick a file to read it here.");
  for (const rel of plan.readmes.slice(0, 12)) {
    const on = cur.readme && cur.readme.rel === rel;
    tabs.append(h(`button.chip${on ? ".active" : ""}`, { type: "button", onclick: () => loadReadme(rel), title: rel }, rel.split("/").pop()));
  }
  const det = h("details.skipped", h("summary", ic("chevron", "ico-sm"), `Readme${plan.readmes.length > 1 ? "s" : ""} (${plan.readmes.length})`),
    h("div", { style: { marginTop: "8px" } }, tabs, boxEl));
  if (cur.readme) det.open = true;
  det.addEventListener("toggle", () => { if (det.open && !cur.readme) loadReadme(plan.readmes[0]); });
  return h("div.plan-section", det);
}

function foot(plan) {
  const blocked = (plan.warnings || []).some((w) => w.level === "Block");
  const activeCount = plan.activeCount ?? plan.ops.filter((o) => o.active).length;
  const installBtn = h("button.btn.btn-primary", {
    type: "button", disabled: blocked || activeCount === 0 || null, onclick: install,
    title: blocked ? "This download can't be installed (see above)" : "",
  }, ic("download"), "INSTALL");
  return h("div.modal-foot",
    h("div.grow", queue.length ? h("span.queue-note", `${queue.length} more waiting after this one`) : `Everything it changes can be undone from Library.`),
    h("button.btn.btn-ghost", { type: "button", onclick: close }, "Cancel"),
    installBtn);
}

function installingView() {
  const p = cur.progress;
  const scanning = cur.phase === "scanning";
  // the scan reports which archive it's on, not how far it is: always an indeterminate bar
  const frac = !scanning && p && p.fraction != null ? Math.max(0, Math.min(1, p.fraction)) : null;
  return h("div.install-progress#install-progress",
    h("div.stage", p && p.stage ? p.stage : scanning ? "Scanning your game's archives…" : "Installing…"),
    h("div.dim", scanning ? "This happens once per game; replace mods after this are placed straight away." : "Don't start the game until this finishes."),
    h(`div.progress${frac == null ? ".indeterminate" : ""}`, h("div", { style: { width: frac == null ? "" : `${(frac * 100).toFixed(1)}%` } })),
    h("div.detail", p && p.detail ? p.detail : ""));
}

function paintProgress() {
  if (!cur || (cur.phase !== "installing" && cur.phase !== "scanning")) return;
  const old = cur.box.querySelector("#install-progress");
  if (old) old.replaceWith(installingView());
}

function doneView() {
  const m = cur.installed || {};
  const wrap = h("div",
    h("div.done-state",
      panelArt(`plan-done-${cur.plan.planId}`, "done-art"),
      h("div.done-check", ic("check")),
      h("div.done-big", "Mod installed"),
      h("h3", `${m.name || cur.plan.title || cur.plan.modName} is installed`),
      h("p", `${m.changeCount != null ? m.changeCount + " changes made to " : "Installed into "}${cur.plan.gameName || "the game"}. Turn it off or remove it any time from Library.`)),
    h("div.modal-foot",
      h("div.grow", queue.length ? h("span.queue-note", `${queue.length} more waiting`) : ""),
      h("button.btn.btn-ghost", { type: "button", onclick: () => { close(); navigate("library"); } }, ic("library"), "Library"),
      h("button.btn", { type: "button", onclick: playNow }, ic("play"), "Play now"),
      h("button.btn.btn-primary", { type: "button", onclick: close }, "Done")));
  return wrap;
}

// ---------------------------------------------------------------- actions

async function selectVariant(groupId, optionId) {
  const entry = cur;
  try {
    const plan = await host.call("plan.select", { planId: entry.plan.planId, groupId, optionId });
    if (cur !== entry) return;
    cur.plan = plan;
    paint();
  } catch (e) {
    toastError(e);
  }
}

async function toggleOp(index, enabled) {
  const entry = cur;
  try {
    const plan = await host.call("plan.toggleOp", { planId: entry.plan.planId, index, enabled });
    if (cur !== entry) return;
    cur.plan = plan;
    paint();
  } catch (e) {
    toastError(e);
    paint();
  }
}

async function loadReadme(rel) {
  const entry = cur;
  if (!rel) return;
  try {
    const r = await host.call("plan.readme", { planId: entry.plan.planId, rel });
    if (cur !== entry) return;
    cur.readme = { rel, text: r.text || "(empty file)" };
  } catch (e) {
    if (cur !== entry) return;
    cur.readme = { rel, text: `Couldn't read ${rel}: ${e.message}` };
  }
  paint();
}

async function locate() {
  const entry = cur;
  entry.phase = "scanning";
  entry.error = null;
  entry.progress = null;
  entry.modal.setDismissable(false);
  paint();
  try {
    entry.plan = await host.call("plan.locate", { planId: entry.plan.planId });
  } catch (e) {
    entry.error = e;
  }
  entry.phase = "review";
  entry.modal.setDismissable(true);
  if (cur === entry) paint();
}

async function install() {
  const entry = cur;
  entry.phase = "installing";
  entry.error = null;
  entry.progress = null;
  entry.modal.setDismissable(false);
  paint();
  try {
    entry.installed = await host.call("plan.install", { planId: entry.plan.planId });
    entry.phase = "done";
    refreshLibrary();
    toast(`${entry.installed && entry.installed.name ? entry.installed.name : "Mod"} installed.`, "ok");
  } catch (e) {
    entry.phase = "review";
    entry.error = e;
  }
  entry.modal.setDismissable(true);
  if (cur === entry) paint();
}

async function playNow() {
  close();
  try {
    await host.call("game.play", { mode: "story" });
    toast("Starting GTA V…", "ok");
  } catch (e) {
    toastError(e);
  }
}

export function planCount() {
  return queue.length + (cur ? 1 : 0);
}
