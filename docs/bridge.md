# UI ↔ host bridge

The launcher window is one WebView2 showing `src/VModLauncher/wwwroot/index.html`, served from
`https://app.vml/` (virtual host mapping, so `fetch` and relative URLs work). The C# host and the
page talk through `window.chrome.webview`.

## Requests (page → host)

```js
// page side helper (wwwroot/js/bridge.js)
const reply = await host.call("site.list", { site: "gta5mods", category: "scripts", sort: "most-downloaded", page: 1 });
```

Wire format:
- page posts `{ id: "r12", cmd: "site.list", args: {...} }` with `chrome.webview.postMessage(obj)` (an object, not a string)
- host answers `{ id: "r12", ok: true, result: <any> }` or `{ id: "r12", ok: false, error: "message", code: "CHALLENGE" | "BLOCKED" | "ERROR" }`

Files dropped on the window: the page calls
`chrome.webview.postMessageWithAdditionalObjects({ id, cmd: "mod.importDropped", args: {} }, fileList)`;
the host reads the real paths from the attached `CoreWebView2File` objects and answers like any request.

## Events (host → page)

`{ event: "name", data: {...} }`, delivered with `PostWebMessageAsJson`. Listen with `host.on("name", fn)`.

| event | data |
|---|---|
| `download.progress` | `{ key, title, received, total \| null, state: "waiting" \| "downloading" \| "done" \| "failed" \| "cancelled", file \| null, error \| null }` — `key` is the mod url (or `"local"`) |
| `plan.ready` | `Plan` (below) — after a download finishes or a local file is imported and analysed |
| `plan.failed` | `{ key, title, error }` |
| `install.progress` | `{ planId, stage, fraction \| null, detail \| null }` |
| `game.changed` | `GameStatus` — after play / mods toggle / install / folder fix |
| `library.changed` | `{ gameId }` |
| `toast` | `{ kind: "info" \| "ok" \| "warn" \| "error", text }` |

## Commands

| cmd | args | result |
|---|---|---|
| `app.init` | – | `{ version, games: Game[], selectedGameId \| null, settings: Settings }` |
| `games.select` | `{ id }` | `GameStatus` |
| `games.addFolder` | – | `Game[]` (opens a folder picker; unchanged list if cancelled) |
| `games.refresh` | – | `Game[]` |
| `game.status` | – | `GameStatus` for the selected game |
| `game.play` | `{ mode: "story" \| "lspdfr" }` | `true` (`BLOCKED` if mods are on and the game is already running, etc.) |
| `game.setModsEnabled` | `{ enabled }` | `GameStatus` |
| `game.fixAccess` | – | `GameStatus` (runs an elevated icacls grant; the player sees a UAC prompt) |
| `game.openFolder` | `{ sub?: string }` | `true` |
| `site.categories` | `{ site }` | `{ categories: [{id,name}], sorts: [{id,name}] }` |
| `site.list` | `{ site, category, sort, page }` | `ListResult` |
| `site.search` | `{ site, query, page }` | `ListResult` |
| `site.detail` | `{ site, url }` | `ModDetail` |
| `site.open` | `{ url }` | `true` — shows the page in the site browser window (log in, read comments) |
| `mod.download` | `{ site, url, title, version?, author?, image? }` | `true` — opens the site's real download page in the site browser window; progress arrives as `download.progress`, then `plan.ready` |
| `mod.importFile` | – | `true` or `false` if cancelled (file picker for .zip/.rar/.7z/.oiv/.asi/.dll/.rpf → `plan.ready`) |
| `mod.importDropped` | (files attached) | `true` |
| `plan.get` | `{ planId }` | `Plan` |
| `plan.select` | `{ planId, groupId, optionId }` | `Plan` |
| `plan.toggleOp` | `{ planId, index, enabled }` | `Plan` |
| `plan.readme` | `{ planId, rel }` | `{ rel, text }` (first 200 KB) |
| `plan.locate` | `{ planId }` | `Plan` — when `plan.needsScan` is true: scans the game's archives once (about 30 s, progress as `install.progress` with stage "Scanning your game's archives"), then re-plans so loose replace files land in the copy the game loads |
| `plan.install` | `{ planId }` | `InstalledMod` (progress as `install.progress`; `BLOCKED` with a readable message if the game is running / folder not writable / plan blocked) |
| `plan.cancel` | `{ planId }` | `true` |
| `library.list` | – | `InstalledMod[]` for the selected game, newest first |
| `library.setEnabled` | `{ id, enabled }` | `InstalledMod` |
| `library.uninstall` | `{ id }` | `{ problems: string[] }` |
| `library.checkUpdates` | – | `[{ id, latestVersion \| null, hasUpdate, error? }]` — reads each site mod's page (about a second per mod); "Update" = `mod.download` with the mod's pageUrl |
| `library.files` | `{ id }` | `{ entries: [{ type, path, inner \| null, item \| null }] }` |
| `settings.set` | `Partial<Settings>` | `Settings` |
| `shell.openUrl` | `{ url }` | `true` (default browser) |

## Shapes

```ts
type Game = { id, name: "GTA V Legacy" | "GTA V Enhanced", edition: "Legacy" | "Enhanced", store, root }

type Prereq = { id, name, state: "Ok" | "Missing" | "Wrong", detail, getUrl: string | null }

type GameStatus = Game & {
  running: boolean, writable: boolean, modsEnabled: boolean, canLspdfr: boolean,
  hasOnigiri: boolean, prereqs: Prereq[], installedCount: number
}

type Settings = { keepDownloads: boolean, confirmUninstall: boolean, defaultSite: "gta5mods" | "lcpdfr", accent: string }

// site.* results come straight from the site extractors: see docs/site-extractors.md
type ListResult = { items: ModSummary[], page, pages: number | null, hasMore: boolean }
// ModSummary / ModDetail: site, id, url, title, author, version, image, downloads, likes, rating, category,
// tags[], editions[] ("legacy"/"enhanced"), updated, fileSize, blurb; detail adds descriptionHtml (already
// sanitized), images[], downloadPageUrl, versions[], requirementsText, uploaded, comments, loginRequired.
// The host adds `installed: { id, version, enabled } | null` to each item it recognises from the library.

type PlanOp = {
  index: number, kind: "CopyFile" | "DlcPack" | "RpfFile" | "Oiv" | "SaveGame" | "Skip",
  sourceRel, target, reason, enabled: boolean, active: boolean,   // active = enabled && not Skip && its variant is selected
  variant: string | null
}
type Plan = {
  planId, key, modName, title, image: string | null, version: string | null, author: string | null, site: string | null, url: string | null,
  summary, ops: PlanOp[], variants: [{ id, title, selected, options: [{ id, label, recommended }] }],
  warnings: [{ level: "Info" | "Warn" | "Block", message }],
  requires: Prereq[],              // resolved against the selected game (state shows if it's installed)
  readmes: string[], activeCount: number, skippedCount: number,
  needsScan: boolean,              // loose replace files are waiting for plan.locate
  gameName: string
}

type InstalledMod = {
  id, name, source: "gta5mods" | "lcpdfr" | "local", pageUrl, version, author, imageUrl, summary,
  installedAt: string, enabled: boolean, changeCount: number, canReEnable: boolean
}
```

---

# Wave 2 ("the ultimate tool")

Host side: `art.*` lives in `Host/Bridge.cs` (main thread). Everything else below lives in
`Host/Bridge.Features.cs` (the partial `HandleFeatureAsync`) on top of Core services in
`src/VModLauncher.Core/Features/`. Same request/reply/event rules as above. All commands act on the
selected game unless they say otherwise.

## Game art (real GTA V images)

| cmd | args | result |
|---|---|---|
| `art.list` | – | `{ hero: string[], panels: string[], status: "ready" \| "extracting" \| "unavailable" }` — URLs on `https://art.vml/…` (jpg) pulled from the player's own installed game. `hero` = the full-bleed newswire character artworks (Franklin, Michael, Trevor, heist crews; 1820×1024) for the Home hero; `panels` = the loading-screen paintings (2048×1024, tilted panels with black gutters — the GTA V cover-art look) for page backdrops / empty states. First call may take a few seconds; while extracting the result is `status:"extracting"` with whatever is ready, and an `art.ready` event follows. Never ship Rockstar art in wwwroot. |

Event `art.ready` `{ hero, panels, status }`.

Fonts: Pricedown (GTA V logo font) for the brand, page titles and big headings when it's installed on the PC
(its licence doesn't allow shipping the file, so the launcher doesn't), else Bahnschrift; Bahnschrift (condensed,
uppercase) stays the menu/label font; Segoe UI for body text.

## Profiles (ReSkate-style mod sets)

| cmd | args | result |
|---|---|---|
| `profiles.list` | – | `Profile[]` for the selected game |
| `profiles.save` | `{ name, id? }` | `Profile` — snapshot of the currently enabled mods (new, or overwrite `id`) |
| `profiles.apply` | `{ id }` | `{ enabled: string[], disabled: string[], problems: string[], missing: MissingMod[] }` — turns the profile's mods on and every other mod off (progress as `install.progress` with `planId: "profile"`); mods the library no longer has come back in `missing` |
| `profiles.delete` | `{ id }` | `true` |
| `profiles.export` | `{ id }` | `{ code }` — a share code (`VML1:` + base64url JSON) |
| `profiles.import` | `{ code }` | `{ profile: Profile, missing: MissingMod[] }` — saves the profile; `missing` lists site mods to install with `mod.download` |

```ts
type Profile = { id, name, gameId, createdAt, updatedAt, mods: { id: string | null, name, source, pageUrl: string | null, version: string | null }[], active: boolean }
type MissingMod = { name, source, pageUrl: string | null, version: string | null }
```

## Game health and repair after a Rockstar update

| cmd | args | result |
|---|---|---|
| `game.health` | – | `Health` |
| `game.repair` | – | `{ health: Health, rebuilt: boolean, restoredItems: number, restoredEntries: number, backup: string \| null, problems: string[] }` — when the game's own `update\update.rpf` changed since `mods\update\update.rpf` was made: keeps the old copy as a backup, copies the new one into mods, and re-applies every dlclist item and every archive entry the library's mods (and other tools) had added. Progress as `install.progress` with `planId: "repair"`. |

```ts
type Health = {
  gameVersion: string | null,          // GTA5.exe / GTA5_Enhanced.exe file version
  modsUpdateStale: boolean,            // mods\update\update.rpf is older than the game's update.rpf (game updated)
  staleReason: string | null,
  scriptHookV: { installed: boolean, supportsGame: boolean | null, version: string | null, message: string | null },  // from ScriptHookV.log / file version
  addonPacks: number,                  // dlc packs in the active dlclist
  packLimitWarning: string | null,     // lots of packs and no gameconfig / packfile limit adjuster
  problems: { level: "Info" | "Warn" | "Block", text, fix: string | null }[]   // fix = a cmd the UI can offer as a button, e.g. "game.repair"
}
```

## Diagnostics

| cmd | args | result |
|---|---|---|
| `diag.report` | – | `{ logs: LogInfo[], crashes: { path, time, size }[], unmanaged: Unmanaged[] }` |
| `diag.openLog` | `{ path }` | `true` (opens in Notepad; only paths from `diag.report`) |
| `diag.quarantine` | `{ path }` | `{ quarantined: QuarantineItem }` — moves an unmanaged file out of the game folder into the launcher's quarantine (reversible) |
| `diag.quarantineList` | – | `QuarantineItem[]` |
| `diag.restore` | `{ id }` | `true` — puts a quarantined file back |

```ts
type LogInfo = { name, path, exists: boolean, modified: string | null, size: number, tail: string /* last ~150 lines */, problems: { level, text, line: number }[] }
type Unmanaged = { path /* game-relative */, kind: "asi" | "script" | "plugin" | "loader" | "other", size, modified, note: string | null }
type QuarantineItem = { id, path /* game-relative */, movedAt }
```
Logs read: ScriptHookV.log, ScriptHookVDotNet.log, asiloader.log, RagePluginHook.log, OpenIV.log,
ELS.log, menyooLog.txt (game folder) and the newest Rockstar CrashLogs. Unmanaged = .asi / .dll / scripts\ / plugins\
files the vanilla game doesn't ship and no library mod installed.

## Library extras

`library.list` items gain `spawnNames: string[]` (model names from vehicles.meta / peds.meta / weapons.meta in the
mod's dlc packs, for trainers' spawn-by-name), `kind: "vehicle" | "ped" | "weapon" | "map" | "script" | "other"`,
and `conflicts: { modId, modName, paths: string[] }[]` (files this mod and another enabled mod both changed; the later
install wins). Plans gain warnings when they'd overwrite another library mod's files.

## Launch options

| cmd | args | result |
|---|---|---|
| `game.launchOptions` | – | `{ file: "args.txt" \| "commandline.txt", args: string[], known: { arg, label, help, editions: string[] }[] }` |
| `game.setLaunchOptions` | `{ args: string[] }` | same shape as `game.launchOptions` |

## Frameworks from their official GitHub releases

| cmd | args | result |
|---|---|---|
| `frameworks.list` | – | `{ id, name, installedVersion \| null, latestVersion \| null, releaseUrl, forEdition: "Legacy" \| "Enhanced" \| "Both", canInstall: boolean }[]` |
| `frameworks.install` | `{ id }` | `true` — downloads the official release zip (progress as `download.progress`, key = id), then the normal `plan.ready` flow |

Ids: `shvdn` (scripthookvdotnet/scripthookvdotnet-nightly, Legacy), `shvdne` (Chiheb-Bacha/ScriptHookVDotNetEnhanced, Enhanced),
`lemonui` (LemonUIbyLemon/LemonUI), `ragenativeui` (alexguirre/RAGENativeUI). Script Hook V itself has no GitHub: the UI links dev-c.com.

## Placing files by hand

| cmd | args | result |
|---|---|---|
| `plan.setTarget` | `{ planId, index, folder, wholeFolder }` | `Plan` — puts op `index` (usually a "Not recognised" / "Game file" one) into the game-relative `folder` (`""` = game root). With `wholeFolder`, the other unplaced files from the same archive folder go along, keeping sub-folders. Offer common folders in a picker: game root, `scripts`, `plugins`, `plugins\LSPDFR`, `lspdfr\audio\scanner`, `menyooStuff\Spooner`, `ELS\pack_default`, `mods`, plus free text. |

## Favourites and storage

| cmd | args | result |
|---|---|---|
| `favorites.list` | – | `ModSummary[]` (as saved) |
| `favorites.toggle` | `{ item: ModSummary }` | `{ favorite: boolean }` |
| `storage.report` | – | `{ modsFolder, backups, downloads, payloads, cache: number /* bytes */ }` |
| `storage.clean` | `{ what: "downloads" \| "cache" \| "staging" }` | `storage.report` result |

---

# Wave 3 (tools)

Same rules as above. Every change to the game goes through `Journal` inside `RpfService.Session(game)`
and is recorded as an `InstalledMod` with `Source = "tool"`, so it shows in Library and Uninstall undoes it.
Mutating commands run on `Task.Run` wrapped in `Busy(...)`, refuse while the game runs or Mods are OFF
(`ModInstaller.Preflight`), and report progress as `install.progress` with the `planId` named below.

## Texture optimizer — `Host/Bridge.Textures.cs`, `Core/Tools/TextureOptimizer.cs`, `wwwroot/js/tools/textures.js`

Shrinks oversized textures in add-on packs by dropping their top mip levels (the smaller levels are
already in the file, so nothing is re-compressed and quality is what the game shows at that size anyway).

| cmd | args | result |
|---|---|---|
| `tex.scan` | `{ maxSize: 1024 \| 2048 \| 4096 }` | `{ maxSize, packs: TexPack[], totalBytes, savableBytes }` (progress `planId: "textures"`) |
| `tex.optimize` | `{ packs: string[] /* TexPack.id */, maxSize }` | `{ optimized: { id, modId, savedBytes, textures }[], problems: string[] }` |

```ts
type TexPack = { id /* game-relative dlc.rpf path */, name, owner: { modId, name } | null, textures: number,
  oversized: number, kept: number /* oversized but left alone */, keptReasons: string[], bytes: number, savableBytes: number, alreadyOptimized: { modId, maxSize } | null,
  skipped: string | null /* e.g. "Enhanced textures this build can't read" */,
  worst: { ytd, texture, width, height, format }[] /* up to 5 biggest */ }
```
Only packs under `mods\update\x64\dlcpacks` and `onigiri\dlcpacks` (never the game's own folders). Undo = uninstall the
"Smaller textures: <pack>" record in Library.

## Add-on pack builder — `Core/Tools/AddonPackBuilder.cs` (+ the hook in `ModAnalyzer`), `Host/Bridge.PackBuilder.cs`, `wwwroot/js/tools/packbuilder.js`

Mods that ship an add-on car or ped as loose files (`.yft`/`.ytd`/`.ydd`/`.ymt` + `vehicles.meta`, `handling.meta`,
`carvariations.meta`, `carcols.meta`, `vehiclelayouts.meta`, or `peds.meta`) get a proper `dlc.rpf` built during
analysis; the plan shows one `DlcPack` op ("Built from the mod's loose files") and the loose files as
"Packed into <name>". Problems (model names in vehicles.meta without a .yft, handling ids that don't match) become plan warnings.

| cmd | args | result |
|---|---|---|
| `packbuild.fromFolder` | `{ name? }` | `true` — folder picker, then the normal `plan.ready` flow for that folder |

`packBuilderCard()` (exported by `js/tools/packbuilder.js`) is shown at the top of the "Add-on packs" tab.

## Pack manager and game file checker — `Core/Tools/PackManager.cs`, `Core/Tools/GameFileChecker.cs`, `Host/Bridge.Packs.cs`, `wwwroot/js/tools/packs.js`, `wwwroot/js/tools/gamefiles.js`

| cmd | args | result |
|---|---|---|
| `packs.list` | – | `{ list: { kind: "onigiri" \| "mods" \| "game", path }, packs: PackRow[], problems: { level, text, fix: string \| null }[] }` |
| `packs.setEnabled` | `{ item, enabled }` | `packs.list` result — takes a pack out of / back into the active dlclist (journaled in a "Pack manager" record) |
| `packs.register` | `{ name }` | `packs.list` result — lists a pack that's on disk but not registered |
| `packs.fix` | `{ fix }` | `packs.list` result — runs a `problems[].fix` (`"duplicates"`, `"onigiri-sync"`, …) |
| `gamefiles.check` | – | `{ archives: { path, state: "original" \| "edited" \| "unreadable", size }[], strayPacks: { path, name }[], advice: string[] }` |
| `gamefiles.moveToMods` | `{ path }` | `gamefiles.check` result — copies an edited original archive into `mods\` (if there's no copy yet) so the edits survive a store "verify files" |
| `gamefiles.verify` | `{ open?: boolean }` | `{ how: string, url: string \| null }` — how to restore the originals through the player's store (Steam validate URI, Epic / Rockstar launcher steps). With `open: true` it also opens the Steam file check (only ever a `steam://validate/` link) |

```ts
type PackRow = { name, item /* dlcpacks:/x/ */, folder: string | null, sizeBytes: number,
  status: "ok" | "missing" | "duplicate" | "disabled" | "notRegistered" | "notInActiveList",
  source: "rockstar" | "launcher" | "other", owner: { modId, name } | null, spawnNames: string[] }
```
"edited" = one of the game's own archives (game root `*.rpf`, `update\*.rpf`, `update\x64\dlcpacks\*\dlc.rpf`) whose header
is OPEN, i.e. edited in place by OpenIV instead of through the mods folder. Header bytes only; never writes the game's files.
