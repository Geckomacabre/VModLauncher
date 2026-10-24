// Real GTA V artwork, pulled by the host from the player's own installed game (art.list / art.ready).
// hero   = full-bleed newswire character artworks (Home hero)
// panels = loading-screen paintings in the cover-art style (page backdrop, empty states, success screen)
// Nothing is drawn here: with no art the UI is plain near-black.
import { host } from "./bridge.js";

const ROTATE_MS = 12000;
const art = { hero: [], panels: [], status: "extracting" };
let heroIndex = 0;
let panelIndex = 0;
let timer = null;
const heroMounts = new Set();
const reduceMotion = window.matchMedia ? window.matchMedia("(prefers-reduced-motion: reduce)").matches : false;

export function artStatus() {
  return art.status;
}

export async function initArt() {
  host.on("art.ready", (d) => apply(d));
  try {
    apply(await host.call("art.list", {}));
  } catch {
    apply({ hero: [], panels: [], status: "unavailable" });
  }
}

function apply(d) {
  if (!d) return;
  const hadHero = art.hero.length;
  art.hero = Array.isArray(d.hero) ? d.hero.filter(Boolean) : [];
  art.panels = Array.isArray(d.panels) ? d.panels.filter(Boolean) : [];
  art.status = d.status || (art.hero.length || art.panels.length ? "ready" : "unavailable");
  if (!hadHero && art.hero.length) {
    // start on a different image each launch
    heroIndex = Math.floor(Math.random() * art.hero.length);
  }
  if (art.panels.length) panelIndex = Math.floor(Math.random() * art.panels.length);
  preload(art.hero[heroIndex]);
  paintBackdrop();
  for (const m of [...heroMounts]) m.paint(true);
  for (const el of document.querySelectorAll("[data-panel-art]")) fillPanel(el);
  startRotation();
}

function preload(src) {
  if (!src) return;
  const img = new Image();
  img.src = src;
}

function startRotation() {
  if (timer || reduceMotion || art.hero.length < 2) return;
  timer = setInterval(() => {
    if (document.hidden) return;
    heroIndex = (heroIndex + 1) % art.hero.length;
    preload(art.hero[(heroIndex + 1) % art.hero.length]);
    for (const m of [...heroMounts]) m.paint(false);
    paintBackdrop();
  }, ROTATE_MS);
}

// ---------------------------------------------------------------- page backdrop

/** The pause menu sits over a dimmed scene: a blurred, very dark copy of the current artwork. */
function paintBackdrop() {
  const bd = document.getElementById("backdrop");
  if (!bd) return;
  const src = art.hero[heroIndex] || art.panels[panelIndex] || null;
  const layers = bd.querySelectorAll(".bd-layer");
  if (layers.length < 2) return;
  const [a, b] = layers;
  const shown = a.classList.contains("on") ? a : b;
  const next = shown === a ? b : a;
  if (!src) {
    a.classList.remove("on");
    b.classList.remove("on");
    return;
  }
  if (shown.dataset.src === src && shown.classList.contains("on")) return;
  next.style.backgroundImage = `url("${src}")`;
  next.dataset.src = src;
  next.classList.add("on");
  shown.classList.remove("on");
}

// ---------------------------------------------------------------- home hero

/** Fills a .hero-art element with two cross-fading layers that follow the rotation. */
export function mountHero(el) {
  const l1 = document.createElement("div");
  const l2 = document.createElement("div");
  l1.className = "hero-img";
  l2.className = "hero-img";
  el.append(l1, l2);
  let front = l1;
  const mount = {
    paint(instant) {
      if (!el.isConnected && el.dataset.mounted) { heroMounts.delete(mount); return; }
      el.dataset.mounted = "1";
      const src = art.hero[heroIndex] || null;
      el.classList.toggle("has-art", !!src);
      if (!src) { l1.classList.remove("on"); l2.classList.remove("on"); return; }
      if (front.dataset.src === src && front.classList.contains("on")) return;
      const back = front === l1 ? l2 : l1;
      back.style.backgroundImage = `url("${src}")`;
      back.dataset.src = src;
      // restart the slow pan on the incoming image
      back.classList.remove("kb");
      void back.offsetWidth;
      back.classList.add("kb");
      if (instant) back.classList.add("instant");
      back.classList.add("on");
      front.classList.remove("on");
      if (instant) requestAnimationFrame(() => back.classList.remove("instant"));
      front = back;
    },
  };
  heroMounts.add(mount);
  mount.paint(true);
  return mount;
}

// ---------------------------------------------------------------- panels (empty states, success)

/**
 * An element showing one loading-screen painting, or nothing when the game's art isn't available.
 * `key` names the spot ("library-empty", "plan-done", …) so it shows the same painting across re-renders.
 */
export function panelArt(key = "panel", cls = "") {
  const el = document.createElement("div");
  el.className = "panel-art" + (cls ? " " + cls : "");
  el.setAttribute("data-panel-art", "");
  el.setAttribute("aria-hidden", "true");
  let n = 7;
  for (const c of key) n = (n * 31 + c.charCodeAt(0)) >>> 0;
  el.dataset.offset = String(n % 997);
  fillPanel(el);
  return el;
}

function fillPanel(el) {
  const pick = art.panels.length ? art.panels[(panelIndex + Number(el.dataset.offset || 0)) % art.panels.length] : null;
  el.style.backgroundImage = pick ? `url("${pick}")` : "";
  el.classList.toggle("has-art", !!pick);
}
