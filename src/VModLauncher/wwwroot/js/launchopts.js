// Launch options editor (args.txt / commandline.txt), shared by Tools and Settings.
import { host } from "./bridge.js";
import { h, ic, clear, toast, toastError } from "./ui.js";
import { currentEdition } from "./state.js";

/** Returns a card element that loads and edits the selected game's launch options. */
export function launchOptionsCard() {
  const card = h("section.card", { "aria-labelledby": "lo-title" },
    h("h2.card-title#lo-title", "Launch options", h("span.card-aside.lo-file", "")),
    h("p.card-sub", "Extra settings GTA V reads when it starts. They're saved in the game folder, so they apply however you launch the game."));
  const body = h("div", h("div.row.muted", h("div.spinner"), "Reading the game's launch options…"));
  card.append(body);
  load(card, body);
  return card;
}

async function load(card, body) {
  let opts;
  try {
    opts = await host.call("game.launchOptions", {});
  } catch (e) {
    clear(body).append(h("div.row.muted", ic("alert", "ico-sm"), `Couldn't read them: ${e.message}`));
    return;
  }
  paint(card, body, opts);
}

function paint(card, body, opts) {
  clear(body);
  const fileEl = card.querySelector(".lo-file");
  if (fileEl) fileEl.textContent = opts.file || "";
  const edition = currentEdition().toLowerCase();
  const known = (opts.known || []).filter((k) => !k.editions || !k.editions.length || k.editions.some((e) => String(e).toLowerCase() === edition));
  const on = new Set((opts.args || []).map((a) => a.toLowerCase()));
  const knownSet = new Set((opts.known || []).map((k) => k.arg.toLowerCase()));

  const toggles = known.map((k) => {
    const input = h("input", { type: "checkbox", role: "switch", "aria-label": k.label });
    input.checked = on.has(k.arg.toLowerCase());
    input.dataset.arg = k.arg;
    return h("div.setting",
      h("div.setting-text", h("b", k.label, h("code.arg", k.arg)), h("span", k.help || "")),
      h("label.switch", input, h("span.switch-track")));
  });

  const extra = h("input.input.mono", {
    type: "text", placeholder: "-anotherOption -value=1", "aria-label": "Other launch options", style: { width: "100%" },
  });
  extra.value = (opts.args || []).filter((a) => !knownSet.has(a.toLowerCase())).join(" ");

  const save = h("button.btn.btn-primary", { type: "button" }, ic("save"), "Save");
  save.addEventListener("click", async () => {
    const args = [];
    for (const t of body.querySelectorAll("input[data-arg]")) if (t.checked) args.push(t.dataset.arg);
    for (const a of extra.value.split(/\s+/)) if (a.trim()) args.push(a.trim());
    save.disabled = true;
    try {
      const r = await host.call("game.setLaunchOptions", { args });
      toast(`Saved to ${r.file || "the game folder"}.`, "ok");
      paint(card, body, r);
    } catch (e) {
      save.disabled = false;
      toastError(e);
    }
  });

  if (!toggles.length) body.append(h("p.muted", "No common options for this edition."));
  body.append(...toggles,
    h("div.setting.setting-col",
      h("div.setting-text", h("b", "Other options"), h("span", "Anything else, separated by spaces. Leave empty if you're not sure.")),
      extra),
    h("div.row", { style: { justifyContent: "flex-end", marginTop: "12px" } }, save));
}
