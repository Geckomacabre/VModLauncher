# V Mod Launcher dev kit

Tools for people who **make** GTA V mods (Legacy and Enhanced), built on the launcher's own engine:

- **`vml`**, a command-line tool: build OIV packages from a small project file, lint them, diff two releases,
  check a package against its sources, test-install it in a sandbox, read archives and a game's registration,
  convert textures for Enhanced, scan a release for files that must not ship, and more.
- **`vml mcp`**, the same tools as a [Model Context Protocol](https://modelcontextprotocol.io) server, so an AI
  agent (Claude Code, Claude Desktop, any MCP client) can build and check releases for you.

The dev kit **reads** game folders and **never writes into one**. Every tool that writes refuses a path inside a
GTA V folder. Installing is the launcher's job (or OpenIV's): those come with backups and an undo.

## Build

Needs the .NET 8 SDK (and the .NET Framework 4.8 targeting pack for `ScriptCheck`, which Visual Studio's
".NET desktop development" workload includes), and the CodeWalker submodule (`git clone --recursive`, or
`git submodule update --init`).

```powershell
dotnet build tools\vml -c Release        # tools\vml\bin\Release\net8.0-windows\vml.exe (+ ScriptCheck.exe)
.\build.ps1 -DevKit                       # also publishes a self-contained publish\VModDevKit\vml.exe + zip
vml help                                  # every command
```

## A release, start to finish

```powershell
vml oiv build mymod.oiv.json --set version=1.4.0     # build the package (it lints itself)
vml oiv test build\MyMod-1.4.0.oiv --game "D:\Games\GTAV"           # install, reinstall, uninstall in a sandbox
vml oiv test build\MyMod-1.4.0.oiv --game "D:\Games\GTAV" --fresh   # the same as a first install
vml oiv verify build\MyMod-1.4.0.oiv --project mymod.oiv.json --hashes build\hashes.txt
vml oiv diff releases\MyMod-1.3.0.oiv build\MyMod-1.4.0.oiv          # what changed since the last release
vml scan --blocklist D:\private-tools build\MyMod-1.4.0.oiv git:C:\src\mymod
```

Each command exits with 0 when everything is fine, so the list works as a CI script too.

## OIV projects (`*.oiv.json`)

A project says what goes into the package. Start one with `vml oiv init mymod.oiv.json --edition Legacy`.

```jsonc
{
  "name": "My Mod",
  "version": "1.4.0",                 // <version>: major 1, minor 4; the tag is "1.4.0" unless "tag" says otherwise
  "edition": "Legacy",                // Legacy | Enhanced | Any: picks the resource versions checked
  "gameVersionElement": true,         // write <gameversion>Legacy</gameversion> (see "Lints" below)
  "author": "Me",
  "authorLink": "https://example.com",
  "description": "@description.txt",  // text, or @file to read it from a file
  "licence": "MIT",
  "icon": "icon.png",
  "colors": { "headerBackground": "$FF2B1B3D", "headerBlackText": false, "iconBackground": "$FFE06AA8" },
  "output": "build/{name}-{version}-{edition}.oiv",
  "vars": { "packs": "D:/build/dlcpacks" }, // ${packs} below; --set packs=... overrides; ${env:NAME} reads the environment

  "files": [
    { "from": "scripts", "to": "scripts", "exclude": ["**/Save/**", "**/*.pdb"] },
    { "from": "MyMod.asi", "to": "MyMod.asi" },
    { "from": "${packs}/mymod/dlc.rpf", "to": "update/x64/dlcpacks/mymod/dlc.rpf", "content": "dlcpacks/mymod/dlc.rpf" }
  ],
  "archives": [
    { "path": "update/update.rpf", "create": true,
      "archives": [
        { "path": "x64/data/cdimages/scaleform_frontend.rpf",
          "files": [ { "from": "startscreen", "to": "", "content": "startscreen" } ] }
      ],
      "steps": [ "@steps/handling.xml" ] }     // raw OIV steps run inside this archive
  ],
  "registerPacks": ["mymod"],                   // dlcpacks:/mymod/ in dlclist.xml, once
  "raise": { "TxdStore": 150000, "ConfigModelInfo/MaxExtraPedModelInfos": 600 },
  "steps": [ "@steps/extra.xml" ]               // raw OIV steps at the end of <content>
}
```

| Field | What it does |
| --- | --- |
| `files` | Loose files. `from` is a file or a folder (everything in it); `to` is the install path from the game folder. Game data (`update\`, `x64*`, `common\`, `*.rpf`, dlcpacks) installs to `mods\`, the rest (scripts, plugins, `.asi`) to the game folder, as OpenIV does. `content` is where the files sit inside the package (default: same as `to`). `exclude` takes globs relative to `from`: `*` stays in one folder, `**` crosses folders. `optional: true` skips a missing `from`. |
| `archives` | Files and steps inside archives, nested as deep as you need. `path` is from the game folder for a top-level archive and from the parent for a nested one. `create` sets `createIfNotExist`. |
| `registerPacks` | Packs to list in `update.rpf`'s `dlclist.xml`, each **once**, however often the package is installed. |
| `raise` | `gameconfig.xml` limits to raise and **never lower**: a pool by its `PoolName`, a value under `Config` (`ConfigModelInfo/MaxExtraPedModelInfos`), or an absolute XPath. |
| `scratchTag` | The placeholder element those steps use and remove again (default `VmlScratch`). |
| `assemblyTemplate` | Build from an existing assembly.xml: its metadata and its own steps stay, and the generated steps go where it has `<!-- vml:content -->` (or at the end of `<content>`). The easy way to move an existing package to vml. |
| `resourceVersions` | Resource versions to enforce by extension. By default, from `edition`: `.ytd` must be 13 on Legacy and 5 on Enhanced. |
| `store` | Extensions packed without compression (default: `.rpf .png .jpg .dds .mp3 .ogg .zip .7z .rar .oiv .gz`). |
| `id` | The package id. Left out, it's made from the name, so every version of a package keeps one id. |

`vml oiv plan mymod.oiv.json --xml` shows what a build would pack, and the assembly.xml, without building.

### What a build guarantees

- **Real file dates.** Every file keeps its own modification time, which the installers write to disk. Script
  Hook V .NET caches scripts by path *and file time* (`%LOCALAPPDATA%\assembly\dl3`). Packages that stamp one
  fixed date on every file make updates keep running the first version a player installed.
- **Steps that work on every install.** `registerPacks` and `raise` generate edits that find their XML node on
  a first install *and* on every update, in OpenIV and in CodeWalker's OIV Package Installer. A plain `<add>`
  lists the pack again on every update. An add guarded by `Paths[not(Item[...])]` finds nothing once the pack is
  listed, and OpenIV calls that install FAILED ("Unable to find XML Node"). The generated steps aim each add at
  the guarded node *or* a placeholder that is removed at the end. A raised value is replaced when it's lower,
  and kept when another mod set it higher.
- **No silent mistakes.** The build stops when a source is missing, when two files would install to the same
  place, or when a resource is in the other edition's format (a gen9 `.ytd` in a Legacy package crashes the
  game). The same content path is packed once, even when two entries point at it.

## Lints (`vml oiv info`)

`vml oiv info` reads any `.oiv`, yours or someone else's, without installing it. It lists the metadata, every
archive and XML edit, the packs it registers and the limits it raises, then flags:

| Level | Lint |
| --- | --- |
| error | an `<add>` whose source isn't in the package |
| error | `.ytd` files in the wrong edition's format |
| warning | files in `content\` that no step installs |
| warning | two files installing to the same place |
| warning | every file carrying one date (the script cache problem above) |
| warning | a `dlclist.xml` `<add>` with no guard (the pack gets listed again on every reinstall) |
| warning | a version in the file name that isn't the package's |
| info | no `<gameversion>`: CodeWalker's OIV Package Installer then asks for the game folder on every install, and ignores `--game` on its command line unless `--force` is given. A player with both editions can pick the wrong one. |

## The sandbox test (`vml oiv test`)

```powershell
vml oiv test MyMod.oiv --game "C:\Program Files\GTAVEnhanced" [--fresh] [--keep]
```

The package goes into a throwaway game folder, the way the launcher (and OpenIV) installs it. Then it goes in
again over itself, the way an update does. Then both installs are undone. The real game is **only read**:

- the sandbox gets a stand-in exe;
- its archives hold just the entries the package edits (`gameconfig.xml`, `dlclist.xml`, nested archives like
  `scaleform_frontend.rpf`), copied from the real game;
- the real game's exe supplies the keys for Rockstar's encrypted archives.

So a test takes seconds, not a 4 GB copy, and it can't break the player's install the way running an installer
against the real game can.

It checks that:
- every file landed where it belongs, byte for byte, including files inside archives;
- there were no installer warnings, so every XML step found its node;
- every XML edit, applied twice *exactly as OpenIV does*, finds its node, lists no pack twice and leaves no
  placeholder (the launcher's own installer journals `dlclist.xml` item by item, which would hide that);
- `dlclist.xml` lists every pack the package registers, and none twice. Duplicates already in the game's own
  list don't count: Rockstar's Enhanced list names `dlcpacks:/mp2025_01_g9ec/` twice;
- every raised limit is at least what the package asks for, and appears once;
- installing over itself changes nothing more;
- after the uninstall, every file and every archive entry is exactly as before.

`--fresh` first takes the package's packs out of the sandbox's `dlclist.xml` and halves the limits it raises,
which tests a first install. (The real game usually has the mod installed already, which tests the update path.)

## Other commands

| Command | |
| --- | --- |
| `vml reg --game <root> [--pools a,b\|all]` | The `dlclist.xml` the game really reads (NaturalVision's onigiri list when installed): packs listed twice, listed but missing, present but not listed; and the `gameconfig.xml` pool sizes. |
| `vml rpf ls\|cat\|extract\|diff` | Read archives, nested ones included (`x64/data/cdimages/scaleform_frontend.rpf/viload_0.ytd`). `--game` supplies the keys for encrypted ones. |
| `vml res info <files\|folders>` | Resource headers: version (`.ytd`: 13 Legacy, 5 Enhanced), page flags. |
| `vml res gen9 <in> <out>` | Legacy texture dictionaries (one, or a folder) converted to Enhanced's gen9 format. |
| `vml scan --blocklist <folder> <targets>` | Byte-identical copies of anything in the blocklist folder, whatever they're called, in files, folders, the entries of `.zip`/`.oiv` packages and `git:<repo>` (the files git tracks). |
| `vml shvdn-cache [list\|clear] [--filter name]` | Script Hook V .NET's script cache. Clear it when "the update didn't take". Refuses while GTA V runs. |
| `vml scriptcheck <dll> --shvdn <folder> [--refs folder]` | JIT-compiles every method of a script against that `ScriptHookVDotNet3.dll`, so a call into an API the player's build doesn't have shows up before they see "Unhandled exception in script". |
| `vml analyze <mod>` | The launcher's install plan for any download, without installing. |

## MCP server

`vml mcp` speaks MCP over stdio (JSON-RPC, protocol 2024-11-05 to 2025-06-18) with no extra runtime. It
offers 18 tools: `games`, `analyze_mod`, `oiv_init`, `oiv_build`, `oiv_info`, `oiv_diff`, `oiv_verify`,
`oiv_sandbox_test`, `registration`, `rpf_list`, `rpf_read`, `rpf_extract`, `rpf_diff`, `resource_info`,
`ytd_to_gen9`, `blocklist_scan`, `shvdn_cache` and `script_check`. Each takes the same inputs as its command and
returns its report as text.

**Claude Code:**

```powershell
claude mcp add vmod -- C:\path\to\vml.exe mcp
```

**Claude Desktop** (`%APPDATA%\Claude\claude_desktop_config.json`), or any client that takes the same shape:

```json
{
  "mcpServers": {
    "vmod": { "command": "C:\\path\\to\\vml.exe", "args": ["mcp"] }
  }
}
```

Then ask for things like "build mymod.oiv.json, sandbox-test it against my Enhanced game, and diff it with
last week's release". The same rule holds over MCP: game folders are only read, and every tool that writes
refuses a path inside a game.
