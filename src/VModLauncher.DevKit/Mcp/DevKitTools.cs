using System.Text;
using System.Text.Json.Nodes;
using VModLauncher.Core;
using VModLauncher.Core.Game;
using VModLauncher.Core.Library;
using VModLauncher.DevKit.Game;
using VModLauncher.DevKit.Oiv;
using VModLauncher.DevKit.Resources;
using VModLauncher.DevKit.Rpf;
using VModLauncher.DevKit.Scan;
using VModLauncher.DevKit.Shvdn;

namespace VModLauncher.DevKit.Mcp;

/// <summary>The dev kit's operations as MCP tools. None of them writes into a game folder.</summary>
public static class DevKitTools
{
    public const string Instructions =
        "Tools for building and checking GTA V mods (Legacy and Enhanced). Typical release flow: oiv_build a project " +
        "(*.oiv.json), oiv_info to lint the package, oiv_sandbox_test with the player's game folder to prove it installs, " +
        "installs again over itself and uninstalls cleanly, oiv_verify against the sources, oiv_diff against the previous " +
        "release, and blocklist_scan before publishing. Real game folders are only ever read: no tool installs into a game " +
        "or writes inside one (use the launcher or an installer for that). Paths are absolute Windows paths.";

    public static McpServer Server(string version) => new(All(), version, Instructions);

    public static IReadOnlyList<McpTool> All() => new List<McpTool>
    {
        new("games", "Find GTA V installs",
            "Lists the GTA V installs on this PC (Steam, Epic, Rockstar; Legacy and Enhanced) and the state of each one's prerequisites " +
            "(Script Hook V, ASI loader, Script Hook V .NET, OpenIV.asi / OpenRPF, RAGE Plugin Hook, LSPDFR...).",
            Schema("{}"), _ =>
            {
                var sb = new StringBuilder();
                foreach (var g in GameLocator.FindAll())
                {
                    sb.AppendLine(g.ToString());
                    foreach (var p in Prerequisites.Check(g)) sb.AppendLine($"   {p.State,-8} {p.Name}: {p.Detail}");
                }
                return sb.Length > 0 ? sb.ToString() : "No GTA V installs found.";
            }),

        new("analyze_mod", "Dry-run a mod install",
            "Works out where every file of a downloaded mod (archive, folder or .oiv) would go, without installing: the install plan " +
            "with choices (Legacy/Enhanced, add-on/replace), requirements and warnings. Give game for a plan against a real install.",
            Schema("""{"path":{"type":"string","description":"The mod: .zip/.rar/.7z/.oiv or a folder"},"game":{"type":"string","description":"GTA V folder (read only)"}}""", "path"),
            a =>
            {
                var game = OptGame(a) ?? new GameInstall { Root = @"C:\FakeGame", Edition = GameEdition.Legacy };
                var paths = new LauncherPaths(Path.Combine(Path.GetTempPath(), "vml-mcp"));
                var service = new ModService(paths, new ModLibrary(paths));
                var src = Req(a, "path");
                var plan = service.Prepare(game, src, Path.GetFileNameWithoutExtension(src), null);
                try { return PlanText.Format(plan); }
                finally { service.DeleteStaging(plan); }
            }),

        new("oiv_init", "Start an OIV project",
            "Writes a starting *.oiv.json project (name, version, edition, files) to fill in. Doesn't overwrite unless overwrite is true.",
            Schema("""{"project":{"type":"string","description":"Where to write the .oiv.json"},"name":{"type":"string"},"edition":{"type":"string","enum":["Legacy","Enhanced","Any"]},"overwrite":{"type":"boolean"}}""", "project"),
            a =>
            {
                var file = Path.GetFullPath(Req(a, "project"));
                Safety.EnsureNotInGame(file);
                if (File.Exists(file) && !Bool(a, "overwrite")) throw new ToolException(file + " exists (overwrite: true replaces it)");
                var p = OivProject.Example(Opt(a, "name") ?? Path.GetFileNameWithoutExtension(file).Replace(".oiv", ""), Opt(a, "edition") ?? "Legacy");
                p.Save(file);
                return $"Wrote {file}:\n{p.ToJson()}";
            }, ReadOnly: false),

        new("oiv_build", "Build an OIV package",
            "Builds a .oiv from a *.oiv.json project: assembly.xml generated (or from its assemblyTemplate), files with their real dates " +
            "(so Script Hook V .NET's cache picks up updates), guarded dlclist.xml / gameconfig.xml steps from registerPacks / raise, " +
            "and checks (missing files, two files to one place, resources in the wrong edition's format).",
            Schema("""{"project":{"type":"string"},"output":{"type":"string","description":"Overrides the project's output path"},"set":{"type":"object","additionalProperties":{"type":"string"},"description":"Values for ${variables} in the project"}}""", "project"),
            a =>
            {
                var project = OivProject.Load(Req(a, "project"), Map(a, "set"));
                var output = Opt(a, "output");
                if (output != null) Safety.EnsureNotInGame(output);
                var plan = OivBuilder.Plan(project);
                Safety.EnsureNotInGame(output ?? OivBuilder.OutputPath(project, plan));
                var r = OivBuilder.Build(project, output);
                return r.ToText() + "\n" + OivInspector.Inspect(r.Output).ToText();
            }, ReadOnly: false),

        new("oiv_info", "Inspect an OIV package",
            "Reads a .oiv without installing it: metadata, steps, archives, the packs it registers and limits it raises, and lints for what " +
            "breaks installs or updates (missing files, unguarded dlclist adds, one fixed date on every file, resources in the other edition's " +
            "format, no <gameversion>).",
            Schema("""{"oiv":{"type":"string"}}""", "oiv"),
            a => OivInspector.Inspect(Req(a, "oiv")).ToText()),

        new("oiv_diff", "Compare two OIV packages",
            "Compares two packages file by file (content, not zip bytes) and their assembly.xml line by line: what was added, removed or changed " +
            "between releases, and changed files that kept their old date.",
            Schema("""{"old":{"type":"string"},"new":{"type":"string"}}""", "old", "new"),
            a => OivDiff.Compare(Req(a, "old"), Req(a, "new")).ToText()),

        new("oiv_verify", "Check a package against its sources",
            "Checks every file in a .oiv is byte-identical to its source: give the project it was built from, or map (content prefix -> folder). " +
            "Optionally writes the sha256 of every packaged file to hashes_out.",
            Schema("""{"oiv":{"type":"string"},"project":{"type":"string"},"set":{"type":"object","additionalProperties":{"type":"string"}},"map":{"type":"object","additionalProperties":{"type":"string"},"description":"content prefix (\"scripts\", \"\" for all) -> source folder"},"hashes_out":{"type":"string"}}""", "oiv"),
            a =>
            {
                var oiv = Req(a, "oiv");
                VerifyResult r;
                if (Opt(a, "project") is { } proj) r = OivVerify.AgainstProject(oiv, OivProject.Load(proj, Map(a, "set")));
                else if (Map(a, "map") is { Count: > 0 } map) r = OivVerify.AgainstFolders(oiv, map);
                else throw new ToolException("give project or map");
                if (Opt(a, "hashes_out") is { } h)
                {
                    Safety.EnsureNotInGame(h);
                    File.WriteAllLines(h, r.Hashes);
                }
                return r.ToText();
            }),

        new("oiv_sandbox_test", "Test-install a package in a sandbox",
            "Installs a .oiv into a throwaway copy of a game (seeded from the real game's files, which are only read), then again over itself, " +
            "then uninstalls both, and checks every file landed, no pack is listed twice, raised limits are right, and the game ends exactly as " +
            "it started. Give game (the real install) so gameconfig.xml, dlclist.xml and encrypted nested archives can be tested.",
            Schema("""{"oiv":{"type":"string"},"game":{"type":"string","description":"The real GTA V folder: read only, for its files and archive keys"},"fresh":{"type":"boolean","description":"Test a first install: the package's packs taken out of dlclist.xml and its limits lowered first"},"keep":{"type":"boolean","description":"Keep the sandbox folder to look at"}}""", "oiv"),
            a => OivSandboxTest.Run(Req(a, "oiv"), Opt(a, "game"), Bool(a, "keep"), fresh: Bool(a, "fresh")).ToText()),

        new("registration", "Read a game's dlclist and limits",
            "What a game will load: its dlclist.xml (packs listed twice, listed but missing, present but not listed; NaturalVision's onigiri list " +
            "when installed) and its gameconfig.xml pool sizes. pools: names to show, or [\"all\"].",
            Schema("""{"game":{"type":"string"},"pools":{"type":"array","items":{"type":"string"}}}""", "game"),
            a => RegistrationReport.Read(Req(a, "game"), List(a, "pools")).ToText()),

        new("rpf_list", "List an archive",
            "Lists the files in an .rpf archive, nested archives included (as path segments), with resource versions. filter: glob patterns " +
            "('**/*.ytd'). game: a GTA V folder whose keys read Rockstar's encrypted archives.",
            Schema("""{"archive":{"type":"string"},"game":{"type":"string"},"filter":{"type":"array","items":{"type":"string"}},"max":{"type":"integer"}}""", "archive"),
            a => Tools(a).List(Req(a, "archive"), Globs(a), Int(a, "max") ?? 2000)),

        new("rpf_read", "Read a text file from an archive",
            "Returns a text file (xml, meta, txt, dat...) from inside an archive: 'common/data/dlclist.xml', or through nested archives " +
            "'x64/data/cdimages/scaleform_frontend.rpf/...'.",
            Schema("""{"archive":{"type":"string"},"path":{"type":"string"},"game":{"type":"string"}}""", "archive", "path"),
            a =>
            {
                var text = Tools(a).ReadText(Req(a, "archive"), Req(a, "path")) ?? throw new ToolException("not found: " + Req(a, "path"));
                return text.Length > 400_000 ? text[..400_000] + $"\n... ({text.Length:N0} characters, cut)" : text;
            }),

        new("rpf_extract", "Extract files from an archive",
            "Extracts files (all, or those matching filter globs) from an archive into out_dir, as standalone files (resources keep their RSC7 header).",
            Schema("""{"archive":{"type":"string"},"out_dir":{"type":"string"},"game":{"type":"string"},"filter":{"type":"array","items":{"type":"string"}}}""", "archive", "out_dir"),
            a =>
            {
                var outDir = Req(a, "out_dir");
                Safety.EnsureNotInGame(outDir);
                var files = Tools(a).ExtractTo(Req(a, "archive"), outDir, Globs(a));
                return $"{files.Count} files extracted to {Path.GetFullPath(outDir)}\n" + string.Join("\n", files.Take(200));
            }, ReadOnly: false),

        new("rpf_diff", "Compare two archives",
            "Compares every file in two archives by content (nested archives through their files): the same, different, only in one.",
            Schema("""{"a":{"type":"string"},"b":{"type":"string"},"game":{"type":"string"}}""", "a", "b"),
            a => Tools(a).Diff(Req(a, "a"), Req(a, "b")).ToText()),

        new("resource_info", "Resource headers",
            "RSC7 header of resources (.ytd .ydr .ydd .yft .ybn .ymap...): version (texture dictionaries: 13 Legacy, 5 Enhanced), page flags, size. " +
            "Folders are searched for resource files.",
            Schema("""{"paths":{"type":"array","items":{"type":"string"}}}""", "paths"),
            a =>
            {
                var sb = new StringBuilder();
                foreach (var p in List(a, "paths")!)
                {
                    var files = Directory.Exists(p)
                        ? Directory.EnumerateFiles(p, "*.y*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase)
                        : (IEnumerable<string>)new[] { p };
                    foreach (var f in files.Take(2000)) sb.AppendLine(ResourceInfo.Read(f).ToString());
                }
                return sb.ToString();
            }),

        new("ytd_to_gen9", "Convert textures for Enhanced",
            "Converts Legacy texture dictionaries (.ytd version 13) to GTA V Enhanced's gen9 format (version 5): one file, or every .ytd in a " +
            "folder (other files are copied).",
            Schema("""{"input":{"type":"string"},"output":{"type":"string"}}""", "input", "output"),
            a =>
            {
                Safety.EnsureNotInGame(Req(a, "output"));
                var done = Gen9.ConvertYtd(Req(a, "input"), Req(a, "output"));
                return string.Join("\n", done.Select(c => $"{Path.GetFileName(c.From)}: v{c.OldVersion} -> v{c.NewVersion}{(c.Textures >= 0 ? $", {c.Textures} textures" : " (already gen9, copied)")} -> {c.To}"));
            }, ReadOnly: false),

        new("blocklist_scan", "Scan a release for files that mustn't ship",
            "Checks files, folders, .zip/.oiv packages (their entries) and git repos ('git:C:\\repo' = the files git tracks) for byte-identical " +
            "copies of anything in the blocklist folder (private tools, other people's files), whatever they're named.",
            Schema("""{"blocklist":{"type":"string"},"targets":{"type":"array","items":{"type":"string"}}}""", "blocklist", "targets"),
            a => BlocklistScan.Run(Req(a, "blocklist"), List(a, "targets")!).ToText()),

        new("shvdn_cache", "Script Hook V .NET's script cache",
            "Lists or clears Script Hook V .NET's shadow-copy cache (%LOCALAPPDATA%\\assembly\\dl3), which can keep running old scripts after " +
            "an update. action: list or clear; filter: a script name or path part. Clearing refuses while GTA V runs.",
            Schema("""{"action":{"type":"string","enum":["list","clear"]},"filter":{"type":"string"}}""", "action"),
            a =>
            {
                var filter = Opt(a, "filter");
                var entries = Req(a, "action") == "clear" ? ShvdnCache.Clear(filter) : ShvdnCache.List(filter);
                var head = Req(a, "action") == "clear" ? $"cleared {entries.Count} cached scripts" : $"{entries.Count} cached scripts";
                return head + "\n" + string.Join("\n", entries.Select(e => "  " + e));
            }, ReadOnly: false),

        new("script_check", "JIT-check a script",
            "JIT-compiles every method of a Script Hook V .NET script against a ScriptHookVDotNet3.dll (shvdn: a game folder works), so calls " +
            "into an API that build doesn't have show up before players see 'Unhandled exception in script'. references: folders with the " +
            "script's other DLLs (e.g. the game's scripts folder for LemonUI).",
            Schema("""{"dll":{"type":"string"},"shvdn":{"type":"string"},"references":{"type":"array","items":{"type":"string"}}}""", "dll", "shvdn"),
            a =>
            {
                var r = ScriptCheck.Run(Req(a, "dll"), Req(a, "shvdn"), List(a, "references"));
                return (r.Passed ? "PASSED\n" : "FAILED\n") + r.Output;
            }),
    };

    // ---------------------------------------------------------------- arguments

    static JsonObject Schema(string properties, params string[] required)
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = JsonNode.Parse(properties) };
        if (required.Length > 0) schema["required"] = new JsonArray(required.Select(r => (JsonNode?)r).ToArray());
        return schema;
    }

    static string Req(JsonObject a, string name) => Opt(a, name) ?? throw new ToolException($"\"{name}\" is required");

    static string? Opt(JsonObject a, string name) => a[name] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    static bool Bool(JsonObject a, string name) => a[name] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    static int? Int(JsonObject a, string name) => a[name] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    static List<string>? List(JsonObject a, string name) => a[name] switch
    {
        JsonArray arr => arr.Select(x => x!.GetValue<string>()).ToList(),
        JsonValue v when v.TryGetValue<string>(out var s) => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        _ => null,
    };

    static Dictionary<string, string>? Map(JsonObject a, string name) =>
        a[name] is JsonObject o ? o.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? "") : null;

    static List<Glob> Globs(JsonObject a) => (List(a, "filter") ?? new()).Select(g => new Glob(g)).ToList();

    static GameInstall? OptGame(JsonObject a) =>
        Opt(a, "game") is { } g ? GameInstall.TryOpen(g) ?? throw new ToolException("Not a GTA V folder: " + g) : null;

    static RpfTools Tools(JsonObject a) => RpfTools.For(Opt(a, "game"));
}
