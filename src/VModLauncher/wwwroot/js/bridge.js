// Talks to the C# host over WebView2 (see docs/bridge.md).
// Outside the launcher (a plain browser) it loads js/mock.js so the UI can be previewed with demo data.

const webview = window.chrome && window.chrome.webview ? window.chrome.webview : null;

// Commands that wait on the player (file pickers, UAC, the site's download page) or on long work.
const NO_TIMEOUT = new Set([
  "plan.install", "plan.locate", "mod.download", "mod.importFile", "mod.importDropped", "games.addFolder",
  "game.fixAccess", "library.setEnabled", "library.uninstall", "library.checkUpdates",
  // wave 2: whole-library work and downloads
  "game.repair", "profiles.apply", "frameworks.install", "storage.clean", "diag.report", "art.list",
  // wave 3: folder picker, whole-pack scans and edits
  "packbuild.fromFolder", "tex.scan", "tex.optimize", "packs.setEnabled", "packs.register", "packs.fix",
  "gamefiles.check", "gamefiles.moveToMods",
]);
const TIMEOUT_MS = 120000;
// Site reads can wait on LCPDFR's browser check (up to 45 s), a 90 s call and one retry.
const SITE_TIMEOUT_MS = 300000;
const SITE_CALLS = new Set(["site.categories", "site.list", "site.search", "site.detail"]);

const pending = new Map();
const listeners = new Map();
let seq = 0;
let mockPromise = null;

function emit(event, data) {
  const set = listeners.get(event);
  if (!set) return;
  for (const fn of [...set]) {
    try { fn(data); } catch (e) { console.error(`listener for ${event} failed`, e); }
  }
}

function onMessage(ev) {
  let msg = ev.data;
  if (typeof msg === "string") {
    try { msg = JSON.parse(msg); } catch { return; }
  }
  if (!msg || typeof msg !== "object") return;
  if (msg.event) { emit(msg.event, msg.data); return; }
  const p = pending.get(msg.id);
  if (!p) return;
  pending.delete(msg.id);
  clearTimeout(p.timer);
  if (msg.ok) p.resolve(msg.result);
  else p.reject(hostError(msg.error, msg.code));
}

function hostError(message, code) {
  const err = new Error(message || "Something went wrong");
  err.code = code || "ERROR";
  return err;
}

if (webview) webview.addEventListener("message", onMessage);

function mock() {
  if (!mockPromise) mockPromise = import("./mock.js").then((m) => m.createMock(emit));
  return mockPromise;
}

function send(cmd, args, files) {
  const id = "r" + (++seq);
  return new Promise((resolve, reject) => {
    const timer = NO_TIMEOUT.has(cmd)
      ? null
      : setTimeout(() => {
          pending.delete(id);
          reject(hostError(`The launcher didn't answer (${cmd})`, "TIMEOUT"));
        }, SITE_CALLS.has(cmd) ? SITE_TIMEOUT_MS : TIMEOUT_MS);
    pending.set(id, { resolve, reject, timer });
    const msg = { id, cmd, args: args || {} };
    try {
      if (files) webview.postMessageWithAdditionalObjects(msg, files);
      else webview.postMessage(msg);
    } catch (e) {
      pending.delete(id);
      clearTimeout(timer);
      reject(hostError(e.message));
    }
  });
}

export const host = {
  isMock: !webview,

  async call(cmd, args = {}) {
    if (webview) return send(cmd, args);
    const m = await mock();
    try {
      return await m.call(cmd, args);
    } catch (e) {
      throw hostError(e.message, e.code);
    }
  },

  /** Sends dropped File objects; the host reads their real paths. */
  async callWithFiles(cmd, args, files) {
    if (webview && typeof webview.postMessageWithAdditionalObjects === "function") return send(cmd, args, files);
    const m = await mock();
    return m.call(cmd, { ...args, files: [...files].map((f) => f.name) });
  },

  on(event, fn) {
    if (!listeners.has(event)) listeners.set(event, new Set());
    listeners.get(event).add(fn);
    if (!webview) mock(); // make sure demo events can flow
    return () => listeners.get(event).delete(fn);
  },
};
