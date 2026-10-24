// The game switcher in the sidebar, and adding / rescanning game folders.
import { host } from "./bridge.js";
import { h, ic, clear, toast, toastError } from "./ui.js";
import { state, changed, selectedGame } from "./state.js";

export async function selectGame(id) {
  try {
    const st = await host.call("games.select", { id });
    state.selectedGameId = id;
    state.status = st;
    state.library = [];
    state.libraryLoaded = false;
    changed("games");
    changed("status");
    changed("library");
  } catch (e) {
    toastError(e, "Couldn't switch game");
  }
}

export async function addGameFolder() {
  try {
    const before = new Set(state.games.map((g) => g.id));
    state.games = await host.call("games.addFolder", {});
    const added = state.games.find((g) => !before.has(g.id));
    if (added) {
      toast(`Added ${added.name} (${added.root})`, "ok");
      await selectGame(added.id);
    } else {
      changed("games");
    }
  } catch (e) {
    toastError(e);
  }
}

export async function rescanGames() {
  try {
    state.games = await host.call("games.refresh", {});
    if (!state.games.some((g) => g.id === state.selectedGameId)) {
      if (state.games.length) await selectGame(state.games[0].id);
      else { state.selectedGameId = null; state.status = null; changed("status"); }
    }
    changed("games");
    toast(`Found ${state.games.length} GTA V install${state.games.length === 1 ? "" : "s"}.`, "info");
  } catch (e) {
    toastError(e);
  }
}

// ---------------------------------------------------------------- sidebar switcher

const btn = () => document.getElementById("game-switch");
const menu = () => document.getElementById("game-menu");

export function renderGameSwitch() {
  const g = selectedGame();
  const dot = document.getElementById("game-switch-dot");
  dot.className = "game-switch-dot" + (g ? " " + (g.edition || "").toLowerCase() : "");
  document.getElementById("game-switch-name").textContent = g ? g.name : "No game";
  document.getElementById("game-switch-store").textContent = g ? `${g.store || "Custom"} · ${shortPath(g.root)}` : "Add a game folder";
  btn().title = g ? `${g.name}\n${g.root}` : "Choose a game";
}

function shortPath(p) {
  if (!p) return "";
  return p.length > 26 ? "…" + p.slice(-25) : p;
}

function openMenu() {
  const m = menu();
  clear(m);
  for (const g of state.games) {
    const current = g.id === state.selectedGameId;
    m.append(h(`button.game-menu-item${current ? ".current" : ""}`, {
      type: "button", role: "option", "aria-selected": current ? "true" : "false",
      onclick: () => { closeMenu(); if (!current) selectGame(g.id); },
    },
      h(`span.game-switch-dot.${(g.edition || "").toLowerCase()}`),
      h("span", { style: { flex: 1, minWidth: 0 } }, g.name, h("small", `${g.store || "Custom"} · ${g.root}`)),
      current ? ic("check", "ico-sm") : null));
  }
  if (state.games.length) m.append(h("div.game-menu-sep"));
  m.append(h("button.game-menu-item", { type: "button", onclick: () => { closeMenu(); addGameFolder(); } }, ic("plus", "ico-sm"), "Add game folder…"));
  m.append(h("button.game-menu-item", { type: "button", onclick: () => { closeMenu(); rescanGames(); } }, ic("refresh", "ico-sm"), "Scan for games"));
  m.hidden = false;
  btn().setAttribute("aria-expanded", "true");
  const first = m.querySelector("button");
  if (first) first.focus();
}

export function closeMenu() {
  menu().hidden = true;
  btn().setAttribute("aria-expanded", "false");
}

export function initGameSwitch() {
  btn().addEventListener("click", (e) => {
    e.stopPropagation();
    if (menu().hidden) openMenu(); else closeMenu();
  });
  document.addEventListener("click", (e) => {
    if (!menu().hidden && !menu().contains(e.target)) closeMenu();
  });
}
