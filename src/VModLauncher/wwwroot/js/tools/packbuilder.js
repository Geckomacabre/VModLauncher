// Card at the top of the "Add-on packs" tab: build an add-on pack from a folder of loose car or ped files.
// The packing itself happens during analysis (ModAnalyzer), so a picked folder goes through the normal
// plan.ready flow and shows up as an install plan with one "Built from the mod's loose files" pack.
import { host } from "../bridge.js";
import { h, ic, toastError } from "../ui.js";

let busy = false;

async function fromFolder(btn) {
  if (busy) return;
  busy = true;
  btn.disabled = true;
  try {
    await host.call("packbuild.fromFolder", {});
  } catch (e) {
    toastError(e, "Couldn't build the pack");
  } finally {
    busy = false;
    btn.disabled = false;
  }
}

export function packBuilderCard() {
  const btn = h("button.btn.btn-primary", { type: "button" }, ic("folder"), "Build a pack from a folder…");
  btn.onclick = () => fromFolder(btn);
  return h("section.card.pack-builder", { "aria-labelledby": "pb-title" },
    h("h2.card-title#pb-title", "Add-on pack builder", h("span.card-aside", ic("car"), "Cars and peds")),
    h("div.mods-toggle", { style: { paddingTop: "0" } },
      h("div.mods-toggle-text",
        h("p.card-sub", { style: { margin: "0" } },
          "Some add-on cars and peds come as loose files; the launcher packs them into a proper add-on pack automatically when you install them. Got a folder of loose files? Build a pack from it.")),
      btn));
}
