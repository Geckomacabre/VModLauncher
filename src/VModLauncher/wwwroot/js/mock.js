// Demo host used only when the page is opened outside the launcher (no chrome.webview).
// It follows docs/bridge.md closely enough to click through every screen. Nothing is real.

const wait = (ms) => new Promise((r) => setTimeout(r, ms));

function art(seed, label) {
  const hues = [[28, 10], [205, 225], [110, 140], [42, 25], [215, 240], [18, 5]];
  const [a, b] = hues[seed % hues.length];
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 600 400"><defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="hsl(${a},55%,42%)"/><stop offset="1" stop-color="hsl(${b},45%,16%)"/></linearGradient></defs><rect width="600" height="400" fill="url(#g)"/><circle cx="${420 - (seed % 5) * 40}" cy="250" r="120" fill="rgba(255,220,160,.25)"/><text x="40" y="350" font-family="Segoe UI" font-weight="800" font-size="64" fill="rgba(255,255,255,.85)">${label}</text></svg>`;
  return "data:image/svg+xml;charset=utf-8," + encodeURIComponent(svg);
}

const G5M_CATS = [["all", "All"], ["scripts", "Scripts"], ["vehicles", "Vehicles"], ["player", "Player"], ["weapons", "Weapons"], ["maps", "Maps"], ["paintjobs", "Paint Jobs"], ["tools", "Tools"], ["misc", "Misc"]];
const G5M_SORTS = [["latest-versions", "Latest versions"], ["latest-uploads", "Latest uploads"], ["most-downloaded", "Most downloaded"], ["most-liked", "Most liked"], ["highest-rated", "Highest rated"]];
const LC_CATS = [["scripts", "Scripts & Plugins"], ["vehiclemodels", "Vehicle Models"], ["vehiclestextures", "Vehicle Skins"], ["character", "Character"], ["audio", "Audio"], ["datafile", "Visual & Data File"], ["misc", "Misc"], ["essential-mods", "Essential Mods"], ["g17media", "LSPDFR"]];
const LC_SORTS = [["updated", "Recently updated"], ["date", "Newest"], ["file_downloads", "Most downloaded"], ["rating", "Highest rated"], ["views", "Most viewed"], ["title", "Title"]];

const NAMES = [
  "Simple Trainer for GTA V", "Realistic Driving V", "Lamborghini Revuelto [Add-On]", "Davis Sheriff Cruiser Pack", "Better Chases+",
  "NaturalVision Remastered", "Heap's Ped Overhaul", "Callouts+ Pack", "Stop The Ped", "Ultimate Backup",
  "Los Santos Tuners Garage", "Menyoo Map: Del Perro Pier", "Wanted Level Overhaul", "Realistic Weapon Sounds", "Map Editor",
  "Police Radio Chatter", "Dispatch Plus", "Neon Nights Visuals", "Crouch and Prone", "Seat Belt Script",
  "Vinewood Billboards", "Grove Street Remaster", "Taxi Driver Jobs", "Better Traffic Lights",
];
const AUTHORS = ["sollaholla", "Rockstar Fan", "Eddlm", "Albo1125", "PNWParksFan", "Razed", "Guadmaz", "OfficerSquare", "BejoIjo", "ikt"];

const games = [
  { id: "legacy|d:\\games\\gtav", name: "GTA V Legacy", edition: "Legacy", store: "Epic", root: "D:\\Games\\GTAV" },
  { id: "enhanced|c:\\program files\\gtavenhanced", name: "GTA V Enhanced", edition: "Enhanced", store: "Epic", root: "C:\\Program Files\\GTAVEnhanced" },
];

const prereqsFor = (edition) => [
  { id: "shv", name: "Script Hook V", state: "Ok", detail: "ScriptHookV.dll found", getUrl: null },
  { id: "asiloader", name: "ASI loader", state: "Ok", detail: edition === "Enhanced" ? "xinput1_4.dll found" : "dinput8.dll found", getUrl: null },
  { id: "shvdn", name: edition === "Enhanced" ? "Script Hook V .NET Enhanced" : "Script Hook V .NET", state: edition === "Enhanced" ? "Wrong" : "Ok",
    detail: edition === "Enhanced" ? "The Legacy SHVDN is installed; Enhanced needs SHVDNE" : "ScriptHookVDotNet.asi found",
    getUrl: edition === "Enhanced" ? "https://github.com/Chiheb-Bacha/ScriptHookVDotNetEnhanced/releases" : null },
  { id: "modsfolder", name: edition === "Enhanced" ? "mods folder (OpenRPF)" : "mods folder (OpenIV.asi)", state: edition === "Enhanced" ? "Missing" : "Ok",
    detail: edition === "Enhanced" ? "Needed for add-on DLC packs and replace mods" : "OpenIV.asi found", getUrl: edition === "Enhanced" ? "https://github.com/" : null },
  { id: "rph", name: "RAGE Plugin Hook", state: edition === "Enhanced" ? "Missing" : "Ok", detail: edition === "Enhanced" ? "Ships with LSPDFR; runs RPH plugins" : "RAGEPluginHook.exe found", getUrl: "https://www.lcpdfr.com/downloads/gta5mods/g17media/7792-lspd-first-response/" },
  { id: "lspdfr", name: "LSPDFR", state: edition === "Enhanced" ? "Missing" : "Ok", detail: edition === "Enhanced" ? "Needed by LSPDFR callouts and plugins" : "plugins\\LSPD First Response.dll found", getUrl: "https://www.lcpdfr.com/downloads/gta5mods/g17media/7792-lspd-first-response/" },
  { id: "els", name: "ELS", state: "Missing", detail: "Only needed by ELS emergency vehicles", getUrl: "https://www.lcpdfr.com/downloads/gta5mods/scripts/13865-emergency-lighting-system/" },
  { id: "menyoo", name: "Menyoo", state: edition === "Enhanced" ? "Ok" : "Missing", detail: edition === "Enhanced" ? "Menyoo.asi found" : "Only needed by Menyoo maps and outfits", getUrl: "https://www.gta5-mods.com/scripts/menyoo-pc-sp" },
];

export function createMock(emit) {
  let selected = games[0].id;
  let settings = { keepDownloads: true, confirmUninstall: true, defaultSite: "gta5mods", accent: "green" };
  const modsEnabled = { [games[0].id]: true, [games[1].id]: true };
  const library = {
    [games[0].id]: [
      { id: "a1b2c3d4e5f6", name: "Simple Trainer for GTA V", source: "gta5mods", pageUrl: "https://www.gta5-mods.com/scripts/simple-trainer-for-gtav", version: "3.4", author: "sollaholla", imageUrl: art(0, "TRAINER"), summary: "ASI plugin", installedAt: new Date(Date.now() - 86400000 * 3).toISOString(), enabled: true, changeCount: 4, canReEnable: true,
        kind: "script", spawnNames: [], conflicts: [{ modId: "c3d4e5f6a1b2", modName: "Lamborghini Revuelto [Add-On]", paths: ["TrainerV.ini"] }] },
      { id: "c3d4e5f6a1b2", name: "Lamborghini Revuelto [Add-On]", source: "gta5mods", pageUrl: "https://www.gta5-mods.com/vehicles/lamborghini-revuelto-add-on", version: "1.1", author: "Razed", imageUrl: art(2, "REVUELTO"), summary: "Add-on DLC pack", installedAt: new Date(Date.now() - 86400000 * 1).toISOString(), enabled: true, changeCount: 3, canReEnable: true,
        kind: "vehicle", spawnNames: ["revuelto", "revuelto_widebody"], conflicts: [{ modId: "a1b2c3d4e5f6", modName: "Simple Trainer for GTA V", paths: ["TrainerV.ini"] }] },
      { id: "b2c3d4e5f6a1", name: "Davis Sheriff Cruiser Pack", source: "lcpdfr", pageUrl: "https://www.lcpdfr.com/downloads/gta5mods/vehiclemodels/12345-davis-sheriff-cruiser-pack/", version: "1.2", author: "PNWParksFan", imageUrl: art(3, "POLICE"), summary: "Add-on vehicle (ELS)", installedAt: new Date(Date.now() - 86400000 * 12).toISOString(), enabled: false, changeCount: 0, canReEnable: true,
        kind: "vehicle", spawnNames: ["sheriff3", "sheriff4", "sheriffsuv2"], conflicts: [], needsCleanup: true },
    ],
    [games[1].id]: [],
  };
  const plans = new Map();
  let planSeq = 0;

  const game = () => games.find((g) => g.id === selected);
  const status = () => {
    const g = game();
    return { ...g, running: false, writable: g.edition !== "Enhanced", modsEnabled: modsEnabled[g.id], canLspdfr: g.edition === "Legacy", hasOnigiri: g.edition === "Enhanced", prereqs: prereqsFor(g.edition), installedCount: library[g.id].length };
  };

  function item(site, i, category) {
    const name = NAMES[i % NAMES.length] + (i >= NAMES.length ? ` ${Math.floor(i / NAMES.length) + 1}` : "");
    const slug = name.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/-$/, "");
    const editions = i % 3 === 0 ? ["legacy", "enhanced"] : i % 3 === 1 ? ["legacy"] : ["enhanced"];
    return {
      site, id: site === "lcpdfr" ? String(40000 + i) : `${category}/${slug}`,
      url: site === "lcpdfr" ? `https://www.lcpdfr.com/downloads/gta5mods/${category}/${40000 + i}-${slug}/` : `https://www.gta5-mods.com/${category === "all" ? "scripts" : category}/${slug}`,
      title: name, author: AUTHORS[i % AUTHORS.length], version: site === "lcpdfr" ? null : `${1 + (i % 4)}.${i % 10}`,
      image: art(i, name.split(" ")[0].toUpperCase().slice(0, 9)),
      downloads: Math.round(1200 * Math.pow(1.6, (i * 7) % 18)), likes: site === "lcpdfr" ? null : 20 + ((i * 37) % 900),
      rating: i % 5 === 4 ? null : 3.5 + ((i * 13) % 15) / 10, category, tags: i % 2 ? ["Gameplay", ".Net"] : ["Asi"],
      editions, updated: new Date(Date.now() - 86400000 * ((i * 5) % 90)).toISOString(), fileSize: site === "lcpdfr" ? `${(2 + (i * 3.7) % 400).toFixed(1)} MB` : null,
      blurb: site === "lcpdfr" ? "A short description from the listing." : null,
      installed: i === 0 ? { id: "a1b2c3d4e5f6", version: "3.4", enabled: true } : null,
    };
  }

  function makePlan(key, meta) {
    const planId = "p" + (++planSeq);
    const g = game();
    const plan = {
      planId, key, modName: meta.title, title: meta.title, image: meta.image || null, version: meta.version || null, author: meta.author || null,
      site: meta.site || "local", url: meta.url || null, summary: "Add-on DLC pack", gameName: g.name, needsScan: true,
      variants: [{ id: "dir~Car", title: "Choose a version (Car)", selected: "Add-On", options: [{ id: "Add-On", label: "Add-On", recommended: true }, { id: "Replace", label: "Replace", recommended: false }] }],
      warnings: [{ level: "Info", message: "First add-on pack: the launcher copies update.rpf into the mods folder once (2.0 GB), so the game's own files stay untouched." }],
      requires: prereqsFor(g.edition).filter((p) => p.id === "modsfolder" || p.id === "shv"),
      readmes: ["Car/readme.txt", "Car/Installation.txt"],
      ops: [
        { index: 0, kind: "DlcPack", sourceRel: "Car/Add-On/revuelto/dlc.rpf", target: "revuelto", reason: "Add-on DLC pack \"revuelto\"", enabled: true, variant: "dir~Car:Add-On" },
        { index: 1, kind: "RpfFile", sourceRel: "Car/Replace/adder.yft", target: "x64e.rpf|levels/gta5/vehicles.rpf/adder.yft", reason: "Replaces the game's own file", enabled: true, variant: "dir~Car:Replace" },
        { index: 2, kind: "RpfFile", sourceRel: "Car/Replace/adder.ytd", target: "x64e.rpf|levels/gta5/vehicles.rpf/adder.ytd", reason: "Replaces the game's own file", enabled: true, variant: "dir~Car:Replace" },
        { index: 3, kind: "CopyFile", sourceRel: "Car/ELS/revuelto.xml", target: "ELS\\pack_default\\revuelto.xml", reason: "ELS vehicle config", enabled: true, variant: null },
        { index: 4, kind: "CopyFile", sourceRel: "Car/Optional/Tuning/handling.meta", target: "mods\\handling.meta", reason: "mods folder file - optional extra, off by default", enabled: false, variant: null },
        { index: 5, kind: "Skip", sourceRel: "Car/readme.txt", target: "", reason: "Readme", enabled: false, variant: null },
        { index: 6, kind: "Skip", sourceRel: "Car/screenshot1.jpg", target: "", reason: "Readme / picture", enabled: false, variant: null },
        { index: 7, kind: "Skip", sourceRel: "Car/Extras/RevueltoHorn.dll", target: "", reason: "Not recognised: pick a target if it's needed", enabled: false, variant: null },
        { index: 8, kind: "Skip", sourceRel: "Car/Extras/horn.ini", target: "", reason: "Not recognised: pick a target if it's needed", enabled: false, variant: null },
        { index: 9, kind: "Skip", sourceRel: "Car/Replace/adder_hi.yft", target: "", reason: "Game file meant to replace one inside the game's archives (scan the game to place it)", enabled: false, variant: null },
      ],
    };
    recompute(plan);
    plans.set(planId, plan);
    return plan;
  }

  function recompute(plan) {
    const sel = (tag) => !tag || tag.split("|").every((t) => {
      const i = t.lastIndexOf(":");
      const g = plan.variants.find((v) => v.id === t.slice(0, i));
      return !g || g.selected === t.slice(i + 1);
    });
    for (const op of plan.ops) op.active = op.kind !== "Skip" && op.enabled && sel(op.variant);
    plan.activeCount = plan.ops.filter((o) => o.active).length;
    plan.skippedCount = plan.ops.length - plan.activeCount;
  }

  async function fakeDownload(key, title, then) {
    emit("download.progress", { key, title, received: 0, total: null, state: "waiting", file: null, error: null });
    await wait(1800);
    const total = 48 * 1024 * 1024;
    for (let r = 0; r <= total; r += total / 12) {
      emit("download.progress", { key, title, received: Math.round(r), total, state: "downloading", file: null, error: null });
      await wait(160);
    }
    emit("download.progress", { key, title, received: total, total, state: "done", file: "C:\\Users\\you\\AppData\\Local\\VModLauncher\\downloads\\mod.zip", error: null });
    await wait(500);
    then();
  }

  const fail = (message, code) => { const e = new Error(message); e.code = code; throw e; };

  // ---------------------------------------------------------------- wave 2 demo state
  // Plain dark gradients stand in for the game's art (no images in the repo).
  const shade = (a, b, angle) => "data:image/svg+xml;charset=utf-8," + encodeURIComponent(
    `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 182 102"><defs><linearGradient id="g" gradientTransform="rotate(${angle})"><stop offset="0" stop-color="${a}"/><stop offset="1" stop-color="${b}"/></linearGradient></defs><rect width="182" height="102" fill="url(#g)"/></svg>`);
  const artReady = {
    hero: [shade("#3a2a1a", "#0c0c0c", 20), shade("#1d2b33", "#090909", 45), shade("#2c1f2b", "#0a0a0a", 70), shade("#22301f", "#0b0b0b", 10)],
    panels: [shade("#2a2a2a", "#050505", 0), shade("#302418", "#060606", 90), shade("#1a2430", "#050505", 135)],
    status: "ready",
  };
  let artSent = false;
  const stale = { [games[0].id]: true, [games[1].id]: false };
  const launch = { [games[0].id]: ["-nobattleye"], [games[1].id]: ["-windowed"] };
  const KNOWN_ARGS = [
    { arg: "-nobattleye", label: "Skip BattlEye", help: "Story mode with mods on Legacy needs this. GTA Online doesn't start with it.", editions: ["Legacy"] },
    { arg: "-windowed", label: "Windowed", help: "Starts in a window instead of fullscreen.", editions: [] },
    { arg: "-borderless", label: "Borderless window", help: "A window without a frame, the size of your screen.", editions: [] },
    { arg: "-ignoreDifferentVideoCard", label: "Ignore graphics card changes", help: "Keeps your settings when you switch GPU or driver.", editions: [] },
    { arg: "-safemode", label: "Safe mode", help: "Lowest graphics settings, for when the game won't start.", editions: [] },
  ];
  const profiles = {
    [games[0].id]: [
      { id: "pf1", name: "LSPDFR night", gameId: games[0].id, createdAt: new Date(Date.now() - 86400000 * 9).toISOString(), updatedAt: new Date(Date.now() - 86400000 * 2).toISOString(),
        mods: [{ id: "b2c3d4e5f6a1", name: "Davis Sheriff Cruiser Pack", source: "lcpdfr", pageUrl: "https://www.lcpdfr.com/downloads/gta5mods/vehiclemodels/12345-davis-sheriff-cruiser-pack/", version: "1.2" },
               { id: null, name: "Stop The Ped", source: "lcpdfr", pageUrl: "https://www.lcpdfr.com/downloads/gta5mods/scripts/40008-stop-the-ped/", version: "4.9" }], active: false },
      { id: "pf2", name: "Car meet", gameId: games[0].id, createdAt: new Date(Date.now() - 86400000 * 4).toISOString(), updatedAt: new Date(Date.now() - 86400000 * 4).toISOString(),
        mods: [{ id: "c3d4e5f6a1b2", name: "Lamborghini Revuelto [Add-On]", source: "gta5mods", pageUrl: "https://www.gta5-mods.com/vehicles/lamborghini-revuelto-add-on", version: "1.1" },
               { id: "a1b2c3d4e5f6", name: "Simple Trainer for GTA V", source: "gta5mods", pageUrl: "https://www.gta5-mods.com/scripts/simple-trainer-for-gtav", version: "3.4" }], active: true },
    ],
    [games[1].id]: [],
  };
  let profileSeq = 3;
  const quarantine = { [games[0].id]: [], [games[1].id]: [] };
  const unmanaged = {
    [games[0].id]: [
      { path: "ClearWeather.asi", kind: "asi", size: 84480, modified: new Date(Date.now() - 86400000 * 40).toISOString(), note: null },
      { path: "scripts\\OldRadio.dll", kind: "script", size: 215040, modified: new Date(Date.now() - 86400000 * 120).toISOString(), note: "Throws an error in ScriptHookVDotNet.log" },
      { path: "dsound.dll", kind: "loader", size: 192000, modified: new Date(Date.now() - 86400000 * 200).toISOString(), note: "A second ASI loader: plugins may load twice" },
    ],
    [games[1].id]: [],
  };
  const favorites = new Map();
  const storage = { modsFolder: 2.3e9, backups: 41e6, downloads: 312e6, payloads: 1.1e9, cache: 64e6 };

  const health = () => {
    const g = game();
    const enh = g.edition === "Enhanced";
    return {
      gameVersion: enh ? "1.0.1158.16" : "1.0.3889.0",
      modsUpdateStale: !!stale[g.id],
      staleReason: stale[g.id] ? "GTA V updated on Oct 21; your mods copy of update.rpf is from Oct 2. Add-on packs won't load until it's rebuilt." : null,
      scriptHookV: enh
        ? { installed: true, supportsGame: false, version: "3889.0/1158.13", message: "Script Hook V 1158.13 says your game (1158.16) is unknown. Check dev-c.com for an update." }
        : { installed: true, supportsGame: true, version: "3889.0", message: "Version 3889.0 supports this game version." },
      addonPacks: enh ? 31 : 7,
      packLimitWarning: enh ? "That's a lot of packs with no Packfile Limit Adjuster: the game may crash on load." : null,
      problems: enh
        ? [{ level: "Warn", text: "The game folder isn't writable, so installs will fail.", fix: "game.fixAccess" },
           { level: "Info", text: "ScriptHookVDotNet.log has 2 script errors.", fix: "nav:tools:diagnostics" }]
        : [{ level: "Warn", text: "ScriptHookVDotNet.log has 1 script error from OldRadio.dll.", fix: "nav:tools:diagnostics" }],
    };
  };

  const LOG_TAIL = [
    "[12:01:22] [DEBUG] Loading scripts from scripts\\...",
    "[12:01:22] [INFO] Loaded LemonUI.SHVDN3.dll",
    "[12:01:23] [INFO] Started script 'SimpleTrainer.Main'",
    "[12:01:24] [ERROR] Failed to instantiate script 'OldRadio.Radio' because constructor threw an exception:",
    "System.NullReferenceException: Object reference not set to an instance of an object.",
    "   at OldRadio.Radio..ctor()",
    "[12:01:24] [INFO] Started script 'Revuelto.Horn'",
  ].join("\n");

  const handlers = {
    "app.init": () => ({ version: "0.1.0 (preview)", games, selectedGameId: selected, settings }),
    "games.select": ({ id }) => { selected = id; return status(); },
    "games.addFolder": () => games,
    "games.refresh": () => games,
    "game.status": () => status(),
    "game.play": () => true,
    "game.setModsEnabled": ({ enabled }) => { modsEnabled[selected] = enabled; return status(); },
    "game.fixAccess": async () => { await wait(900); return status(); },
    "game.openFolder": () => true,
    "site.categories": ({ site }) => {
      const [c, s] = site === "lcpdfr" ? [LC_CATS, LC_SORTS] : [G5M_CATS, G5M_SORTS];
      return { categories: c.map(([id, name]) => ({ id, name })), sorts: s.map(([id, name]) => ({ id, name })) };
    },
    "site.list": async ({ site, category, sort, page }) => {
      await wait(500);
      const offset = (page - 1) * 16 + (sort || "").length + (category || "").length;
      return { items: Array.from({ length: 16 }, (_, k) => item(site, offset + k, category)), page, pages: 6, hasMore: page < 6 };
    },
    "site.search": async ({ site, query, page }) => {
      await wait(600);
      if (query.toLowerCase() === "challenge") fail(`CHALLENGE: https://www.lcpdfr.com/search/?q=${query}`, "CHALLENGE");
      const items = Array.from({ length: 24 }, (_, k) => item(site, k, "scripts")).filter((it) => it.title.toLowerCase().includes(query.toLowerCase()));
      return { items, page, pages: 1, hasMore: false, searchPageUrl: site === "gta5mods" ? `https://www.gta5-mods.com/search/${encodeURIComponent(query).replace(/%20/g, "+")}` : null };
    },
    "site.detail": async ({ site, url }) => {
      await wait(450);
      const i = Math.abs([...url].reduce((a, c) => (a * 31 + c.charCodeAt(0)) | 0, 7)) % 40;
      const base = item(site, i, "scripts");
      // keep the card's own title so the preview reads naturally
      const slug = url.replace(/\/$/, "").split("/").pop().replace(/^\d+-/, "");
      const title = NAMES.find((n) => n.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/-$/, "") === slug.replace(/-\d+$/, "")) || base.title;
      return {
        ...base, url, title,
        descriptionHtml: `<p><strong>${base.title}</strong> brings a fresh take on Los Santos. Works with both editions where noted.</p><h3>Features</h3><ul><li>Configurable through an .ini file</li><li>Press <code>F7</code> to open the menu</li><li>No performance hit</li></ul><p>Requirements: <a href="https://www.dev-c.com/gtav/scripthookv/">Script Hook V</a> and <a href="https://www.gta5-mods.com/tools/scripthookv-net">Script Hook V .NET</a>.</p><blockquote>Not for GTA Online.</blockquote>`,
        images: [art(i, "SHOT 1"), art(i + 1, "SHOT 2"), art(i + 2, "SHOT 3"), art(i + 3, "SHOT 4")],
        downloadPageUrl: site === "lcpdfr" ? url + "?do=download" : url + "/download/12345",
        versions: [{ version: base.version || "2.0", date: new Date().toISOString() }, { version: "1.0", date: new Date(Date.now() - 86400000 * 200).toISOString() }],
        requirementsText: site === "lcpdfr" ? "Requires: RAGE Plugin Hook\nScript/Plugin Type: LSPDFR plugin" : null,
        uploaded: new Date(Date.now() - 86400000 * 300).toISOString(), comments: 128, loginRequired: site === "lcpdfr",
      };
    },
    "site.open": () => true,
    "shell.openUrl": () => true,
    "mod.download": ({ url, title, version, author, image, site }) => {
      fakeDownload(url, title, () => emit("plan.ready", makePlan(url, { title, version, author, image, site, url })));
      return true;
    },
    "mod.importFile": () => {
      setTimeout(() => emit("plan.ready", makePlan("local", { title: "My Local Mod", site: "local" })), 400);
      return true;
    },
    "mod.importDropped": () => {
      setTimeout(() => emit("plan.ready", makePlan("local", { title: "Dropped Mod", site: "local" })), 400);
      return true;
    },
    "plan.get": ({ planId }) => plans.get(planId),
    "plan.select": ({ planId, groupId, optionId }) => {
      const p = plans.get(planId);
      p.variants.find((v) => v.id === groupId).selected = optionId;
      recompute(p);
      return p;
    },
    "plan.toggleOp": ({ planId, index, enabled }) => {
      const p = plans.get(planId);
      p.ops[index].enabled = enabled;
      recompute(p);
      return p;
    },
    "plan.locate": async ({ planId }) => {
      const p = plans.get(planId);
      for (let k = 0; k <= 10; k++) {
        emit("install.progress", { planId, stage: "Scanning your game's archives", fraction: k / 10, detail: `x64${"abcdefghij"[k % 10]}.rpf` });
        await wait(150);
      }
      p.needsScan = false;
      return p;
    },
    "plan.readme": ({ rel }) => ({ rel, text: `${rel}\n\nINSTALLATION\n------------\n1. Copy the folder into mods\\update\\x64\\dlcpacks\n2. Add dlcpacks:/revuelto/ to dlclist.xml\n\n(The launcher does both for you.)\n` }),
    "plan.install": async ({ planId }) => {
      const p = plans.get(planId);
      if (status().running) fail("GTA V is running. Close the game first.", "BLOCKED");
      const steps = ["Copying update.rpf into mods", "Installing", "Installing", "Installing"];
      for (let k = 0; k <= 20; k++) {
        emit("install.progress", { planId, stage: steps[Math.min(3, Math.floor(k / 6))], fraction: k / 20, detail: p.ops[k % p.ops.length].sourceRel });
        await wait(110);
      }
      const mod = { id: Math.random().toString(16).slice(2, 14), name: p.title, source: p.site, pageUrl: p.url, version: p.version, author: p.author, imageUrl: p.image, summary: p.summary, installedAt: new Date().toISOString(), enabled: true, changeCount: p.activeCount + 2, canReEnable: true };
      library[selected].unshift(mod);
      plans.delete(planId);
      setTimeout(() => { emit("game.changed", status()); emit("library.changed", { gameId: selected }); }, 50);
      return mod;
    },
    "plan.cancel": ({ planId }) => { plans.delete(planId); return true; },
    "library.list": () => library[selected],
    "library.setEnabled": async ({ id, enabled }) => {
      await wait(600);
      const m = library[selected].find((x) => x.id === id);
      m.enabled = enabled;
      m.changeCount = enabled ? 5 : 0;
      return m;
    },
    "library.uninstall": async ({ id }) => {
      await wait(500);
      const target = library[selected].find((x) => x.id === id);
      if (target && target.needsCleanup) { target.needsCleanup = false; target.changeCount = 0; target.enabled = false; return { problems: [] }; }
      library[selected] = library[selected].filter((x) => x.id !== id);
      setTimeout(() => emit("game.changed", status()), 50);
      return { problems: [] };
    },
    "library.checkUpdates": async () => {
      await wait(1200);
      return library[selected].filter((m) => m.pageUrl).map((m, i) => i === 0
        ? { id: m.id, latestVersion: "3.5", hasUpdate: true }
        : { id: m.id, latestVersion: m.version, hasUpdate: false });
    },
    "library.files": ({ id }) => ({
      entries: library[selected].find((x) => x.id === id).enabled
        ? [
            { type: "file", path: "TrainerV.asi", inner: null, item: null },
            { type: "file", path: "TrainerV.ini", inner: null, item: null },
            { type: "dir", path: "scripts\\TrainerV", inner: null, item: null },
            { type: "dlc-item", path: "mods\\update\\update.rpf", inner: null, item: "dlcpacks:/revuelto/" },
            { type: "rpf-entry", path: "mods\\x64e.rpf", inner: "levels/gta5/vehicles.rpf/adder.yft", item: null },
          ]
        : [],
    }),
    "settings.set": (patch) => { settings = { ...settings, ...patch }; return settings; },

    // ---------------------------------------------------------------- wave 2
    "art.list": () => {
      if (artSent) return artReady;
      artSent = true;
      setTimeout(() => emit("art.ready", artReady), 900);
      return { hero: [], panels: [], status: "extracting" };
    },
    "game.health": async () => { await wait(300); return health(); },
    "game.repair": async () => {
      const steps = ["Keeping the old copy as a backup", "Copying the game's update.rpf into mods", "Putting dlclist entries back", "Putting edited files back"];
      for (let k = 0; k <= 16; k++) {
        emit("install.progress", { planId: "repair", stage: "Rebuilding your mods copy of update.rpf", fraction: k / 16, detail: steps[Math.min(3, Math.floor(k / 4.5))] });
        await wait(120);
      }
      stale[selected] = false;
      setTimeout(() => { emit("game.changed", status()); emit("library.changed", { gameId: selected }); }, 30);
      return { health: health(), rebuilt: true, restoredItems: 7, restoredEntries: 3, backup: "mods\\update\\update.rpf.before-repair-20261022", problems: [] };
    },
    "profiles.list": () => profiles[selected].map((p) => ({ ...p })),
    "profiles.save": ({ name, id }) => {
      const mods = library[selected].filter((m) => m.enabled).map((m) => ({ id: m.id, name: m.name, source: m.source, pageUrl: m.pageUrl, version: m.version }));
      const now = new Date().toISOString();
      let p = id && profiles[selected].find((x) => x.id === id);
      if (p) { p.mods = mods; p.updatedAt = now; if (name) p.name = name; }
      else { p = { id: "pf" + (profileSeq++), name, gameId: selected, createdAt: now, updatedAt: now, mods, active: false }; profiles[selected].push(p); }
      return p;
    },
    "profiles.apply": async ({ id }) => {
      const p = profiles[selected].find((x) => x.id === id) || fail("That profile doesn't exist any more.", "ERROR");
      const want = new Set(p.mods.map((m) => m.id).filter(Boolean));
      const enabled = [], disabled = [];
      for (const [k, m] of library[selected].entries()) {
        emit("install.progress", { planId: "profile", stage: `Switching to ${p.name}`, fraction: k / Math.max(1, library[selected].length), detail: m.name });
        await wait(200);
        if (want.has(m.id) && !m.enabled) { m.enabled = true; enabled.push(m.name); }
        if (!want.has(m.id) && m.enabled) { m.enabled = false; disabled.push(m.name); }
      }
      for (const x of profiles[selected]) x.active = x.id === id;
      const missing = p.mods.filter((m) => !m.id || !library[selected].some((l) => l.id === m.id)).map((m) => ({ name: m.name, source: m.source, pageUrl: m.pageUrl, version: m.version }));
      setTimeout(() => { emit("library.changed", { gameId: selected }); emit("toast", { kind: "ok", text: `Switched to ${p.name}.` }); }, 30);
      return { enabled, disabled, problems: [], missing };
    },
    "profiles.delete": ({ id }) => { profiles[selected] = profiles[selected].filter((p) => p.id !== id); return true; },
    "profiles.export": ({ id }) => {
      const p = profiles[selected].find((x) => x.id === id);
      return { code: "VML1:" + btoa(unescape(encodeURIComponent(JSON.stringify({ n: p.name, e: game().edition, m: p.mods.map((m) => [m.name, m.source, m.pageUrl, m.version]) })))).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "") };
    },
    "profiles.import": ({ code }) => {
      if (!/^VML1:/.test(code || "")) fail("That isn't a profile code (they start with VML1:).", "ERROR");
      const now = new Date().toISOString();
      const p = { id: "pf" + (profileSeq++), name: "Imported profile", gameId: selected, createdAt: now, updatedAt: now, active: false,
        mods: [{ id: null, name: "Ultimate Backup", source: "lcpdfr", pageUrl: "https://www.lcpdfr.com/downloads/gta5mods/scripts/40011-ultimate-backup/", version: "1.8" },
               { id: "a1b2c3d4e5f6", name: "Simple Trainer for GTA V", source: "gta5mods", pageUrl: "https://www.gta5-mods.com/scripts/simple-trainer-for-gtav", version: "3.4" }] };
      profiles[selected].push(p);
      return { profile: p, missing: [{ name: "Ultimate Backup", source: "lcpdfr", pageUrl: p.mods[0].pageUrl, version: "1.8" }] };
    },
    "diag.report": async () => {
      await wait(500);
      const g = game();
      return {
        logs: [
          { name: "ScriptHookV.log", path: g.root + "\\ScriptHookV.log", exists: true, modified: new Date(Date.now() - 3600e3).toISOString(), size: 6144, tail: "[12:00:58] INIT: Started\n[12:00:58] INIT: Game version VER_1_0_3889_0\n[12:01:20] CORE: Loading ASI plugins\n[12:01:21] CORE: Pool 1 extended", problems: [] },
          { name: "ScriptHookVDotNet.log", path: g.root + "\\ScriptHookVDotNet.log", exists: true, modified: new Date(Date.now() - 3600e3).toISOString(), size: 18432, tail: LOG_TAIL,
            problems: [{ level: "Error", text: "Failed to instantiate script 'OldRadio.Radio' because constructor threw an exception:", line: 4 }] },
          { name: "asiloader.log", path: g.root + "\\asiloader.log", exists: true, modified: new Date(Date.now() - 3600e3).toISOString(), size: 512, tail: "Loaded ScriptHookVDotNet.asi\nLoaded ClearWeather.asi\nLoaded OpenIV.asi", problems: [] },
          { name: "RagePluginHook.log", path: g.root + "\\RagePluginHook.log", exists: g.edition === "Legacy", modified: null, size: 0, tail: "", problems: [] },
          { name: "ELS.log", path: g.root + "\\ELS.log", exists: false, modified: null, size: 0, tail: "", problems: [] },
        ],
        crashes: [{ path: "C:\\Users\\you\\AppData\\Local\\Rockstar Games\\GTAV\\CrashLogs\\crash-2026-10-21.dmp", time: new Date(Date.now() - 86400000).toISOString(), size: 1.8e6 },
                  { path: "C:\\Users\\you\\AppData\\Local\\Rockstar Games\\GTAV\\CrashLogs\\crashcontext.log", time: new Date(Date.now() - 86400000).toISOString(), size: 21000 }],
        unmanaged: unmanaged[selected],
      };
    },
    "diag.openLog": () => true,
    "diag.quarantine": ({ path }) => {
      const u = unmanaged[selected].find((x) => x.path === path) || fail("Refresh Diagnostics first.", "ERROR");
      unmanaged[selected] = unmanaged[selected].filter((x) => x !== u);
      const item = { id: "q" + Date.now(), path, movedAt: new Date().toISOString(), _u: u };
      quarantine[selected].push(item);
      return { quarantined: { id: item.id, path, movedAt: item.movedAt } };
    },
    "diag.quarantineList": () => quarantine[selected].map(({ id, path, movedAt }) => ({ id, path, movedAt })),
    "diag.restore": ({ id }) => {
      const q = quarantine[selected].find((x) => x.id === id) || fail("Not in quarantine.", "ERROR");
      quarantine[selected] = quarantine[selected].filter((x) => x !== q);
      unmanaged[selected].push(q._u);
      return true;
    },
    "game.launchOptions": () => ({ file: game().edition === "Enhanced" ? "commandline.txt" : "args.txt", args: launch[selected], known: KNOWN_ARGS }),
    "game.setLaunchOptions": ({ args }) => { launch[selected] = args || []; return { file: game().edition === "Enhanced" ? "commandline.txt" : "args.txt", args: launch[selected], known: KNOWN_ARGS }; },
    "frameworks.list": async () => {
      await wait(400);
      return [
        { id: "shvdn", name: "Script Hook V .NET (nightly)", installedVersion: game().edition === "Legacy" ? "3.7.0-nightly.190" : null, latestVersion: "3.7.0-nightly.193", releaseUrl: "https://github.com/scripthookvdotnet/scripthookvdotnet-nightly/releases", forEdition: "Legacy", canInstall: true },
        { id: "shvdne", name: "Script Hook V .NET Enhanced", installedVersion: null, latestVersion: "1.1.0.6", releaseUrl: "https://github.com/Chiheb-Bacha/ScriptHookVDotNetEnhanced/releases", forEdition: "Enhanced", canInstall: true },
        { id: "lemonui", name: "LemonUI", installedVersion: "2.2", latestVersion: "2.2", releaseUrl: "https://github.com/LemonUIbyLemon/LemonUI/releases", forEdition: "Both", canInstall: true },
        { id: "ragenativeui", name: "RAGENativeUI", installedVersion: null, latestVersion: "1.9.3", releaseUrl: "https://github.com/alexguirre/RAGENativeUI/releases", forEdition: "Both", canInstall: true },
      ];
    },
    "frameworks.install": ({ id }) => {
      fakeDownload(id, id, () => emit("plan.ready", makePlan(id, { title: id === "lemonui" ? "LemonUI" : id, site: "local" })));
      return true;
    },
    "favorites.list": () => [...favorites.values()],
    "favorites.toggle": ({ item }) => {
      if (favorites.has(item.url)) { favorites.delete(item.url); return { favorite: false }; }
      favorites.set(item.url, item);
      return { favorite: true };
    },
    // ---------------------------------------------------------------- texture optimizer (wave 3)
    "tex.scan": async ({ maxSize = 2048, quiet = false }) => {
      const big = (ytd, texture, size, h, n = 1) => Array.from({ length: n }, (_, i) => ({ ytd, texture: n > 1 ? `${texture}_${i + 1}` : texture, width: size, height: h, format: "DXT5" }));
      const defs = game().edition === "Enhanced"
        ? [{ name: "lwedesertmedium", textures: 812, bytes: 690e6, big: [], skipped: "Enhanced-format (gen9) textures: the launcher can't shrink these yet." }]
        : [{ name: "revuelto", owner: "c3d4e5f6a1b2", textures: 64, bytes: 182e6, big: [...big("revuelto.ytd", "revuelto_livery", 8192, 8192), ...big("revuelto_hi.ytd", "revuelto_badges", 4096, 4096), ...big("revuelto.ytd", "revuelto_interior", 4096, 2048, 3)] },
           { name: "sheriffpack", owner: "b2c3d4e5f6a1", textures: 210, bytes: 96e6, big: big("sheriff3+hi.ytd", "sheriff3_sign", 4096, 1024, 4) },
           { name: "vihud", textures: 1356, bytes: 88.6e6, big: big("vihud_minimap.ytd", "minimap_tile", 2048, 2048, 7) },
           { name: "weaponsounds", textures: 0, bytes: 0, big: [], skipped: "No textures in this pack." }];
      const packs = [];
      for (const [i, d] of defs.entries()) {
        if (!quiet) { emit("install.progress", { planId: "textures", stage: "Reading your add-on packs", fraction: i / defs.length, detail: d.name }); await wait(250); }
        const rec = library[selected].find((m) => m.source === "tool" && m.name === "Smaller textures: " + d.name);
        const done = rec ? parseInt(rec.version, 10) : 0;
        const tex = d.big.map((t) => { const k = done && Math.max(t.width, t.height) > done ? Math.log2(Math.max(t.width, t.height) / done) : 0; return { ...t, width: t.width >> k, height: t.height >> k }; });
        const bytesOf = (t) => t.width * t.height * 4 / 3;
        const over = tex.filter((t) => Math.max(t.width, t.height) > maxSize);
        const savable = over.reduce((s, t) => s + bytesOf(t) * (1 - (maxSize / Math.max(t.width, t.height)) ** 2), 0);
        const owner = d.owner && library[selected].find((m) => m.id === d.owner);
        packs.push({ id: `mods\\update\\x64\\dlcpacks\\${d.name}\\dlc.rpf`, name: d.name, owner: owner ? { modId: owner.id, name: owner.name } : null,
          textures: d.textures, oversized: over.length, kept: 0, keptReasons: [], bytes: Math.round(d.bytes - d.big.reduce((s, t, j) => s + bytesOf(t) - bytesOf(tex[j]), 0)), savableBytes: Math.round(savable),
          alreadyOptimized: rec ? { modId: rec.id, maxSize: done } : null, skipped: d.skipped || null,
          worst: [...over].sort((a, b) => b.width * b.height - a.width * a.height).slice(0, 5) });
      }
      packs.sort((a, b) => b.savableBytes - a.savableBytes);
      return { maxSize, packs, totalBytes: packs.reduce((s, p) => s + p.bytes, 0), savableBytes: packs.reduce((s, p) => s + p.savableBytes, 0) };
    },
    "tex.optimize": async ({ packs, maxSize = 2048 }) => {
      if (!packs || !packs.length) fail("Pick at least one pack.", "ERROR");
      const scan = await handlers["tex.scan"]({ maxSize, quiet: true });
      const optimized = [], problems = [];
      for (const [i, id] of packs.entries()) {
        const p = scan.packs.find((x) => x.id === id);
        emit("install.progress", { planId: "textures", stage: `Shrinking ${p ? p.name : id}`, fraction: i / packs.length, detail: null });
        await wait(600);
        if (!p) { problems.push(`${id} isn't an add-on pack the game loads.`); continue; }
        if (p.skipped) { problems.push(`${p.name}: ${p.skipped}`); continue; }
        if (!p.savableBytes) { problems.push(`${p.name}: nothing to shrink at ${maxSize} px.`); continue; }
        library[selected] = library[selected].filter((m) => m.name !== "Smaller textures: " + p.name);
        const mod = { id: Math.random().toString(16).slice(2, 14), name: "Smaller textures: " + p.name, source: "tool", pageUrl: null, version: `${maxSize} px`, author: "V Mod Launcher", imageUrl: null,
          summary: `Texture optimization (max ${maxSize} px)`, installedAt: new Date().toISOString(), enabled: true, changeCount: p.oversized, canReEnable: true, kind: "other", spawnNames: [], conflicts: [] };
        library[selected].unshift(mod);
        optimized.push({ id: p.id, modId: mod.id, savedBytes: p.savableBytes, textures: p.oversized });
      }
      setTimeout(() => { emit("library.changed", { gameId: selected }); emit("game.changed", status()); }, 30);
      return { optimized, problems };
    },
    "storage.report": async () => { await wait(250); return { ...storage }; },
    "storage.clean": async ({ what }) => {
      await wait(400);
      if (what === "downloads") storage.downloads = 0;
      if (what === "cache") storage.cache = 0;
      return { ...storage };
    },
    "plan.setTarget": ({ planId, index, folder, wholeFolder }) => {
      const p = plans.get(planId);
      const op = p.ops[index];
      if (!op || op.kind !== "Skip") fail("That file can't be placed.", "ERROR");
      if (/[:*?"<>|]/.test(folder || "") || /\.\./.test(folder || "")) fail("That folder name isn't allowed.", "ERROR");
      const dir = op.sourceRel.includes("/") ? op.sourceRel.slice(0, op.sourceRel.lastIndexOf("/")) : "";
      const placeOp = (o) => {
        const name = o.sourceRel.slice(dir.length ? dir.length + 1 : 0).replace(/\//g, "\\");
        o.kind = "CopyFile";
        o.target = (folder ? folder + "\\" : "") + name;
        o.reason = "Placed by you";
        o.enabled = true;
      };
      placeOp(op);
      if (wholeFolder) for (const o of p.ops) if (o !== op && o.kind === "Skip" && /^(Not recognised|Game file)/.test(o.reason) && dir && o.sourceRel.startsWith(dir + "/")) placeOp(o);
      recompute(p);
      return p;
    },
    // wave 3: add-on pack builder (the folder's loose car files come back as one built pack)
    "packbuild.fromFolder": () => {
      setTimeout(() => {
        const p = makePlan("local", { title: "Loose Add-on Car", site: "local" });
        p.summary = "Add-on vehicle"; p.variants = []; p.readmes = [];
        p.ops = [
          { index: 0, kind: "DlcPack", sourceRel: "~vml_built/mycar/dlc.rpf", target: "mycar", reason: "Built from the mod's loose files (add-on car: mycar)", enabled: true, variant: null },
          ...["mycar.yft", "mycar_hi.yft", "mycar.ytd", "vehicles.meta", "handling.meta", "carvariations.meta"].map((f, i) =>
            ({ index: i + 1, kind: "Skip", sourceRel: "Loose Add-on Car/" + f, target: "", reason: "Packed into mycar", enabled: false, variant: null })),
        ];
        recompute(p);
        emit("plan.ready", p);
      }, 400);
      return true;
    },
    // wave 3: pack manager and game file checker (Legacy: the mods list; Enhanced: NaturalVision's onigiri list)
    ...(() => {
      const rockstarPacks = ["mpbeach", "mpbusiness", "mpchristmas", "mpvalentines", "mpbusiness2", "mphipster", "mpindependence", "mppilot", "mplts", "mpchristmas2", "mpheist", "mpluxe", "mpchristmas3", "mpsum2", "mpchristmas3_g9ec", "mp2023_01", "mp2025_01"];
      const packs = {};
      const files = {};
      const rows = () => packs[selected] ??= [
        ...(game().edition === "Legacy" ? [
          { name: "revuelto", item: "dlcpacks:/revuelto/", folder: "mods\\update\\x64\\dlcpacks\\revuelto", sizeBytes: 48234496, status: "duplicate", source: "launcher", owner: { modId: "c3d4e5f6a1b2", name: "Lamborghini Revuelto [Add-On]" }, spawnNames: ["revuelto", "revuelto_widebody"] },
          { name: "sheriffpack", item: "dlcpacks:/sheriffpack/", folder: "mods\\update\\x64\\dlcpacks\\sheriffpack", sizeBytes: 91226112, status: "disabled", source: "launcher", owner: { modId: "b2c3d4e5f6a1", name: "Davis Sheriff Cruiser Pack" }, spawnNames: ["sheriff3", "sheriff4", "sheriffsuv2"] },
          { name: "vihudregtest", item: "dlcpacks:/vihudregtest/", folder: null, sizeBytes: 0, status: "missing", source: "other", owner: null, spawnNames: [] },
          { name: "jasonped", item: "dlcpacks:/jasonped/", folder: "mods\\update\\x64\\dlcpacks\\jasonped", sizeBytes: 15925248, status: "notRegistered", source: "other", owner: null, spawnNames: [] },
        ] : [
          { name: "vihud", item: "dlcpacks:/vihud/", folder: "mods\\update\\x64\\dlcpacks\\vihud", sizeBytes: 3145728, status: "notInActiveList", source: "other", owner: null, spawnNames: [] },
          { name: "nve", item: "dlcpacks:/nve/", folder: "onigiri\\dlcpacks\\nve", sizeBytes: 2147483648, status: "ok", source: "other", owner: null, spawnNames: [] },
        ]),
        ...rockstarPacks.map((n) => ({ name: n, item: `dlcpacks:/${n}/`, folder: `update\\x64\\dlcpacks\\${n}`, sizeBytes: 20000000 + n.length * 1234567, status: "ok", source: "rockstar", owner: null, spawnNames: [] })),
      ];
      const report = () => {
        const list = rows();
        const onigiri = game().edition === "Enhanced";
        const problems = [];
        const dropped = list.filter((r) => r.status === "notInActiveList");
        if (dropped.length) problems.push({ level: "Block", text: `NaturalVision's own dlclist is the one the game reads, and ${dropped.length} pack${dropped.length === 1 ? "" : "s"} from update.rpf's list ${dropped.length === 1 ? "isn't" : "aren't"} in it (${dropped.map((r) => r.name).join(", ")}). They don't load until they're added there.`, fix: "onigiri-sync" });
        for (const r of list.filter((x) => x.status === "missing")) problems.push({ level: "Warn", text: `${r.name} is listed but its files are gone, so it doesn't load. Take it off the list to tidy it up.`, fix: "missing:" + r.item });
        const dupes = list.filter((r) => r.status === "duplicate");
        if (dupes.length) problems.push({ level: "Warn", text: `${dupes.length} pack${dupes.length === 1 ? " is" : "s are"} listed more than once (${dupes.map((r) => r.name).join(", ")}). The extra lines can be removed; the first one stays where it is.`, fix: "duplicates" });
        for (const r of list.filter((x) => x.status === "notRegistered")) problems.push({ level: "Warn", text: `${r.name} is in mods\\update\\x64\\dlcpacks but no list loads it, so it never shows up in the game.`, fix: "register:" + r.name });
        return {
          list: onigiri ? { kind: "onigiri", path: "onigiri\\common\\data\\dlclist.xml" } : { kind: "mods", path: "mods\\update\\update.rpf" },
          packs: [...list.filter((r) => r.source !== "rockstar").sort((a, b) => (a.status === "ok") - (b.status === "ok") || a.name.localeCompare(b.name)), ...list.filter((r) => r.source === "rockstar")],
          problems,
        };
      };
      const changedPacks = () => setTimeout(() => emit("library.changed", { gameId: selected }), 20);
      const archives = () => files[selected] ??= [
        ...["common.rpf", "x64a.rpf", "x64b.rpf", "x64c.rpf", "x64d.rpf", "x64e.rpf", "x64f.rpf", "x64g.rpf", "x64h.rpf", "x64i.rpf", "x64j.rpf", "x64k.rpf", "x64l.rpf", "x64m.rpf", "x64n.rpf", "x64o.rpf", "x64p.rpf", "x64q.rpf", "x64r.rpf", "x64s.rpf", "x64t.rpf", "x64u.rpf", "x64v.rpf", "x64w.rpf"]
          .map((p) => ({ path: p, state: game().edition === "Legacy" && p === "x64e.rpf" ? "edited" : "original", size: 1500000000 + p.charCodeAt(4) * 1000000 })),
        { path: "update\\update.rpf", state: "original", size: 1092616192 },
        ...rockstarPacks.map((n) => ({ path: `update\\x64\\dlcpacks\\${n}\\dlc.rpf`, state: "original", size: 20000000 + n.length * 1234567 })),
      ];
      const check = () => {
        const list = archives();
        const strays = game().edition === "Legacy" ? [{ path: "update\\x64\\dlcpacks\\oldcar", name: "oldcar" }] : [];
        const edited = list.filter((a) => a.state === "edited");
        const advice = [];
        if (edited.length) {
          advice.push(`${edited.length} of the game's own archive${edited.length === 1 ? " was" : "s were"} edited in place, so the next game update or a file check in your store undoes those edits (and an update can crash a game with half-old files).`);
          advice.push(edited.every((a) => a._copied) ? "Each one already has a copy in the mods folder, which is what the game loads with OpenIV.asi / OpenRPF. Restore the originals through your store." : "Move the edits into the mods folder first (they keep working from there), then restore the originals through your store.");
        }
        if (strays.length) advice.push(`${strays.length} mod pack${strays.length === 1 ? " is" : "s are"} in the game's own update\\x64\\dlcpacks folder. Packs belong in mods\\update\\x64\\dlcpacks, where game updates and file checks leave them alone: copy them there and register them on the Add-on packs tab.`);
        if (!advice.length) advice.push("Every archive is as Rockstar ships it. Mods only live in the mods folder.");
        return { archives: list.map(({ path, state, size }) => ({ path, state, size })), strayPacks: strays, advice };
      };
      return {
        "packs.list": async () => { await wait(300); return report(); },
        "packs.setEnabled": async ({ item, enabled }) => {
          await wait(250);
          const r = rows().find((x) => x.item === item) || fail(`${item} isn't in the list.`, "ERROR");
          if (r.source === "rockstar") fail(`${r.name} is one of Rockstar's own packs. The launcher leaves those alone: switching one off can break story mode saves.`, "BLOCKED");
          r.status = enabled ? "ok" : "disabled";
          changedPacks();
          return report();
        },
        "packs.register": async ({ name }) => {
          await wait(250);
          const r = rows().find((x) => x.name === name && x.status === "notRegistered") || fail(`There's no ${name}\\dlc.rpf in the mods or onigiri pack folders.`, "BLOCKED");
          r.status = "ok";
          changedPacks();
          return report();
        },
        "packs.fix": async ({ fix }) => {
          await wait(350);
          const list = rows();
          if (fix === "duplicates") {
            const d = list.filter((r) => r.status === "duplicate");
            if (!d.length) fail("No pack is listed twice any more.", "BLOCKED");
            d.forEach((r) => { r.status = "ok"; });
          } else if (fix === "onigiri-sync") {
            const d = list.filter((r) => r.status === "notInActiveList");
            if (!d.length) fail("Every pack in update.rpf's list is already in NaturalVision's list.", "BLOCKED");
            d.forEach((r) => { r.status = "ok"; r.folder = `onigiri\\dlcpacks\\${r.name}`; });
          } else if (fix.startsWith("register:")) {
            return handlers["packs.register"]({ name: fix.slice(9) });
          } else if (fix.startsWith("missing:")) {
            const i = list.findIndex((r) => r.item === fix.slice(8) && r.status === "missing");
            if (i < 0) fail(`${fix.slice(8)} isn't a missing pack (its files are there, or it's not listed).`, "BLOCKED");
            list.splice(i, 1);
          } else {
            fail("Unknown fix " + fix, "ERROR");
          }
          changedPacks();
          return report();
        },
        "gamefiles.check": async () => { await wait(700); return check(); },
        "gamefiles.moveToMods": async ({ path }) => {
          await wait(900);
          const a = archives().find((x) => x.path === path && x.state === "edited") || fail("Check the game files again first.", "ERROR");
          const copied = !a._copied;
          a._copied = true;
          setTimeout(() => {
            emit("library.changed", { gameId: selected });
            emit("toast", copied
              ? { kind: "ok", text: `${path} is copied into the mods folder. Now restore the originals through your store.` }
              : { kind: "info", text: `${path} already has a copy in the mods folder.` });
          }, 20);
          return check();
        },
        "gamefiles.verify": ({ open }) => {
          const g = game();
          const how = g.store === "Steam"
            ? "Steam checks the files for you: press the button, or in Steam go to Library, right-click Grand Theft Auto V, Properties, Installed Files, Verify integrity of game files. It puts Rockstar's original files back and leaves the mods folder alone."
            : "In the Epic Games Launcher: Library, the three dots on Grand Theft Auto V" + (g.edition === "Enhanced" ? " Enhanced" : "") + ", Manage, then Verify. It puts Rockstar's original files back and leaves the mods folder alone.";
          const url = g.store === "Steam" ? `steam://validate/${g.edition === "Enhanced" ? 3240220 : 271590}` : null;
          if (open && url) emit("toast", { kind: "info", text: "(demo) Steam would start verifying the game files now." });
          return { how, url };
        },
      };
    })(),
  };

  return {
    async call(cmd, args) {
      const fn = handlers[cmd];
      if (!fn) fail(`Unknown command ${cmd}`, "ERROR");
      await wait(60);
      return fn(args || {});
    },
  };
}
