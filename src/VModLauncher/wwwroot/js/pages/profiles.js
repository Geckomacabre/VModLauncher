// Profiles: named sets of mods (ReSkate-style). Save what's on now, switch sets in one go, share with a code.
import { host } from "../bridge.js";
import { h, ic, clear, fmtDate, toast, toastError, confirmDialog, promptDialog, infoDialog, copyText } from "../ui.js";
import { state, changed, selectedGame } from "../state.js";
import { render } from "../router.js";
import { refreshLibrary } from "./library.js";
import { runWithProgress } from "../progress.js";
import { panelArt } from "../art.js";

let profiles = null;
let gameId = null;
let loading = false;
let error = null;

async function load(force) {
  if (loading) return;
  if (profiles && !force && gameId === state.selectedGameId) return;
  loading = true;
  const forGame = state.selectedGameId;
  try {
    profiles = await host.call("profiles.list", {});
    error = null;
  } catch (e) {
    error = e;
    profiles = [];
  }
  gameId = forGame;
  loading = false;
  if (state.page === "profiles") render();
}

export function renderProfiles(root) {
  clear(root);
  const page = h("div.page");
  root.append(page);
  const game = selectedGame();
  if (gameId !== state.selectedGameId) { profiles = null; error = null; }

  page.append(h("div.page-head",
    h("div", h("h1.page-title", "Profiles"),
      h("p.page-sub", "Save which mods are on as a set — \"LSPDFR night\", \"Clean story\", \"Car meet\" — and switch between them in one click. Share a set with a code.")),
    h("div.lib-toolbar",
      h("button.btn", { type: "button", disabled: !game || null, onclick: importCode }, ic("upload"), "Import code…"),
      h("button.btn.btn-primary", { type: "button", disabled: !game || !state.library.length || null, onclick: () => saveCurrent(null) }, ic("save"), "Save current mods"))));

  if (!game) {
    page.append(h("div.empty", panelArt("profiles-empty", "empty-art"), ic("car"), h("h3", "No game selected"), h("p", "Pick or add a GTA V install on the Home page first.")));
    return;
  }
  if (!profiles) {
    page.append(h("div.row.muted", { style: { padding: "30px 0" } }, h("div.spinner"), "Reading your profiles…"));
    load(false);
    return;
  }
  if (error) {
    page.append(h("div.alert.danger", ic("alert"), h("div.alert-body", h("b", "Couldn't read the profiles. "), error.message)));
  }
  if (!profiles.length) {
    page.append(h("div.empty",
      panelArt("profiles-empty", "empty-art"),
      ic("layers"),
      h("h3", "No profiles yet"),
      h("p", state.library.length
        ? "Switch on the mods you want together in Library, then save them here as a profile."
        : "Install a few mods first, then save the ones you play with together as a profile."),
      h("div.row",
        state.library.length ? h("button.btn.btn-primary", { type: "button", onclick: () => saveCurrent(null) }, ic("save"), "Save current mods") : null,
        h("button.btn", { type: "button", onclick: importCode }, ic("upload"), "Import a shared code"))));
    return;
  }

  const enabledNow = state.library.filter((m) => m.enabled).length;
  page.append(h("p.result-note", `${profiles.length} profile${profiles.length === 1 ? "" : "s"} · ${enabledNow} mod${enabledNow === 1 ? "" : "s"} on right now`));
  page.append(h("div.profile-list", profiles.map(profileCard)));
}

function profileCard(p) {
  const known = new Map(state.library.map((m) => [m.id, m]));
  const mods = p.mods || [];
  const missing = mods.filter((m) => !m.id || !known.has(m.id)).length;
  return h(`section.profile${p.active ? ".active" : ""}`,
    h("div.profile-head",
      h("div.grow",
        h("div.profile-name", p.name, p.active ? h("span.badge.badge-installed", "Active") : null),
        h("div.dim.small", `${mods.length} mod${mods.length === 1 ? "" : "s"} · saved ${fmtDate(p.updatedAt || p.createdAt)}${missing ? ` · ${missing} not in your library` : ""}`)),
      h("div.profile-actions",
        h("button.btn.btn-sm.btn-ghost", { type: "button", title: "Replace this profile with the mods that are on right now", onclick: () => saveCurrent(p) }, ic("save", "ico-sm"), "Update"),
        h("button.btn.btn-sm.btn-ghost", { type: "button", onclick: () => exportCode(p) }, ic("link", "ico-sm"), "Share"),
        h("button.btn.btn-sm.btn-danger", { type: "button", onclick: () => remove(p) }, ic("trash", "ico-sm"), "Delete"),
        h(`button.btn.btn-sm${p.active ? "" : ".btn-primary"}`, { type: "button", onclick: () => apply(p) }, ic("play", "ico-sm"), p.active ? "Re-apply" : "Apply"))),
    mods.length
      ? h("div.profile-mods", mods.slice(0, 24).map((m) => {
          const have = m.id && known.has(m.id);
          return h(`span.chip-static${have ? "" : ".missing"}`, { title: have ? m.name : `${m.name} isn't in your library any more` }, m.name, m.version ? h("span.dim", ` v${String(m.version).replace(/^v/i, "")}`) : null);
        }), mods.length > 24 ? h("span.dim.small", `+${mods.length - 24} more`) : null)
      : h("p.dim.small", "Empty: applying it turns every mod off (a clean game)."));
}

async function saveCurrent(existing) {
  const on = state.library.filter((m) => m.enabled);
  let name = existing ? existing.name : null;
  if (!existing) {
    name = await promptDialog({
      title: "Save current mods",
      text: `${on.length} mod${on.length === 1 ? " is" : "s are"} on right now. Give this set a name.`,
      placeholder: "LSPDFR night",
      ok: "Save",
    });
    if (!name) return;
  } else {
    const ok = await confirmDialog({ title: `Update ${existing.name}?`, text: `It will hold the ${on.length} mod${on.length === 1 ? "" : "s"} that are on right now.`, ok: "Update" });
    if (!ok) return;
  }
  try {
    await host.call("profiles.save", existing ? { name, id: existing.id } : { name });
    toast(`Saved "${name}".`, "ok");
  } catch (e) {
    toastError(e);
  }
  load(true);
}

async function apply(p) {
  const ok = await confirmDialog({
    title: `Apply ${p.name}?`,
    text: `Turns on the ${(p.mods || []).length} mod${(p.mods || []).length === 1 ? "" : "s"} in this profile and turns every other mod off. Nothing is uninstalled.`,
    ok: "Apply",
  });
  if (!ok) return;
  try {
    const r = await runWithProgress({
      title: `Applying ${p.name}`,
      sub: "Switching mods on and off. Don't start the game until this finishes.",
      planId: "profile",
      call: () => host.call("profiles.apply", { id: p.id }),
    });
    await refreshLibrary();
    // the host already says "Switched to …"; only open a dialog when there's more to tell
    if ((r.problems && r.problems.length) || (r.missing && r.missing.length)) {
      const content = [h("p", `${(r.enabled || []).length} turned on, ${(r.disabled || []).length} turned off.`)];
      if (r.problems && r.problems.length) content.push(h("div.notice.lvl-warn", ic("alert"), h("div", r.problems.slice(0, 5).join("; "))));
      if (r.missing && r.missing.length) content.push(missingList(r.missing));
      infoDialog(`${p.name} is active`, ...content);
    }
  } catch (e) {
    toastError(e, "Couldn't apply the profile");
    refreshLibrary();
  }
  load(true);
}

async function remove(p) {
  const ok = await confirmDialog({ title: `Delete ${p.name}?`, text: "Only the profile goes. Your mods stay installed as they are.", ok: "Delete", danger: true });
  if (!ok) return;
  try {
    await host.call("profiles.delete", { id: p.id });
    toast(`Deleted "${p.name}".`, "ok");
  } catch (e) {
    toastError(e);
  }
  load(true);
}

async function exportCode(p) {
  try {
    const r = await host.call("profiles.export", { id: p.id });
    const box = h("textarea.input.textarea.mono.code-box", { readonly: true, rows: "4", "aria-label": "Share code" });
    box.value = r.code;
    infoDialog(`Share ${p.name}`,
      h("p", "Anyone with the launcher can import this code. It lists each mod's name, version and page; it holds no files."),
      box,
      h("div.row", { style: { marginTop: "10px" } },
        h("button.btn.btn-sm", { type: "button", onclick: () => copyText(r.code, "Share code copied") }, ic("copy", "ico-sm"), "Copy code")));
    setTimeout(() => box.select(), 60);
  } catch (e) {
    toastError(e);
  }
}

async function importCode() {
  const code = await promptDialog({
    title: "Import a profile",
    text: "Paste a share code (it starts with VML1:).",
    placeholder: "VML1:…",
    ok: "Import",
    multiline: true,
    mono: true,
  });
  if (!code) return;
  try {
    const r = await host.call("profiles.import", { code: code.trim() });
    load(true);
    const content = [h("p", `Saved as "${r.profile.name}" with ${(r.profile.mods || []).length} mod${(r.profile.mods || []).length === 1 ? "" : "s"}.`)];
    if (r.missing && r.missing.length) content.push(missingList(r.missing));
    else content.push(h("p.dim", "You already have every mod in it. Apply it from the list."));
    infoDialog("Profile imported", ...content);
  } catch (e) {
    toastError(e, "That code didn't work");
  }
}

/** Mods a profile needs that the library doesn't have, each with an Install button. */
function missingList(missing) {
  return h("div.missing-list",
    h("h4", `Not in your library (${missing.length})`),
    missing.map((m) => {
      const canGet = m.pageUrl && (m.source === "gta5mods" || m.source === "lcpdfr");
      const btn = h("button.btn.btn-sm", { type: "button", disabled: !canGet || null, title: canGet ? "Opens the mod's download page" : "This one came from a local file" }, ic("download", "ico-sm"), "Install");
      btn.addEventListener("click", async () => {
        btn.disabled = true;
        try {
          await host.call("mod.download", { site: m.source, url: m.pageUrl, title: m.name, version: m.version || null, author: null, image: null });
          toast(`${m.name}: the download page is open.`, "info", 5000);
        } catch (e) {
          btn.disabled = false;
          toastError(e);
        }
      });
      return h("div.missing-row", h("div.grow", h("b", m.name), h("span.dim.small", [m.version ? `v${m.version}` : null, m.source === "lcpdfr" ? "LCPDFR" : m.source === "gta5mods" ? "GTA5-Mods" : "local file"].filter(Boolean).join(" · "))), btn);
    }));
}

export function forgetProfiles() {
  profiles = null;
  changed("library");
}
