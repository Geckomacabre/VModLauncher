// A blocking progress dialog for whole-game jobs (repair after an update, applying a profile).
// The host reports progress as install.progress events with a fixed planId ("repair", "profile").
import { host } from "./bridge.js";
import { h, openModal } from "./ui.js";

/** Runs call() while showing install.progress for planId; resolves with call()'s result or rethrows. */
export async function runWithProgress({ title, sub, planId, call }) {
  const stage = h("div.stage", title);
  const subEl = h("div.dim", sub || "Don't start the game until this finishes.");
  const bar = h("div");
  const progress = h("div.progress.indeterminate", bar);
  const detail = h("div.detail", "");
  const box = h("div.install-progress", stage, subEl, progress, detail);
  const m = openModal(h("div", box), { small: true, dismissable: false });
  const off = host.on("install.progress", (d) => {
    if (!d || d.planId !== planId) return;
    if (d.stage) stage.textContent = d.stage;
    detail.textContent = d.detail || "";
    if (d.fraction != null) {
      progress.classList.remove("indeterminate");
      bar.style.width = `${(Math.max(0, Math.min(1, d.fraction)) * 100).toFixed(1)}%`;
    } else {
      progress.classList.add("indeterminate");
      bar.style.width = "";
    }
  });
  try {
    return await call();
  } finally {
    off();
    m.setDismissable(true);
    m.close();
  }
}
