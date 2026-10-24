// App-wide state plus a tiny change feed. Pages re-render from here.

export const state = {
  version: "",
  games: [],
  selectedGameId: null,
  /** GameStatus of the selected game (bridge.md) */
  status: null,
  /** Health of the selected game (bridge.md wave 2), or null until loaded */
  health: null,
  settings: { keepDownloads: true, confirmUninstall: true, defaultSite: "gta5mods", accent: "green" },
  page: "home",
  library: [],
  libraryLoaded: false,
  /** url -> ModSummary the player starred */
  favorites: new Map(),
  lockedEdition: null,
};

const subs = new Set();

export function subscribe(fn) {
  subs.add(fn);
  return () => subs.delete(fn);
}

/** what: "games" | "status" | "health" | "settings" | "library" | "page" | "favorites" */
export function changed(what) {
  for (const fn of [...subs]) {
    try { fn(what); } catch (e) { console.error(e); }
  }
}

export function selectedGame() {
  return state.games.find((g) => g.id === state.selectedGameId) || null;
}

/** "Legacy" or "Enhanced" for the selected game (or the build's edition when there's no game yet). */
export function currentEdition() {
  const g = selectedGame();
  if (g) return g.edition || (g.name.includes("Enhanced") ? "Enhanced" : "Legacy");
  return state.lockedEdition || "Legacy";
}

export const SITES = {
  gta5mods: { id: "gta5mods", name: "GTA5-Mods", home: "https://www.gta5-mods.com/" },
  lcpdfr: { id: "lcpdfr", name: "LCPDFR", home: "https://www.lcpdfr.com/downloads/gta5mods/" },
};
