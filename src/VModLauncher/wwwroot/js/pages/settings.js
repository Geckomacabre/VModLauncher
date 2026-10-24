import { host } from "../bridge.js";
import { h, ic, clear, toast, toastError } from "../ui.js";
import { state, changed, SITES } from "../state.js";
import { addGameFolder, rescanGames, selectGame } from "../games.js";
import { launchOptionsCard } from "../launchopts.js";
import { storageCard } from "./tools.js";
import { USEFUL_SITES } from "../collections.js";

export function renderSettings(root) {
  clear(root);
  const s = state.settings;

  const prefs = h("section.card",
    h("h2.card-title", "Preferences"),
    toggleRow("keepDownloads", "Keep downloaded archives",
      "Lets you switch a mod off and back on, or reinstall it, without downloading it again. Uses disk space."),
    toggleRow("confirmUninstall", "Ask before uninstalling", "Shows a confirmation before a mod is removed."),
    h("div.setting",
      h("div.setting-text", h("b", "Open Browse on"), h("span", "The site the Browse page starts with.")),
      siteSelect(s.defaultSite)));

  const games = h("section.card",
    h("h2.card-title", "Games",
      h("div.row",
        h("button.btn.btn-sm", { type: "button", onclick: rescanGames }, ic("refresh", "ico-sm"), "Rescan"),
        h("button.btn.btn-sm", { type: "button", onclick: addGameFolder }, ic("plus", "ico-sm"), "Add game folder…"))),
    h("p.card-sub", "Found from Steam, Epic and the Rockstar Games Launcher, plus any folder you add. The selected one is where installs go."),
    state.games.length
      ? h("div.game-rows", state.games.map((g) => {
          const current = g.id === state.selectedGameId;
          return h(`div.game-row${current ? ".current" : ""}`,
            h(`span.game-switch-dot.${(g.edition || "").toLowerCase()}`),
            h("div.game-row-text", h("b", g.name, h("span.dim", { style: { fontWeight: 500, marginLeft: "8px" } }, g.store || "Custom")), h("code", { title: g.root }, g.root)),
            current
              ? [h("span.badge.badge-installed", "Selected"),
                 h("button.btn.btn-sm.btn-ghost.btn-icon", { type: "button", title: "Open folder", "aria-label": `Open ${g.name} folder`, onclick: openFolder }, ic("folder", "ico-sm"))]
              : h("button.btn.btn-sm", { type: "button", onclick: () => selectGame(g.id) }, "Select"));
        }))
      : h("p.muted", "No GTA V installs found yet."));

  const about = h("section.card",
    h("h2.card-title", "About"),
    h("div.about",
      h("p", h("b", "V Mod Launcher"), state.version ? ` ${state.version}` : "", " — browse, install and remove GTA V single-player mods."),
      h("p", "The launcher keeps its downloads, backups and mod list in its own data folder, never inside the game. Archive edits go into copies under the game's mods folder, so the game's own files stay untouched and every change can be undone."),
      h("p", "Downloads happen on the sites' own pages in a window you see. If a site asks you to log in, you type your password into the site, not into the launcher."),
      h("p", "Uses ", h("b", "CodeWalker.Core"), " by dexyfex (MIT) to read and write RPF archives, and ", h("b", "SharpCompress"), " to unpack archives."),
      h("p.dim", "Mods listed here come from GTA5-Mods.com and LCPDFR.com and belong to their authors. Not affiliated with Rockstar Games, Take-Two, GTA5-Mods.com or LCPDFR.com. Mods are for single player: switch them off before playing GTA Online.")));

  const sites = h("section.card",
    h("h2.card-title", "Other useful sites"),
    h("p.card-sub", "Official pages for the frameworks and tools mods depend on. They open in your own browser."),
    h("div.site-links", USEFUL_SITES.map((s) => h("button.site-link", { type: "button", title: s.url, onclick: () => host.call("shell.openUrl", { url: s.url }).catch(toastError) },
      ic("link", "ico-sm"), h("span.grow", h("b", s.name), h("span", s.note)), ic("external", "ico-sm")))));

  root.append(h("div.page",
    h("div.page-head", h("div", h("h1.page-title", "Settings"))),
    h("div.settings", prefs, games, state.selectedGameId ? launchOptionsCard() : null, state.selectedGameId ? storageCard() : null, sites, about)));
}

function toggleRow(key, title, text) {
  const input = h("input", { type: "checkbox", role: "switch", "aria-label": title });
  input.checked = !!state.settings[key];
  input.addEventListener("change", () => save({ [key]: input.checked }, () => { input.checked = !input.checked; }));
  return h("div.setting",
    h("div.setting-text", h("b", title), h("span", text)),
    h("label.switch", input, h("span.switch-track")));
}

function siteSelect(value) {
  const sel = h("select.select", { "aria-label": "Default site" },
    Object.values(SITES).map((s) => {
      const o = h("option", { value: s.id }, s.name);
      if (s.id === value) o.selected = true;
      return o;
    }));
  sel.addEventListener("change", () => save({ defaultSite: sel.value }));
  return sel;
}

async function save(patch, revert) {
  try {
    state.settings = await host.call("settings.set", patch);
    changed("settings");
    toast("Saved.", "ok", 1800);
  } catch (e) {
    if (revert) revert();
    toastError(e);
  }
}

function openFolder() {
  host.call("game.openFolder", {}).catch(toastError);
}
