// Line icons (24px grid, stroke = currentColor). Drawn for this launcher.
const P = {
  home: '<path d="M3 10.5 12 3l9 7.5"/><path d="M5 9.5V20a1 1 0 0 0 1 1h4v-6h4v6h4a1 1 0 0 0 1-1V9.5"/>',
  grid: '<rect x="3.5" y="3.5" width="7" height="7" rx="1.6"/><rect x="13.5" y="3.5" width="7" height="7" rx="1.6"/><rect x="3.5" y="13.5" width="7" height="7" rx="1.6"/><rect x="13.5" y="13.5" width="7" height="7" rx="1.6"/>',
  library: '<path d="M4 4.5h4v15H4z"/><path d="M10 4.5h4v15h-4z"/><path d="m16.2 5.4 3.8-1 3 14.4-3.8 1z" transform="translate(-2 0)"/>',
  settings: '<circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z"/>',
  play: '<path d="M7 4.8v14.4a1 1 0 0 0 1.5.86l11.6-7.2a1 1 0 0 0 0-1.72L8.5 3.94A1 1 0 0 0 7 4.8z" fill="currentColor" stroke="none"/>',
  shield: '<path d="M12 3 4.5 6v5.5c0 4.6 3.1 8.4 7.5 9.5 4.4-1.1 7.5-4.9 7.5-9.5V6z"/>',
  badge: '<path d="M12 3 4.5 6v5.5c0 4.6 3.1 8.4 7.5 9.5 4.4-1.1 7.5-4.9 7.5-9.5V6z"/><path d="m9 12 2 2 4-4"/>',
  download: '<path d="M12 4v11"/><path d="m7 10 5 5 5-5"/><path d="M5 20h14"/>',
  search: '<circle cx="11" cy="11" r="6.5"/><path d="m20 20-4.2-4.2"/>',
  check: '<path d="m5 12.5 4.5 4.5L19 7.5"/>',
  x: '<path d="M6 6l12 12M18 6 6 18"/>',
  alert: '<path d="M12 4 2.8 19.5a1 1 0 0 0 .87 1.5h16.66a1 1 0 0 0 .87-1.5L12 4z"/><path d="M12 10v4.5"/><path d="M12 17.6v.01"/>',
  info: '<circle cx="12" cy="12" r="9"/><path d="M12 11v6"/><path d="M12 7.5v.01"/>',
  block: '<circle cx="12" cy="12" r="9"/><path d="m5.7 5.7 12.6 12.6"/>',
  folder: '<path d="M3.5 6.5a1.5 1.5 0 0 1 1.5-1.5h4.2l2 2.2H19a1.5 1.5 0 0 1 1.5 1.5v9.3A1.5 1.5 0 0 1 19 19.5H5a1.5 1.5 0 0 1-1.5-1.5z"/>',
  external: '<path d="M14 4h6v6"/><path d="M20 4 11 13"/><path d="M18 14v4.5a1.5 1.5 0 0 1-1.5 1.5h-11A1.5 1.5 0 0 1 4 18.5v-11A1.5 1.5 0 0 1 5.5 6H10"/>',
  file: '<path d="M14 3.5H7A1.5 1.5 0 0 0 5.5 5v14A1.5 1.5 0 0 0 7 20.5h10a1.5 1.5 0 0 0 1.5-1.5V8z"/><path d="M14 3.5V8h4.5"/>',
  package: '<path d="m12 3 8 4.5v9L12 21l-8-4.5v-9z"/><path d="m4 7.5 8 4.5 8-4.5"/><path d="M12 12v9"/>',
  archive: '<rect x="3.5" y="4" width="17" height="4.5" rx="1"/><path d="M5 8.5V19a1.5 1.5 0 0 0 1.5 1.5h11A1.5 1.5 0 0 0 19 19V8.5"/><path d="M10 12.5h4"/>',
  box: '<path d="M4 7.5 12 3l8 4.5"/><path d="M4 7.5v9L12 21l8-4.5v-9"/><path d="M8 5.3 16 9.8v3.7"/>',
  save: '<path d="M5 3.5h11.5L20.5 7.5V19A1.5 1.5 0 0 1 19 20.5H5A1.5 1.5 0 0 1 3.5 19V5A1.5 1.5 0 0 1 5 3.5z"/><path d="M7.5 3.5v5h8v-5"/><rect x="7" y="13" width="10" height="7.5" rx="1"/>',
  minus: '<path d="M6 12h12"/>',
  power: '<path d="M12 3v8"/><path d="M6.4 6.6a8 8 0 1 0 11.2 0"/>',
  refresh: '<path d="M20 11a8 8 0 0 0-14.3-4.9L4 8"/><path d="M4 3.5V8h4.5"/><path d="M4 13a8 8 0 0 0 14.3 4.9L20 16"/><path d="M20 20.5V16h-4.5"/>',
  plus: '<path d="M12 5v14M5 12h14"/>',
  chevron: '<path d="m9 6 6 6-6 6"/>',
  chevrons: '<path d="m8 9 4-4 4 4"/><path d="m8 15 4 4 4-4"/>',
  left: '<path d="m15 6-6 6 6 6"/>',
  right: '<path d="m9 6 6 6-6 6"/>',
  star: '<path d="m12 3.6 2.6 5.3 5.8.9-4.2 4.1 1 5.8L12 17l-5.2 2.7 1-5.8-4.2-4.1 5.8-.9z" fill="currentColor" stroke="none"/>',
  thumb: '<path d="M7.5 20.5h-3v-9h3"/><path d="M7.5 11.5 11 4a2.2 2.2 0 0 1 2.2 2.4l-.4 3.6h5.4a2 2 0 0 1 2 2.3l-1.2 6.4a2.2 2.2 0 0 1-2.2 1.8H7.5z"/>',
  trash: '<path d="M4.5 7h15"/><path d="M9.5 7V4.5h5V7"/><path d="M6.5 7l.8 12.1A1.5 1.5 0 0 0 8.8 20.5h6.4a1.5 1.5 0 0 0 1.5-1.4L17.5 7"/>',
  eye: '<path d="M2.5 12S6 5.5 12 5.5 21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12z"/><circle cx="12" cy="12" r="3"/>',
  list: '<path d="M9 6.5h11M9 12h11M9 17.5h11"/><path d="M4.5 6.5h.01M4.5 12h.01M4.5 17.5h.01"/>',
  car: '<path d="M5 16.5v2M19 16.5v2"/><path d="M3.5 16.5V12l2-5.3A1.5 1.5 0 0 1 6.9 5.7h10.2a1.5 1.5 0 0 1 1.4 1L20.5 12v4.5z"/><path d="M3.5 12h17"/><path d="M7 14.3h.01M17 14.3h.01"/>',
  lock: '<rect x="5" y="10.5" width="14" height="10" rx="1.8"/><path d="M8 10.5V8a4 4 0 0 1 8 0v2.5"/>',
  globe: '<circle cx="12" cy="12" r="9"/><path d="M3 12h18"/><path d="M12 3a14 14 0 0 1 0 18 14 14 0 0 1 0-18z"/>',
  bolt: '<path d="M13 2.5 4.5 13.5H12l-1 8 8.5-11H12z"/>',
  siren: '<path d="M6 18v-6a6 6 0 0 1 12 0v6"/><path d="M4 18h16v2.5H4z"/><path d="M12 2.5v2M4.2 5.2l1.4 1.4M19.8 5.2l-1.4 1.4"/>',
  tools: '<path d="M14.7 6.3a4 4 0 0 0-5.2 5.2L3.8 17.2a1.6 1.6 0 0 0 2.3 2.3l5.7-5.7a4 4 0 0 0 5.2-5.2l-2.6 2.6-2.3-.4-.4-2.3z"/>',
  wrench: '<path d="M14.7 6.3a4 4 0 0 0-5.2 5.2L3.8 17.2a1.6 1.6 0 0 0 2.3 2.3l5.7-5.7a4 4 0 0 0 5.2-5.2l-2.6 2.6-2.3-.4-.4-2.3z"/>',
  layers: '<path d="m12 3.5 8.5 4.5L12 12.5 3.5 8z"/><path d="m3.5 12 8.5 4.5 8.5-4.5"/><path d="m3.5 16 8.5 4.5 8.5-4.5"/>',
  starOutline: '<path d="m12 3.6 2.6 5.3 5.8.9-4.2 4.1 1 5.8L12 17l-5.2 2.7 1-5.8-4.2-4.1 5.8-.9z"/>',
  copy: '<rect x="8.5" y="8.5" width="12" height="12" rx="1.5"/><path d="M15.5 8.5V5A1.5 1.5 0 0 0 14 3.5H5A1.5 1.5 0 0 0 3.5 5v9A1.5 1.5 0 0 0 5 15.5h3.5"/>',
  user: '<circle cx="12" cy="8" r="4"/><path d="M4.5 20.5a7.5 7.5 0 0 1 15 0"/>',
  gun: '<path d="M3.5 8.5h16l1 3h-6l-1 2.5h-3l-1.5 5h-3.5l1.5-6.5H3.5z"/>',
  map: '<path d="m3.5 6 5.5-2.5 6 2.5 5.5-2.5V18l-5.5 2.5-6-2.5L3.5 20.5z"/><path d="M9 3.5v14.5M15 6v14.5"/>',
  code: '<path d="m8.5 7-5 5 5 5"/><path d="m15.5 7 5 5-5 5"/><path d="m13.5 4.5-3 15"/>',
  terminal: '<rect x="3" y="4.5" width="18" height="15" rx="1.5"/><path d="m7 9.5 3 2.5-3 2.5"/><path d="M12.5 15h4.5"/>',
  pulse: '<path d="M3 12h4l2.5-6 4.5 12 2.5-6H21"/>',
  disk: '<ellipse cx="12" cy="6" rx="8" ry="2.8"/><path d="M4 6v12c0 1.5 3.6 2.8 8 2.8s8-1.3 8-2.8V6"/><path d="M4 12c0 1.5 3.6 2.8 8 2.8s8-1.3 8-2.8"/>',
  sliders: '<path d="M4 6.5h9M17 6.5h3M4 12h3M11 12h9M4 17.5h11M19 17.5h1"/><circle cx="15" cy="6.5" r="2"/><circle cx="9" cy="12" r="2"/><circle cx="17" cy="17.5" r="2"/>',
  upload: '<path d="M12 15V4"/><path d="m7 9 5-5 5 5"/><path d="M5 20h14"/>',
  undo: '<path d="M8.5 5 4 9.5 8.5 14"/><path d="M4 9.5h10a6 6 0 0 1 0 12h-3"/>',
  link: '<path d="M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1"/><path d="M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1"/>',
  quarantine: '<rect x="4" y="4" width="16" height="16" rx="1.5"/><path d="m8.5 8.5 7 7M15.5 8.5l-7 7"/>',
  target: '<circle cx="12" cy="12" r="8"/><circle cx="12" cy="12" r="3"/><path d="M12 1.5v4M12 18.5v4M1.5 12h4M18.5 12h4"/>',
};

export function icon(name, cls = "") {
  const body = P[name] || P.info;
  return `<span class="ico ${cls}" aria-hidden="true"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">${body}</svg></span>`;
}

/** Fills every <span data-icon="x"> already in the page (static markup in index.html). */
export function hydrateIcons(root = document) {
  root.querySelectorAll("[data-icon]").forEach((el) => {
    const name = el.getAttribute("data-icon");
    el.removeAttribute("data-icon");
    const tmp = document.createElement("span");
    tmp.innerHTML = icon(name, el.className.replace(/\bico\b/, "").trim());
    el.replaceWith(tmp.firstElementChild);
  });
}
