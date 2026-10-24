// Entry point: wires the bridge events, the pages and the global UI bits together.
import { host } from "./bridge.js";
import { hydrateIcons } from "./icons.js";
import { toast, toastError, topModalDismiss } from "./ui.js";
import { state, subscribe, changed } from "./state.js";
import { registerPages, navigate, render } from "./router.js";
import { renderHome, loadHealth } from "./pages/home.js";
import { renderBrowse, forgetBrowseCards, loadFavorites } from "./pages/browse.js";
import { renderLibrary, loadLibrary, refreshLibrary } from "./pages/library.js";
import { renderProfiles } from "./pages/profiles.js";
import { renderTools } from "./pages/tools.js";
import { renderSettings } from "./pages/settings.js";
import { initGameSwitch, renderGameSwitch, closeMenu } from "./games.js";
import { initTray } from "./tray.js";
import { initPlans, enqueuePlan } from "./plan.js";
import { isDetailOpen, closeDetail, lightboxKey } from "./detail.js";
import { initArt } from "./art.js";

registerPages({
  home: renderHome, browse: renderBrowse, library: renderLibrary,
  profiles: renderProfiles, tools: renderTools, settings: renderSettings,
});

function wireEvents() {
  host.on("plan.ready", (plan) => enqueuePlan(plan));
  host.on("plan.failed", (d) => toast(`Couldn't get ${d && d.title ? d.title : "the download"} ready: ${d && d.error ? d.error : "unknown error"}`, "error"));
  host.on("game.changed", (st) => {
    if (!st) return;
    if (!state.selectedGameId || st.id === state.selectedGameId) {
      state.status = st;
      changed("status");
    }
  });
  host.on("library.changed", (d) => {
    if (!d || !d.gameId || d.gameId === state.selectedGameId) {
      refreshLibrary();
      loadHealth();
    }
  });
  host.on("toast", (d) => d && toast(d.text, d.kind || "info"));
}

function onStateChange(what) {
  if (what === "games" || what === "status") renderGameSwitch();
  if (what === "library") forgetBrowseCards();
  if (what === "games") { state.health = null; loadHealth(); }
  updateNavCount();
  updateNavAlert();
  const page = state.page;
  if ((what === "status" || what === "games" || what === "health") && (page === "home" || page === "settings")) render();
  else if (what === "library" && (page === "library" || page === "home" || page === "profiles")) render();
  else if (what === "games" && (page === "library" || page === "profiles" || page === "tools")) render();
  else if (what === "favorites" && page === "browse") render();
}

function updateNavCount() {
  const el = document.getElementById("nav-count");
  const n = state.libraryLoaded ? state.library.length : state.status ? state.status.installedCount : 0;
  el.hidden = !n;
  el.textContent = n > 99 ? "99+" : String(n);
}

/** A dot on Tools when the game needs attention (repair after an update, Script Hook V too old, …). */
function updateNavAlert() {
  const el = document.getElementById("nav-alert");
  const hlth = state.health;
  const bad = !!hlth && (hlth.modsUpdateStale || (hlth.scriptHookV && hlth.scriptHookV.supportsGame === false)
    || (hlth.problems || []).some((p) => p.level === "Block" || p.level === "Warn"));
  el.hidden = !bad;
  el.title = bad ? "Your game needs attention (see Home)" : "";
}

// ---------------------------------------------------------------- drag & drop

function hasFiles(e) {
  return e.dataTransfer && [...(e.dataTransfer.types || [])].includes("Files");
}

function setupDragDrop() {
  const overlay = document.getElementById("drop-overlay");
  let depth = 0;
  window.addEventListener("dragenter", (e) => {
    if (!hasFiles(e)) return;
    e.preventDefault();
    depth++;
    overlay.hidden = false;
  });
  window.addEventListener("dragover", (e) => {
    if (!hasFiles(e)) return;
    e.preventDefault(); // without this WebView2 would navigate to the file
    e.dataTransfer.dropEffect = "copy";
  });
  window.addEventListener("dragleave", (e) => {
    if (!hasFiles(e)) return;
    depth = Math.max(0, depth - 1);
    if (!depth) overlay.hidden = true;
  });
  window.addEventListener("drop", (e) => {
    e.preventDefault();
    depth = 0;
    overlay.hidden = true;
    const files = e.dataTransfer ? e.dataTransfer.files : null;
    if (!files || !files.length) return;
    if (!state.selectedGameId) { toast("Pick a GTA V install first (Home page).", "warn"); return; }
    toast(files.length === 1 ? `Reading ${files[0].name}…` : `Reading ${files.length} files…`, "info", 3000);
    host.callWithFiles("mod.importDropped", {}, files).catch((err) => toastError(err));
  });
}

// ---------------------------------------------------------------- keyboard

function setupKeys() {
  document.addEventListener("keydown", (e) => {
    if (lightboxKey(e)) { e.preventDefault(); return; }
    if (e.key !== "Escape") return;
    if (topModalDismiss()) { e.preventDefault(); return; }
    const menu = document.getElementById("game-menu");
    if (!menu.hidden) { closeMenu(); e.preventDefault(); return; }
    if (isDetailOpen()) { closeDetail(); e.preventDefault(); }
  });
}

// ---------------------------------------------------------------- start

async function start() {
  hydrateIcons();
  document.querySelectorAll(".nav-item").forEach((b) => b.addEventListener("click", () => {
    if (isDetailOpen()) closeDetail();
    navigate(b.dataset.page);
  }));
  initGameSwitch();
  initTray();
  initPlans();
  wireEvents();
  subscribe(onStateChange);
  setupDragDrop();
  setupKeys();
  initArt();

  try {
    const init = await host.call("app.init", {});
    state.version = init.version || "";
    state.games = init.games || [];
    state.selectedGameId = init.selectedGameId || (state.games[0] && state.games[0].id) || null;
    state.settings = { ...state.settings, ...(init.settings || {}) };
    state.lockedEdition = init.lockedEdition || null;
    if (state.lockedEdition) {
      // the Legacy-only / Enhanced-only builds say which game they're for under the logo; nothing else changes
      document.title = init.productName || document.title;
      const brand = document.querySelector(".brand-text");
      if (brand && !brand.querySelector(".brand-edition")) {
        const tag = document.createElement("span");
        tag.className = "brand-edition brand-edition-" + state.lockedEdition.toLowerCase();
        tag.textContent = state.lockedEdition.toUpperCase();
        brand.appendChild(tag);
      }
    }
  } catch (e) {
    toastError(e, "The launcher couldn't start properly");
  }

  renderGameSwitch();
  navigate("home");
  if (host.isMock) toast("Preview mode: demo data, nothing is installed.", "info", 6000);
  loadFavorites();

  if (state.selectedGameId) {
    try {
      state.status = await host.call("game.status", {});
      changed("status");
    } catch (e) {
      toastError(e, "Couldn't check the game");
    }
    loadHealth();
    await loadLibrary();
    changed("library");
  }
}

start();
