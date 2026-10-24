// Small DOM and formatting helpers shared by every page.
import { icon } from "./icons.js";

/** h("div.card#id", {onclick, ...attrs}, children...) — builds real DOM nodes. */
export function h(spec, props, ...children) {
  if (props == null || typeof props !== "object" || props instanceof Node || Array.isArray(props)) {
    if (props !== undefined && props !== null) children.unshift(props);
    props = {};
  }
  const m = /^([a-z0-9]+)?((?:[.#][\w-]+)*)$/i.exec(spec) || [];
  const el = document.createElement(m[1] || "div");
  for (const part of (m[2] || "").match(/[.#][\w-]+/g) || []) {
    if (part[0] === ".") el.classList.add(part.slice(1));
    else el.id = part.slice(1);
  }
  for (const [k, v] of Object.entries(props)) {
    if (v == null || v === false) continue;
    if (k.startsWith("on") && typeof v === "function") el.addEventListener(k.slice(2).toLowerCase(), v);
    else if (k === "class") el.className += " " + v;
    else if (k === "html") el.innerHTML = v;
    else if (k === "style" && typeof v === "object") Object.assign(el.style, v);
    else if (k === "dataset") Object.assign(el.dataset, v);
    else if (v === true) el.setAttribute(k, "");
    else el.setAttribute(k, v);
  }
  append(el, children);
  return el;
}

function append(el, children) {
  for (const c of children.flat(Infinity)) {
    if (c == null || c === false) continue;
    el.append(c instanceof Node ? c : document.createTextNode(String(c)));
  }
}

/** An icon as a DOM node. */
export function ic(name, cls = "") {
  const t = document.createElement("template");
  t.innerHTML = icon(name, cls);
  return t.content.firstElementChild;
}

export function clear(el) {
  while (el.firstChild) el.removeChild(el.firstChild);
  return el;
}

export function esc(s) {
  return String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}

export function fmtNum(n) {
  if (n == null || isNaN(n)) return "–";
  if (n >= 1e6) return (n / 1e6).toFixed(n >= 1e7 ? 0 : 1).replace(/\.0$/, "") + "M";
  if (n >= 1e3) return (n / 1e3).toFixed(n >= 1e4 ? 0 : 1).replace(/\.0$/, "") + "K";
  return String(n);
}

export function fmtBytes(n) {
  if (n == null) return "";
  const u = ["B", "KB", "MB", "GB"];
  let i = 0;
  while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; }
  return `${n.toFixed(i === 0 ? 0 : 1)} ${u[i]}`;
}

export function fmtDate(iso) {
  if (!iso) return "";
  const d = new Date(iso);
  if (isNaN(d)) return String(iso);
  const days = Math.floor((Date.now() - d.getTime()) / 86400000);
  if (days < 1) return "today";
  if (days < 2) return "yesterday";
  if (days < 30) return `${days} days ago`;
  return d.toLocaleDateString(undefined, { year: "numeric", month: "short", day: "numeric" });
}

/** Image that fades in when loaded and falls back to a placeholder icon. */
export function lazyImg(src, alt = "", cls = "") {
  // no referrer: the sites' image CDNs may refuse hotlinks from an unknown origin
  const img = h("img", { alt, loading: "lazy", decoding: "async", class: cls || null, referrerpolicy: "no-referrer" });
  img.addEventListener("load", () => img.classList.add("loaded"));
  img.addEventListener("error", () => { img.style.visibility = "hidden"; });
  if (src) img.src = src;
  else img.style.visibility = "hidden";
  return img;
}

// ---------------------------------------------------------------- toasts

const toastBox = () => document.getElementById("toasts");
const TOAST_ICON = { ok: "check", info: "info", warn: "alert", error: "block" };

export function toast(text, kind = "info", ms) {
  const el = h(`div.toast.${kind}`, { role: kind === "error" ? "alert" : "status" },
    ic(TOAST_ICON[kind] || "info"),
    h("div.toast-text", text),
    h("button", { type: "button", "aria-label": "Dismiss", onclick: () => close() }, ic("x", "ico-sm")));
  toastBox().append(el);
  const timer = setTimeout(close, ms ?? (kind === "error" ? 9000 : 5000));
  function close() {
    clearTimeout(timer);
    el.classList.add("leaving");
    setTimeout(() => el.remove(), 220);
  }
  return close;
}

/** Shows an error from host.call in a toast, with a friendlier line for known codes. */
export function toastError(err, prefix) {
  const msg = err && err.message ? err.message : String(err);
  toast(prefix ? `${prefix}: ${msg}` : msg, err && err.code === "BLOCKED" ? "warn" : "error");
}

// ---------------------------------------------------------------- modals

const layer = () => document.getElementById("modal-layer");
const modalStack = [];

/** Opens a modal; returns { el, close }. onClose runs once. */
export function openModal(content, { small = false, onClose, dismissable = true } = {}) {
  const box = h(`div.modal${small ? ".modal-sm" : ""}`, { role: "dialog", "aria-modal": "true" });
  box.append(content);
  const entry = { box, onClose, dismissable, closed: false };
  modalStack.push(entry);
  renderLayer();
  setTimeout(() => {
    const f = box.querySelector("[autofocus], .btn-primary, button");
    if (f) f.focus();
  }, 30);
  return {
    el: box,
    close() { closeEntry(entry); },
    setDismissable(v) { entry.dismissable = v; },
  };
}

function closeEntry(entry) {
  if (entry.closed) return;
  entry.closed = true;
  const i = modalStack.indexOf(entry);
  if (i >= 0) modalStack.splice(i, 1);
  renderLayer();
  if (entry.onClose) entry.onClose();
}

function renderLayer() {
  const l = layer();
  clear(l);
  const top = modalStack[modalStack.length - 1];
  if (!top) { l.hidden = true; return; }
  l.hidden = false;
  l.append(top.box);
}

export function topModalDismiss() {
  const top = modalStack[modalStack.length - 1];
  if (top && top.dismissable) { closeEntry(top); return true; }
  return false;
}

document.addEventListener("mousedown", (e) => {
  if (e.target === layer()) topModalDismiss();
});

/** Yes/no dialog. Resolves true when confirmed. */
export function confirmDialog({ title, text, ok = "OK", danger = false }) {
  return new Promise((resolve) => {
    let result = false;
    const okBtn = h(`button.btn.${danger ? "btn-danger" : "btn-primary"}`, { type: "button", onclick: () => { result = true; m.close(); } }, ok);
    const body = h("div",
      h("div.confirm-body", h("h3", title), h("p", text)),
      h("div.modal-foot",
        h("button.btn.btn-ghost", { type: "button", onclick: () => m.close() }, "Cancel"),
        okBtn));
    const m = openModal(body, { small: true, onClose: () => resolve(result) });
    setTimeout(() => okBtn.focus(), 40);
  });
}

/** Text-entry dialog. Resolves the entered text, or null when cancelled. */
export function promptDialog({ title, text, value = "", placeholder = "", ok = "OK", multiline = false, mono = false }) {
  return new Promise((resolve) => {
    let result = null;
    const field = multiline
      ? h(`textarea.input.textarea${mono ? ".mono" : ""}`, { rows: "5", placeholder, "aria-label": title })
      : h("input.input", { type: "text", placeholder, "aria-label": title, style: { width: "100%" } });
    field.value = value;
    const submit = () => {
      const v = field.value.trim();
      if (!v) { field.focus(); return; }
      result = v;
      m.close();
    };
    if (!multiline) field.addEventListener("keydown", (e) => { if (e.key === "Enter") { e.preventDefault(); submit(); } });
    const body = h("div",
      h("div.confirm-body", h("h3", title), text ? h("p", text) : null, field),
      h("div.modal-foot",
        h("button.btn.btn-ghost", { type: "button", onclick: () => m.close() }, "Cancel"),
        h("button.btn.btn-primary", { type: "button", onclick: submit }, ok)));
    const m = openModal(body, { small: true, onClose: () => resolve(result) });
    setTimeout(() => { field.focus(); if (!multiline) field.select(); }, 40);
  });
}

/** A modal that only shows something (share code, results). Returns the modal handle. */
export function infoDialog(title, ...content) {
  const body = h("div",
    h("div.confirm-body", h("h3", title), ...content),
    h("div.modal-foot", h("button.btn.btn-primary", { type: "button", onclick: () => m.close() }, "Close")));
  const m = openModal(body, { small: true });
  return m;
}

/** Copies text to the clipboard (works inside WebView2's https origin, with a fallback). */
export async function copyText(text, what = "Copied") {
  try {
    await navigator.clipboard.writeText(text);
  } catch {
    const ta = h("textarea", { style: { position: "fixed", left: "-9999px", top: "0" } });
    ta.value = text;
    document.body.append(ta);
    ta.select();
    try { document.execCommand("copy"); } catch { /* nothing else to try */ }
    ta.remove();
  }
  toast(`${what} to the clipboard.`, "ok", 2200);
}

/** True for links that should open inside the launcher's site browser rather than the system browser. */
export function isSiteUrl(url) {
  return /^https:\/\/(www\.)?(gta5-mods\.com|lcpdfr\.com)\//i.test(url || "");
}
