# V Mod Launcher

A ReSkate-style launcher for GTA V (Legacy and Enhanced). Browse mods from **GTA5-Mods.com** and
**LCPDFR.com** inside the launcher, press Install, and it puts every file where it belongs, then
lets you turn each mod off, back on, update it, or remove it without leaving anything behind.

## What it does

- **Finds your games**: Epic, Steam and Rockstar Launcher installs of GTA V Legacy and Enhanced, plus any folder you add.
- **Browse**: category pages, sorting, search and full mod pages (gallery, description, versions,
  requirements) for both sites, read through a real browser session at human speed (one request a second).
- **Download through the site itself**: Install opens the mod's real download page in a launcher
  window. You go through the site's own countdown or sign-in (LCPDFR needs an account) and press its
  button; the launcher catches the file. Your password goes to the site, never to the launcher.
  Ads are blocked in that window (ad networks, and code from sites other than the mod sites), so no fake
  "Download" ad sits next to the real button; links to other sites open in your own browser.
- **Works out where everything goes** by looking at what the files are, not just their folder names:
  - .NET DLLs are read for their references: Script Hook V .NET scripts → `scripts\`, LSPDFR plugins →
    `plugins\LSPDFR\`, RAGE plugins → `plugins\`, their helper libraries → the game folder.
  - `dlc.rpf` add-on packs → `mods\update\x64\dlcpacks\<name>` (named from the pack's own setup2.xml) and
    registered in dlclist.xml; with NaturalVision's onigiri installed, its dlcpacks folder and list instead.
  - `.oiv` packages run step by step like OpenIV does (mods folder, XML edits, nested archives).
  - ELS configs → `ELS\pack_default\`, Menyoo maps/outfits → `menyooStuff\`, Map Editor maps → `scripts\AutoloadMaps\`.
  - Archives that mirror the game folder (`Grand Theft Auto V\scripts\...`, `bin\ScriptHookV.dll`) map 1:1.
  - Folders named like archives (`x64e.rpf\levels\gta5\vehicles.rpf\adder.yft`) go inside that archive.
  - Loose replace files (`adder.yft`, `handling.meta`) go into the copy the game actually loads: the
    launcher scans your game once (about 30 s) and, for data files that exist in many DLCs, picks the
    copy whose entries the mod edits.
  - Save games → your Rockstar profile folder (the old save is backed up).
  - Legacy / Enhanced / Add-On / Replace / option folders become choices, preselected for your game.
  - Readmes, screenshots, source code, installers and full dlclist.xml replacements are shown, not copied.
  - FiveM resources and non-GTA downloads are recognised and refused with a reason.
- **Never touches the game's own archives**: every archive edit goes to a copy in `mods\`.
- **Undo for everything**: each change is journaled with a backup. Uninstall restores the exact bytes,
  even when two mods changed the same file (the later one keeps working). A failed install rolls back.
- **Prerequisites at a glance**: Script Hook V, the ASI loader, SHVDN / SHVDNE (the right one per
  edition), OpenIV.asi / OpenRPF, RAGE Plugin Hook, LSPDFR, ELS, Menyoo.
- **Play**: story mode through your store, LSPDFR through RAGE Plugin Hook, and a Mods ON/OFF switch
  that parks the ASI loaders and `mods\` so GTA Online starts clean.
- **Program Files installs**: one "Fix folder access" click (Windows asks for admin once) instead of
  running the launcher as administrator.

## Legacy and Enhanced

There are two launchers, built from the same code:

- **V Mod Launcher Legacy** (`VModLauncher.Legacy.exe`): only sees GTA V Legacy (`GTA5.exe`). Uses SHVDN,
  `dinput8.dll` and OpenIV.asi, and picks the Legacy folder when a mod ships both.
- **V Mod Launcher Enhanced** (`VModLauncher.Enhanced.exe`): only sees GTA V Enhanced (`GTA5_Enhanced.exe`).
  Uses SHVDNE, `xinput1_4.dll` and OpenRPF, picks the Enhanced folder when a mod ships both, and follows
  NaturalVision's onigiri dlclist when it's installed.

Both warn before installing a mod whose page marks it for the other edition only. A plain `dotnet build`
also gives `VModLauncher.exe`, which handles both editions with a game switcher.

## Build

Needs the .NET 8 SDK on Windows and the Microsoft Edge WebView2 Runtime (built into Windows 11). CodeWalker,
which reads the game's archives, is a git submodule built from its source, so clone with `--recursive` (or run
`git submodule update --init` in an existing clone).

```powershell
git clone --recursive https://github.com/Geckomacabre/VModLauncher.git
dotnet build VModLauncher.sln
dotnet build src\VModLauncher -p:Edition=Legacy     # or Enhanced
dotnet test tests\VModLauncher.Tests
.\build.ps1          # self-contained Legacy and Enhanced builds + zips in .\publish (-All adds the combined one,
                     # -DevKit the dev kit)
```

`tools\vml` is a command-line front end to the same engine, handy for checking what the launcher would do:

```powershell
vml games                                   # installs found and their prerequisites
vml analyze <archive> --game <gta folder>   # dry run: prints the install plan
vml index --game <gta folder> adder.yft     # builds the replace index and looks up names
```

## Dev kit (for mod authors)

`vml` is also a toolkit for **making** mods, and `vml mcp` serves it to AI agents as an MCP server. It reads
games and never writes into one. Full guide: [docs/devkit.md](docs/devkit.md).

```powershell
vml oiv build mymod.oiv.json                 # an OIV from a small JSON project: real file dates, guarded
                                             # dlclist/gameconfig steps, wrong-edition resources caught
vml oiv info  MyMod.oiv                      # metadata, steps and lints for any package
vml oiv test  MyMod.oiv --game <gta folder>  # install, reinstall, uninstall in a sandbox seeded from the game
vml oiv diff  old.oiv new.oiv                # what changed between releases
vml oiv verify MyMod.oiv --project mymod.oiv.json --hashes hashes.txt
vml reg --game <gta folder>                  # what dlclist.xml / gameconfig.xml the game really loads
vml rpf ls|cat|extract|diff ...              # archives, nested and encrypted ones included
vml res gen9 <in.ytd> <out.ytd>              # Legacy textures for Enhanced
vml scan --blocklist <folder> <targets>      # no private or third-party file ships
vml mcp                                      # the same 18 tools over MCP (claude mcp add vmod -- vml.exe mcp)
```

## Where things live

- Launcher data: `%LOCALAPPDATA%\VModLauncher` (library.json, backups, kept downloads, the site browser profile, logs).
- Game changes: only the game folder, its `mods\` copies, and the save profile for save games.

## Notes

- Mods are for story mode. Switch mods OFF before playing GTA Online.
- GTA5-Mods.com and LCPDFR.com content belongs to the mod authors; the launcher shows their pages and
  downloads through the sites' own download pages.
- Layout: `src\VModLauncher.Core` (engine), `src\VModLauncher` (WPF + WebView2 app; UI in `wwwroot`,
  site readers in `SiteScripts`), `src\VModLauncher.DevKit` (the dev kit and MCP server), `tests`, `tools`
  (`vml`, `ScriptCheck`, `OivCheck`). Contracts: `docs\bridge.md`, `docs\site-extractors.md`, `docs\devkit.md`.
