// Page switching. Pages are plain functions that render into <main>.
import { state, changed } from "./state.js";

let pages = {};
const main = () => document.getElementById("main");

export function registerPages(map) {
  pages = map;
}

export function navigate(page) {
  if (!pages[page]) return;
  const same = state.page === page;
  state.page = page;
  document.querySelectorAll(".nav-item").forEach((b) => {
    const on = b.dataset.page === page;
    b.classList.toggle("active", on);
    if (on) b.setAttribute("aria-current", "page"); else b.removeAttribute("aria-current");
  });
  render();
  if (!same) main().scrollTop = 0;
  changed("page");
}

/** Re-renders the current page in place (keeps the scroll position). */
export function render() {
  const el = main();
  const top = el.scrollTop;
  pages[state.page](el);
  el.scrollTop = top;
}
