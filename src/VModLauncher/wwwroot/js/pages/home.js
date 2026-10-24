import { host } from "../bridge.js";
import { h, ic, clear, toast, toastError, isSiteUrl, infoDialog } from "../ui.js";
import { state, changed, selectedGame } from "../state.js";
import { addGameFolder, rescanGames } from "../games.js";
import { navigate } from "../router.js";
import { mountHero, panelArt } from "../art.js";
import { runWithProgress } from "../progress.js";
import { openTools } from "./tools.js";

let healthLoading = false;
let healthError = null;
let healthToken = 0;

/** Reads game.health for the selected game (cheap; called on start, game switch and library changes). */
export async function loadHealth() {
  if (!state.selectedGameId) { state.health = null; changed("health"); return; }
  const my = ++healthToken;
  const gameId = state.selectedGameId;
  healthLoading = true;
  healthError = null;
  try {
    const hlth = await host.call("game.health", {});
    if (my !== healthToken || gameId !== state.selectedGameId) return;
    state.health = hlth;
  } catch (e) {
    if (my !== healthToken) return;
    healthError = e;
  }
  healthLoading = false;
  changed("health");
}

export function renderHome(root) {
  clear(root);
  const page = h("div.page");
  root.append(page);

  const game = selectedGame();
  if (!game) {
    page.append(h("div.empty",
      panelArt("home-empty", "empty-art"),
      ic("car"),
      h("h3", "No GTA V found"),
      h("p", "The launcher looks for Steam, Epic and Rockstar Games Launcher installs. If yours lives somewhere else, point the launcher at the folder that has GTA5.exe or GTA5_Enhanced.exe."),
      h("div.row",
        h("button.btn.btn-primary", { type: "button", onclick: addGameFolder }, ic("folder"), "Add game folder…"),
        h("button.btn", { type: "button", onclick: rescanGames }, ic("refresh"), "Scan again"))));
    return;
  }

  const st = state.status && state.status.id === game.id ? state.status : null;
  page.append(hero(game, st));

  if (st) {
    if (st.running) {
      page.append(h("div.alert", ic("alert"),
        h("div.alert-body", h("b", "GTA V is running. "), "Installs, uninstalls and the mods switch wait until the game is closed, because its files are locked while it plays.")));
    }
    if (!st.writable) {
      page.append(h("div.alert.danger", ic("lock"),
        h("div.alert-body", h("b", "Windows won't let the launcher write to the game folder. "),
          "This happens with games in Program Files. Fix it once: Windows asks for administrator rights and gives your account write access to this folder only."),
        h("button.btn.btn-sm", { type: "button", onclick: fixAccess }, ic("shield", "ico-sm"), "Fix folder access")));
    }
    if (!st.modsEnabled) {
      page.append(h("div.alert.info", ic("power"),
        h("div.alert-body", h("b", "Mods are OFF. "), "The game starts clean, which is what you want for GTA Online. Turn mods back on before playing story mode with your mods.")));
    }
  }
  const hlth = state.health;
  if (hlth && hlth.modsUpdateStale) {
    page.append(h("div.alert.danger.alert-big", ic("alert"),
      h("div.alert-body", h("b", "GTA V was updated. "),
        hlth.staleReason || "Your mods folder still holds the old update.rpf, so add-on packs and edits won't load (or the game crashes) until it's rebuilt."),
      h("button.btn.btn-primary", { type: "button", onclick: repair }, ic("wrench"), "Repair")));
  }

  const grid = h("div.home-grid");
  page.append(grid);

  // left: health + requirements
  const left = h("div.home-col");
  grid.append(left);
  left.append(healthCard(st));

  const reqCard = h("section.card", { "aria-labelledby": "req-title" },
    h("h2.card-title#req-title", "Mod frameworks", st ? h("span.card-aside", summaryLine(st)) : null),
    h("p.card-sub", "What mods need to load. Most mods need Script Hook V; .NET scripts need Script Hook V .NET; add-on cars and replace mods need the mods folder loader."));
  if (!st) {
    reqCard.append(h("div.row.muted", h("div.spinner"), "Checking the game folder…"));
  } else {
    reqCard.append(h("div.prereqs", st.prereqs.map(prereqCard)));
    reqCard.append(h("div.row", { style: { marginTop: "12px" } },
      h("button.btn.btn-sm", { type: "button", onclick: () => openTools("frameworks") }, ic("download", "ico-sm"), "Install or update frameworks")));
  }
  left.append(reqCard);

  // right: mods switch + quick actions
  const side = h("div.home-col");
  grid.append(side);
  side.append(modsCard(st));
  side.append(h("section.card",
    h("h2.card-title", "Quick actions"),
    h("div.quick",
      h("button.btn", { type: "button", onclick: importFile }, ic("download"), "Install from file…"),
      h("button.btn", { type: "button", onclick: () => navigate("browse") }, ic("grid"), "Browse mods"),
      h("button.btn", { type: "button", onclick: () => navigate("library") }, ic("library"), `Installed mods${st ? ` (${st.installedCount})` : ""}`),
      h("button.btn", { type: "button", onclick: () => navigate("profiles") }, ic("layers"), "Mod profiles"),
      h("button.btn", { type: "button", onclick: () => openTools("launch") }, ic("sliders"), "Launch options"),
      h("button.btn", { type: "button", onclick: () => openTools("diagnostics") }, ic("pulse"), "Diagnostics & logs"),
      h("button.btn", { type: "button", onclick: () => host.call("game.openFolder", {}).catch(toastError) }, ic("folder"), "Open game folder")),
    h("p.hint", { style: { marginTop: "14px", marginBottom: 0 } }, ic("info", "ico-sm"), "Tip: drop a .zip, .rar, .7z or .oiv anywhere on this window to install it.")));
}

function summaryLine(st) {
  const ok = st.prereqs.filter((p) => p.state === "Ok").length;
  return `${ok} of ${st.prereqs.length} installed`;
}

function hero(game, st) {
  const edition = game.edition || (game.name.includes("Enhanced") ? "Enhanced" : "Legacy");
  const running = st && st.running;
  const playBtn = h("button.btn.btn-primary.btn-lg.btn-play", {
    type: "button", disabled: running || null,
    onclick: () => play("story"),
  }, ic("play"), running ? "RUNNING" : "PLAY");

  const actions = h("div.hero-actions", playBtn);
  if (st && st.canLspdfr) {
    actions.append(h("button.btn.btn-lspdfr", { type: "button", disabled: running || null, onclick: () => play("lspdfr"), title: "Starts RAGE Plugin Hook, which starts the game with LSPDFR" },
      ic("siren"), "PLAY LSPDFR"));
  }

  const stats = h("div.hero-stats");
  if (st) {
    stats.append(h("div.stat-pill", h("b.num", String(st.installedCount)), "mods installed"));
    stats.append(h("div.stat-pill", ic(st.modsEnabled ? "check" : "power", "ico-sm"), st.modsEnabled ? "Mods on" : "Mods off"));
  }

  const art = h("div.hero-art");
  mountHero(art);
  return h("section.hero", { "aria-label": game.name },
    art,
    stats,
    h("div.hero-kicker",
      h(`span.game-switch-dot.${edition.toLowerCase()}`),
      `${game.store || "Custom"} · ${edition}`),
    h("h1.hero-title", "GTA V ", h("span.accent", edition)),
    h("div.hero-path", { title: game.root }, game.root),
    actions);
}

// ---------------------------------------------------------------- game health

function healthCard(st) {
  const hlth = state.health;
  const card = h("section.card.health", { "aria-labelledby": "health-title" },
    h("h2.card-title#health-title", "Game health",
      h("span.card-aside", hlth && hlth.gameVersion ? `Game version ${hlth.gameVersion}` : "",
        h("button.btn.btn-sm.btn-ghost.btn-icon", { type: "button", title: "Check again", "aria-label": "Check game health again", onclick: () => loadHealth() }, ic("refresh", "ico-sm")))));

  if (!hlth) {
    card.append(healthLoading || !healthError
      ? h("div.row.muted", h("div.spinner"), "Checking the game…")
      : h("div.row.muted", ic("alert", "ico-sm"), `Couldn't check: ${healthError.message}`));
    return card;
  }

  const rows = h("div.health-rows");
  card.append(rows);

  // Script Hook V vs the game version
  const shv = hlth.scriptHookV || {};
  if (!shv.installed) {
    rows.append(healthRow("missing", "Script Hook V", "Not installed. Most mods need it.",
      h("button.btn.btn-sm", { type: "button", onclick: () => openGetUrl("http://www.dev-c.com/gtav/scripthookv/") }, ic("external", "ico-sm"), "dev-c.com")));
  } else if (shv.supportsGame === false) {
    rows.append(healthRow("bad", "Script Hook V",
      shv.message || `${shv.version ? `Version ${shv.version}` : "Your Script Hook V"} doesn't support this game version yet. Mods that need it won't load until a new Script Hook V is out.`,
      h("button.btn.btn-sm", { type: "button", onclick: () => openGetUrl("http://www.dev-c.com/gtav/scripthookv/") }, ic("external", "ico-sm"), "Check dev-c.com")));
  } else {
    rows.append(healthRow("ok", "Script Hook V", shv.message
      || (shv.version ? `Version ${shv.version}${shv.supportsGame ? " supports this game version" : ""}.` : "Installed. Start the game once and the launcher can check its version against yours.")));
  }

  // mods folder after a Rockstar update
  rows.append(hlth.modsUpdateStale
    ? healthRow("bad", "Mods folder", hlth.staleReason || "Made for an older game version. Rebuild it so your add-ons load.",
        h("button.btn.btn-sm.btn-primary", { type: "button", onclick: repair }, ic("wrench", "ico-sm"), "Repair"))
    : healthRow("ok", "Mods folder", "Matches your game version."));

  // add-on packs
  rows.append(healthRow(hlth.packLimitWarning ? "warn" : "ok", "Add-on packs",
    h("span", h("b.num", String(hlth.addonPacks ?? 0)), ` add-on pack${hlth.addonPacks === 1 ? "" : "s"} registered.`, hlth.packLimitWarning ? ` ${hlth.packLimitWarning}` : "")));

  for (const p of hlth.problems || []) {
    const lvl = p.level === "Block" ? "bad" : p.level === "Warn" ? "warn" : "info";
    rows.append(healthRow(lvl, null, p.text, p.fix ? fixButton(p.fix) : null));
  }
  return card;
}

function healthRow(kind, title, text, action) {
  const iconName = kind === "ok" ? "check" : kind === "bad" ? "alert" : kind === "warn" ? "alert" : kind === "missing" ? "minus" : "info";
  return h(`div.health-row.${kind}`,
    h("div.health-icon", ic(iconName)),
    h("div.health-text", title ? h("b", title) : null, h("span", text)),
    action || null);
}

const FIX_LABEL = { "game.repair": "Repair", "game.fixAccess": "Fix access", "game.setModsEnabled": "Turn mods on" };

function fixButton(fix) {
  let label = FIX_LABEL[fix] || "Fix";
  if (/^https?:/i.test(fix)) label = "Open";
  else if (fix.startsWith("frameworks.install:")) label = "Install";
  else if (fix.startsWith("nav:")) label = "Show me";
  return h("button.btn.btn-sm", { type: "button", onclick: () => runFix(fix) }, ic("wrench", "ico-sm"), label);
}

async function runFix(fix) {
  if (fix === "game.repair") return repair();
  if (fix === "game.fixAccess") return fixAccess();
  if (/^https?:/i.test(fix)) return openGetUrl(fix);
  if (fix.startsWith("nav:")) {
    const [, page, tab] = fix.split(":");
    if (page === "tools") openTools(tab || "diagnostics"); else navigate(page);
    return;
  }
  try {
    if (fix.startsWith("frameworks.install:")) {
      await host.call("frameworks.install", { id: fix.split(":")[1] });
      toast("Downloading from the official release. The install plan opens when it's done.", "info", 6000);
      return;
    }
    if (fix === "game.setModsEnabled") {
      state.status = await host.call("game.setModsEnabled", { enabled: true });
      changed("status");
    } else {
      await host.call(fix, {});
    }
    toast("Done.", "ok", 2000);
  } catch (e) {
    toastError(e);
  }
  loadHealth();
}

export async function repair() {
  try {
    const r = await runWithProgress({
      title: "Repairing the mods folder",
      sub: "Copying the game's new update.rpf into mods and putting your add-ons back. This takes a minute.",
      planId: "repair",
      call: () => host.call("game.repair", {}),
    });
    if (r && r.health) { state.health = r.health; changed("health"); }
    const lines = [];
    if (r && r.rebuilt) lines.push(h("p", `Rebuilt mods\\update\\update.rpf and put back ${r.restoredItems} add-on pack entr${r.restoredItems === 1 ? "y" : "ies"} and ${r.restoredEntries} edited file${r.restoredEntries === 1 ? "" : "s"}.`));
    else lines.push(h("p", "Nothing needed rebuilding: the mods folder already matches the game."));
    if (r && r.backup) lines.push(h("p.dim", "The old copy is kept as a backup: ", h("code.mono", r.backup)));
    if (r && r.problems && r.problems.length) lines.push(h("div.notice.lvl-warn", ic("alert"), h("div", `${r.problems.length} thing(s) couldn't be put back: ${r.problems.slice(0, 4).join("; ")}`)));
    infoDialog(r && r.rebuilt ? "Mods folder repaired" : "All good", ...lines);
  } catch (e) {
    toastError(e, "Repair didn't finish");
    loadHealth();
  }
}

// ---------------------------------------------------------------- frameworks

function prereqCard(p) {
  const cls = p.state === "Ok" ? "ok" : p.state === "Wrong" ? "wrong" : "missing";
  const iconName = p.state === "Ok" ? "check" : p.state === "Wrong" ? "alert" : "minus";
  return h(`div.prereq.${cls}`,
    h("div.prereq-icon", ic(iconName)),
    h("div.prereq-body",
      h("div.prereq-name", p.name),
      h("div.prereq-detail", p.detail),
      p.state !== "Ok" && p.getUrl
        ? h("button.btn.btn-sm", { type: "button", onclick: () => openGetUrl(p.getUrl) }, ic("external", "ico-sm"), p.state === "Wrong" ? "Get the right one" : "Get it")
        : null));
}

export function openGetUrl(url) {
  const cmd = isSiteUrl(url) ? "site.open" : "shell.openUrl";
  host.call(cmd, { url }).catch(toastError);
}

function modsCard(st) {
  const input = h("input", { type: "checkbox", role: "switch", "aria-label": "Mods on or off" });
  input.checked = st ? st.modsEnabled : true;
  input.disabled = !st || st.running;
  input.addEventListener("change", async () => {
    const want = input.checked;
    input.disabled = true;
    try {
      state.status = await host.call("game.setModsEnabled", { enabled: want });
      changed("status");
      toast(want ? "Mods are on. Story mode will load them." : "Mods are off. The game will start clean.", want ? "ok" : "info");
    } catch (e) {
      input.checked = !want;
      input.disabled = false;
      toastError(e);
    }
  });
  const on = st ? st.modsEnabled : true;
  return h("section.card",
    h("div.mods-toggle",
      h("div.mods-toggle-text",
        h("b", "Mods ", h(`span.mods-state.${on ? "on" : "off"}`, on ? "ON" : "OFF")),
        h("span", "OFF parks the ASI loader and the mods folder, so GTA Online starts clean. ON puts them back.")),
      h("label.switch", input, h("span.switch-track"))));
}

async function play(mode) {
  try {
    await host.call("game.play", { mode });
    toast(mode === "lspdfr" ? "Starting RAGE Plugin Hook…" : "Starting GTA V…", "ok");
  } catch (e) {
    toastError(e);
  }
}

async function fixAccess() {
  try {
    state.status = await host.call("game.fixAccess", {});
    changed("status");
    toast(state.status.writable ? "The game folder is writable now." : "The folder still isn't writable. Did Windows' prompt get a No?", state.status.writable ? "ok" : "warn");
  } catch (e) {
    toastError(e);
  }
  loadHealth();
}

export async function importFile() {
  try {
    await host.call("mod.importFile", {});
  } catch (e) {
    toastError(e);
  }
}
