// Curated starter packs for Browse → Collections.
// Every site URL below was loaded once in a browser on 2026-10-22 and showed the named mod
// (GTA5-Mods and LCPDFR pages read their title; external sites their home page). No download
// buttons were clicked. Items whose page couldn't be confirmed were left out (Heap Limit Adjuster,
// StopThePed, Ultimate Backup, Compulite, QuantV).
//
// kind: "site"      → opens the mod's detail drawer (INSTALL goes through the site's download page)
//       "framework" → frameworks.install (official GitHub release)
//       "link"      → opens the official page in the player's browser
// editions: omit for both; ["Legacy"] / ["Enhanced"] to show only for that game.

export const COLLECTIONS = [
  {
    id: "essentials",
    title: "Essentials",
    blurb: "What nearly every mod setup starts with: the script hooks, a menu library, the mods-folder loader and the limit fixes for lots of add-ons.",
    items: [
      { kind: "link", name: "Script Hook V", why: "Needed by almost every script mod. Updated after each game patch.", url: "http://www.dev-c.com/gtav/scripthookv/" },
      { kind: "framework", name: "Script Hook V .NET", why: "Runs .NET scripts (most script mods).", framework: "shvdn", editions: ["Legacy"] },
      { kind: "framework", name: "Script Hook V .NET Enhanced", why: "Runs .NET scripts on GTA V Enhanced.", framework: "shvdne", editions: ["Enhanced"] },
      { kind: "framework", name: "LemonUI", why: "The menu library many scripts need.", framework: "lemonui" },
      { kind: "link", name: "OpenIV", why: "Its ASI Manager installs OpenIV.asi, which loads your mods folder.", url: "https://openiv.com/", editions: ["Legacy"] },
      { kind: "site", name: "OpenRPF (OpenIV.asi for Enhanced)", why: "Loads the mods folder on GTA V Enhanced.", site: "gta5mods", url: "https://www.gta5-mods.com/tools/openrpf-openiv-asi-for-gta-v-enhanced", editions: ["Enhanced"] },
      { kind: "link", name: "Menyoo", why: "Trainer, outfit and map editor (now on GitHub).", url: "https://github.com/itsjustcurtis/MenyooSP/releases" },
      { kind: "site", name: "Simple Trainer for GTA V", why: "A light, easy trainer.", site: "gta5mods", url: "https://www.gta5-mods.com/scripts/simple-trainer-for-gtav" },
      { kind: "site", name: "Packfile Limit Adjuster", why: "Lets the game load lots of add-on packs.", site: "gta5mods", url: "https://www.gta5-mods.com/scripts/packfile-limit-adjuster" },
      { kind: "site", name: "Gameconfig (Legacy & Enhanced)", why: "Bigger pools so lots of add-on cars don't crash the game.", site: "gta5mods", url: "https://www.gta5-mods.com/misc/gta-5-gameconfig-300-cars" },
    ],
  },
  {
    id: "lspdfr",
    title: "LSPDFR starter",
    blurb: "Play as a cop: LSPDFR itself, the libraries its plugins use, lights, uniforms and a few of the most downloaded callout packs.",
    items: [
      { kind: "site", name: "LSPD First Response", why: "The police mod itself (comes with RAGE Plugin Hook).", site: "lcpdfr", url: "https://www.lcpdfr.com/downloads/gta5mods/g17media/7792-lspd-first-response/", editions: ["Legacy"] },
      { kind: "site", name: "LSPD First Response (Enhanced Preview)", why: "The Enhanced build of LSPDFR.", site: "lcpdfr", url: "https://www.lcpdfr.com/downloads/gta5mods/g17media/52970-lspd-first-response-enhanced-preview/", editions: ["Enhanced"] },
      { kind: "framework", name: "RAGENativeUI", why: "Menu library most LSPDFR plugins need.", framework: "ragenativeui" },
      { kind: "site", name: "Emergency Lighting System (ELS)", why: "Realistic lights for ELS emergency vehicles.", site: "lcpdfr", url: "https://www.lcpdfr.com/downloads/gta5mods/scripts/13865-emergency-lighting-system/" },
      { kind: "site", name: "Emergency Uniforms Pack - Law & Order", why: "Police, fire and EMS uniforms (EUP).", site: "lcpdfr", url: "https://www.lcpdfr.com/downloads/gta5mods/character/8151-emergency-uniforms-pack-law-order/" },
      { kind: "site", name: "SirenSetting Limit Adjuster", why: "Lets many emergency vehicles with custom sirens load.", site: "lcpdfr", url: "https://www.lcpdfr.com/downloads/gta5mods/scripts/28560-sirensetting-limit-adjuster/" },
      { kind: "site", name: "MTFO: Move The F*** Over", why: "Traffic pulls over properly for your lights and siren.", site: "lcpdfr", url: "https://www.lcpdfr.com/downloads/gta5mods/scripts/47768-mtfo-move-the-f-over/" },
      { kind: "site", name: "UnitedCallouts", why: "One of the most downloaded callout packs.", site: "lcpdfr", url: "https://www.lcpdfr.com/downloads/gta5mods/scripts/20730-unitedcallouts/" },
      { kind: "site", name: "MCCallouts", why: "A big, popular callout pack.", site: "lcpdfr", url: "https://www.lcpdfr.com/downloads/gta5mods/scripts/47079-mccallouts/" },
      { kind: "site", name: "pdComp - Police Computer", why: "An in-game police computer for lookups and reports.", site: "lcpdfr", url: "https://www.lcpdfr.com/downloads/gta5mods/scripts/54106-pdcomp-police-computer/" },
      { kind: "site", name: "Lenny's Mod Loader", why: "Loads replace mods without editing archives; many LCPDFR packs use it.", site: "lcpdfr", url: "https://www.lcpdfr.com/downloads/gta5mods/misc/25437-lennys-mod-loader/" },
    ],
  },
  {
    id: "visuals",
    title: "Visuals",
    blurb: "Make Los Santos look better: lighting and weather overhauls and the post-processing tool most of them build on.",
    items: [
      { kind: "site", name: "VisualV", why: "Classic lighting, weather and colour overhaul.", site: "gta5mods", url: "https://www.gta5-mods.com/misc/visualv", editions: ["Legacy"] },
      { kind: "link", name: "NaturalVision (Razed)", why: "The big photo-real overhaul, from its author's own site.", url: "https://www.razedmods.com/" },
      { kind: "link", name: "ReShade", why: "Post-processing injector most visual presets need.", url: "https://reshade.me/" },
    ],
  },
];

/** Links for Settings → Other useful sites (checked 2026-10-22). */
export const USEFUL_SITES = [
  { name: "GTA5-Mods.com", url: "https://www.gta5-mods.com/", note: "The biggest GTA V single-player mod site." },
  { name: "LCPDFR.com", url: "https://www.lcpdfr.com/", note: "LSPDFR and police mods." },
  { name: "Script Hook V (dev-c.com)", url: "http://www.dev-c.com/gtav/scripthookv/", note: "Official Script Hook V downloads." },
  { name: "OpenIV", url: "https://openiv.com/", note: "Archive editor; its ASI Manager installs OpenIV.asi." },
  { name: "Script Hook V .NET (nightly)", url: "https://github.com/scripthookvdotnet/scripthookvdotnet-nightly/releases", note: "GitHub releases, GTA V Legacy." },
  { name: "Script Hook V .NET Enhanced", url: "https://github.com/Chiheb-Bacha/ScriptHookVDotNetEnhanced/releases", note: "GitHub releases, GTA V Enhanced." },
  { name: "LemonUI", url: "https://github.com/LemonUIbyLemon/LemonUI/releases", note: "Menu library for scripts." },
  { name: "RAGENativeUI", url: "https://github.com/alexguirre/RAGENativeUI/releases", note: "Menu library for LSPDFR plugins." },
  { name: "Menyoo", url: "https://github.com/itsjustcurtis/MenyooSP/releases", note: "Trainer and map editor." },
  { name: "CodeWalker", url: "https://github.com/dexyfex/CodeWalker", note: "World viewer and archive editor (the launcher uses its engine)." },
  { name: "Natives reference", url: "https://docs.fivem.net/natives/", note: "Every GTA V script function, for script makers." },
  { name: "ReShade", url: "https://reshade.me/", note: "Post-processing for visual mods." },
  { name: "Razed Mods (NaturalVision)", url: "https://www.razedmods.com/", note: "NaturalVision's official site." },
  { name: "Rockstar Support", url: "https://support.rockstargames.com/", note: "Game updates, launcher and account problems." },
];
