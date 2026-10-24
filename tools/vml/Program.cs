using System.Text;
using VModLauncher.Core;
using VModLauncher.Core.Game;
using VModLauncher.Core.Install;
using VModLauncher.Core.Install.Oiv;
using VModLauncher.Core.Library;
using VModLauncher.DevKit;
using VModLauncher.DevKit.Game;
using VModLauncher.DevKit.Mcp;
using VModLauncher.DevKit.Oiv;
using VModLauncher.DevKit.Resources;
using VModLauncher.DevKit.Rpf;
using VModLauncher.DevKit.Scan;
using VModLauncher.DevKit.Shvdn;

// vml: the launcher's engine and the mod author's dev kit on the command line (and as an MCP server: vml mcp).
// Run "vml help" for every command; docs/devkit.md has the details.

ModInstaller.OivRunner = (journal, path, log) =>
{
    using var pkg = OivPackage.Open(path);
    var oiv = new OivInstaller(journal);
    oiv.Install(pkg, log);
    foreach (var w in oiv.Warnings) Console.WriteLine("  oiv warning: " + w);
};

var a = new Args(args);
if (a.Command == null || a.Command is "help" or "--help" or "-h") { Console.WriteLine(Help.Text); return a.Command == null ? 1 : 0; }

try
{
    return a.Command switch
    {
        "mcp" => await Mcp(),
        "oiv" => Oiv(a),
        "reg" => Print(RegistrationReport.Read(a.Game ?? throw Usage("reg needs --game <GTA V folder>"), a.Opt("--pools")?.Split(',', StringSplitOptions.TrimEntries)).ToText()),
        "rpf" => Rpf(a),
        "res" => Res(a),
        "scan" => Scan(a),
        "shvdn-cache" => ShvdnCacheCommand(a),
        "scriptcheck" => ScriptCheckCommand(a),
        _ => Engine(a),
    };
}
catch (UsageException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or InvalidDataException or InvalidOperationException or IOException)
{
    Console.Error.WriteLine("error: " + ex.Message);
    return 3;
}

static async Task<int> Mcp()
{
    // stdout carries only protocol messages: anything else printed goes to stderr
    var stdout = Console.OpenStandardOutput();
    Console.SetOut(Console.Error);
    var version = typeof(DevKitTools).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
    await DevKitTools.Server(version).RunAsync(Console.OpenStandardInput(), stdout);
    return 0;
}

static int Oiv(Args a)
{
    switch (a.Sub)
    {
        case "init":
        {
            var file = a.Pos(0) ?? throw Usage("vml oiv init <project.oiv.json> [--name N] [--edition Legacy|Enhanced|Any]");
            Safety.EnsureNotInGame(file);
            if (File.Exists(file) && !a.Flag("--force")) throw Usage(file + " exists (--force replaces it)");
            var p = OivProject.Example(a.Opt("--name") ?? Path.GetFileName(file).Split('.')[0], a.Opt("--edition") ?? "Legacy");
            p.Save(file);
            Console.WriteLine($"Wrote {Path.GetFullPath(file)}");
            return 0;
        }
        case "build":
        {
            var project = OivProject.Load(a.Pos(0) ?? throw Usage("vml oiv build <project.oiv.json> [--out file.oiv] [--set name=value]..."), a.Sets());
            var output = a.Opt("--out");
            var plan = OivBuilder.Plan(project);
            Safety.EnsureNotInGame(output ?? OivBuilder.OutputPath(project, plan));
            var r = OivBuilder.Build(project, output, new Progress<string>(s => Console.Error.WriteLine("  " + s)));
            Console.Write(r.ToText());
            var report = OivInspector.Inspect(r.Output);
            foreach (var l in report.Lints.Where(l => l.Level != LintLevel.Info)) Console.WriteLine("  " + l);
            return report.HasErrors ? 1 : 0;
        }
        case "plan":
        {
            var plan = OivBuilder.Plan(OivProject.Load(a.Pos(0) ?? throw Usage("vml oiv plan <project.oiv.json> [--set name=value]..."), a.Sets()));
            foreach (var f in plan.Files) Console.WriteLine($"{(f.Scope.Length > 0 ? f.Scope + " > " : "")}{f.Target}   <- {f.Source}");
            foreach (var w in plan.Warnings) Console.WriteLine("warning: " + w);
            if (a.Flag("--xml")) Console.WriteLine(plan.AssemblyXml);
            Console.WriteLine($"{plan.Files.Count} files, {plan.Entries.Count} zip entries");
            return 0;
        }
        case "info":
        {
            var r = OivInspector.Inspect(a.Pos(0) ?? throw Usage("vml oiv info <package.oiv>"));
            Console.Write(r.ToText());
            return r.HasErrors ? 1 : 0;
        }
        case "diff":
        {
            var r = OivDiff.Compare(a.Pos(0) ?? throw Usage("vml oiv diff <old.oiv> <new.oiv>"), a.Pos(1) ?? throw Usage("vml oiv diff <old.oiv> <new.oiv>"));
            Console.Write(r.ToText());
            return r.Identical ? 0 : 4;
        }
        case "verify":
        {
            var oiv = a.Pos(0) ?? throw Usage("vml oiv verify <package.oiv> (--project p.oiv.json [--set k=v] | --map prefix=folder ...) [--hashes out.txt] [--label text]");
            VerifyResult r;
            if (a.Opt("--project") is { } proj) r = OivVerify.AgainstProject(oiv, OivProject.Load(proj, a.Sets()), a.Opt("--label"));
            else
            {
                var map = a.Opts("--map").Select(m => m.Split('=', 2)).ToDictionary(kv => kv[0], kv => kv.Length > 1 ? kv[1] : throw Usage("--map prefix=folder"));
                if (map.Count == 0) throw Usage("verify needs --project or --map");
                r = OivVerify.AgainstFolders(oiv, map, a.Opt("--label"));
            }
            if (a.Opt("--hashes") is { } h) { Safety.EnsureNotInGame(h); File.WriteAllLines(h, r.Hashes); }
            Console.Write(r.ToText());
            return r.Ok ? 0 : 1;
        }
        case "test":
        {
            var r = OivSandboxTest.Run(a.Pos(0) ?? throw Usage("vml oiv test <package.oiv> [--game <real GTA V folder, read only>] [--fresh] [--keep]"), a.Game, a.Flag("--keep"),
                new Progress<string>(s => { if (a.Flag("--verbose")) Console.Error.WriteLine("  " + s); }), fresh: a.Flag("--fresh"));
            Console.Write(r.ToText());
            return r.Passed ? 0 : 1;
        }
        default:
            throw Usage("vml oiv init|build|plan|info|diff|verify|test ...");
    }
}

static int Rpf(Args a)
{
    var tools = RpfTools.For(a.Game);
    var filter = a.Opts("--filter").Select(g => new Glob(g)).ToList();
    switch (a.Sub)
    {
        case "ls":
            Console.Write(tools.List(a.Pos(0) ?? throw Usage("vml rpf ls <archive.rpf> [--game root] [--filter glob]..."), filter, int.TryParse(a.Opt("--max"), out var m) ? m : 5000));
            return 0;
        case "cat":
        {
            var text = tools.ReadText(a.Pos(0) ?? throw Usage("vml rpf cat <archive.rpf> <inner/path> [--game root]"), a.Pos(1) ?? throw Usage("vml rpf cat <archive.rpf> <inner/path>"));
            if (text == null) { Console.Error.WriteLine("not found"); return 3; }
            Console.Write(text);
            return 0;
        }
        case "extract":
        {
            var outDir = a.Pos(1) ?? throw Usage("vml rpf extract <archive.rpf> <out folder> [--game root] [--filter glob]...");
            Safety.EnsureNotInGame(outDir);
            var files = tools.ExtractTo(a.Pos(0)!, outDir, filter);
            Console.WriteLine($"{files.Count} files extracted to {Path.GetFullPath(outDir)}");
            return 0;
        }
        case "diff":
        {
            var r = tools.Diff(a.Pos(0) ?? throw Usage("vml rpf diff <a.rpf> <b.rpf> [--game root]"), a.Pos(1) ?? throw Usage("vml rpf diff <a.rpf> <b.rpf>"));
            Console.Write(r.ToText());
            return r.Differ.Count + r.OnlyFirst.Count + r.OnlySecond.Count == 0 ? 0 : 4;
        }
        default:
            throw Usage("vml rpf ls|cat|extract|diff ...");
    }
}

static int Res(Args a)
{
    switch (a.Sub)
    {
        case "info":
            foreach (var p in a.Positional)
            {
                var files = Directory.Exists(p) ? Directory.EnumerateFiles(p, "*.y*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase) : (IEnumerable<string>)new[] { p };
                foreach (var f in files) Console.WriteLine(ResourceInfo.Read(f));
            }
            return 0;
        case "gen9":
        {
            var output = a.Pos(1) ?? throw Usage("vml res gen9 <in.ytd|folder> <out.ytd|folder>");
            Safety.EnsureNotInGame(output);
            foreach (var c in Gen9.ConvertYtd(a.Pos(0)!, output))
                Console.WriteLine($"{Path.GetFileName(c.From)}: v{c.OldVersion} -> v{c.NewVersion}{(c.Textures >= 0 ? $", {c.Textures} textures" : " (already gen9)")} -> {c.To}");
            return 0;
        }
        default:
            throw Usage("vml res info <files|folders>... | vml res gen9 <in> <out>");
    }
}

static int Scan(Args a)
{
    var block = a.Opt("--blocklist") ?? throw Usage("vml scan --blocklist <folder> <file|folder|package.oiv|git:repo>...");
    if (a.Positional.Count == 0) throw Usage("vml scan --blocklist <folder> <targets>...");
    var r = BlocklistScan.Run(block, a.Positional);
    Console.Write(r.ToText());
    return r.Clean ? 0 : 1;
}

static int ShvdnCacheCommand(Args a)
{
    var clear = a.Sub == "clear";
    if (a.Sub is not (null or "list" or "clear")) throw Usage("vml shvdn-cache [list|clear] [--filter name]");
    var entries = clear ? ShvdnCache.Clear(a.Opt("--filter")) : ShvdnCache.List(a.Opt("--filter"));
    Console.WriteLine($"{(clear ? "cleared " : "")}{entries.Count} cached scripts in {ShvdnCache.Root}");
    foreach (var e in entries) Console.WriteLine("  " + e);
    return 0;
}

static int ScriptCheckCommand(Args a)
{
    var dll = a.Pos(0) ?? throw Usage("vml scriptcheck <script.dll> --shvdn <folder with ScriptHookVDotNet3.dll> [--refs folder]...");
    var r = ScriptCheck.Run(dll, a.Opt("--shvdn") ?? a.Game ?? throw Usage("scriptcheck needs --shvdn <folder>"), a.Opts("--refs"));
    Console.WriteLine(r.Output);
    return r.Passed ? 0 : 1;
}

// ---------------------------------------------------------------- the launcher engine (as before)

static int Engine(Args a)
{
    var args = a.Raw;
    var gameRoot = a.Game;
    var game = gameRoot != null ? GameInstall.TryOpen(gameRoot) : null;
    if (gameRoot != null && game == null) { Console.Error.WriteLine("Not a GTA V folder: " + gameRoot); return 2; }
    var paths = new LauncherPaths(a.Opt("--data") ?? Path.Combine(Path.GetTempPath(), "vml-cli"));
    var library = new ModLibrary(paths);
    var service = new ModService(paths, library);

    switch (args[0])
    {
        case "games":
            foreach (var g in GameLocator.FindAll())
            {
                Console.WriteLine(g);
                foreach (var p in Prerequisites.Check(g)) Console.WriteLine($"   {p.State,-8} {p.Name}: {p.Detail}");
            }
            return 0;

        case "analyze":
        case "install":
        {
            var src = args[1];
            var index = game != null && args.Contains("--index") ? GameIndex.TryLoad(game, paths.Cache) : null;
            var plan = service.Prepare(game ?? new GameInstall { Root = @"C:\FakeGame", Edition = GameEdition.Legacy }, src, Path.GetFileNameWithoutExtension(src), index);
            foreach (var pick in a.Opts("--pick"))
            {
                var kv = pick.Split('=', 2);
                plan.Select(kv[0], kv[1]);
            }
            Console.Write(PlanText.Format(plan));
            if (args[0] == "analyze") { service.DeleteStaging(plan); return 0; }
            if (game == null) { Console.Error.WriteLine("--game is required to install"); return 2; }
            var mod = service.Install(game, plan, new InstalledMod { Name = plan.ModName },
                new Progress<InstallProgress>(p => { if (p.Detail != null) Console.WriteLine($"  [{p.Stage}] {p.Detail}"); }));
            Console.WriteLine($"Installed {mod.Id}: {mod.Entries.Count} journal entries");
            return 0;
        }

        case "index":
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var idx = GameIndex.TryLoad(game!, paths.Cache) ?? GameIndex.Build(game!, paths.Cache, new Progress<string>(s => { if (s.Contains("/") && !s.EndsWith("0)")) return; Console.WriteLine("  " + s); }));
            Console.WriteLine($"{idx.Count:N0} entries in {sw.Elapsed.TotalSeconds:0.0}s");
            foreach (var name in a.Positional)
                foreach (var hit in idx.Find(name).Take(6)) Console.WriteLine($"  {name}: {hit}");
            return 0;
        }

        case "cat":
        {
            // vml cat <archive.rpf> <inner/path> --game <root>
            var bytes = new RpfService(game!).TryRead(args[1], args[2]);
            if (bytes == null) { Console.Error.WriteLine("not found"); return 3; }
            Console.Write(Encoding.UTF8.GetString(bytes));
            return 0;
        }

        case "rpfdiff":
        {
            // vml rpfdiff <a.rpf> <b.rpf> --game <root>: kept for scripts; "vml rpf diff" is the same
            var r = RpfTools.For(gameRoot).Diff(args[1], args[2]);
            Console.Write(r.ToText());
            return r.Differ.Count + r.OnlyFirst.Count + r.OnlySecond.Count == 0 ? 0 : 4;
        }

        case "copyentry":
        {
            // vml copyentry <from.rpf> <to.rpf> <inner/path> --game <root>: puts one file back from a backup archive
            var svc = new RpfService(game!);
            var bytes = svc.TryRead(args[1], args[3]);
            if (bytes == null) { Console.Error.WriteLine("not found"); return 3; }
            svc.Write(args[2], args[3], bytes);
            Console.WriteLine($"wrote {bytes.Length} bytes to {args[3]}");
            return 0;
        }

        case "sandbox":
        {
            // a fake game folder for UI tests: fake exe and loader, an OPEN mods\update\update.rpf with a small dlclist
            var root = Path.GetFullPath(args[1]);
            bool enhanced = args.Contains("--enhanced");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, enhanced ? "GTA5_Enhanced.exe" : "GTA5.exe"), "sandbox");
            File.WriteAllText(Path.Combine(root, enhanced ? "xinput1_4.dll" : "dinput8.dll"), "sandbox loader");
            File.WriteAllText(Path.Combine(root, "ScriptHookV.dll"), "sandbox");
            File.WriteAllText(Path.Combine(root, "OpenIV.asi"), "sandbox");
            var dir = Path.Combine(root, "mods", "update");
            Directory.CreateDirectory(dir);
            if (!File.Exists(Path.Combine(dir, "update.rpf")))
            {
                var rpf = CodeWalker.GameFiles.RpfFile.CreateNew(dir, "update.rpf", CodeWalker.GameFiles.RpfEncryption.OPEN);
                var common = CodeWalker.GameFiles.RpfFile.CreateDirectory(rpf.Root, "common");
                var data = CodeWalker.GameFiles.RpfFile.CreateDirectory(common, "data");
                var xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<SMandatoryPacksData>\r\n\t<Paths>\r\n\t\t<Item>dlcpacks:/mpbeach/</Item>\r\n\t</Paths>\r\n</SMandatoryPacksData>\r\n";
                CodeWalker.GameFiles.RpfFile.CreateFile(data, "dlclist.xml", Encoding.UTF8.GetBytes(xml), true);
            }
            Console.WriteLine("Sandbox game at " + root);
            return 0;
        }

        case "list":
            foreach (var m in library.Mods) { Console.WriteLine($"{m.Id}  {m.Name}  [{m.Summary}] enabled={m.Enabled}"); foreach (var e in m.Entries) Console.WriteLine("     " + e); }
            return 0;

        case "uninstall":
        {
            var mod = library.Find(args[1]) ?? throw new InvalidOperationException("No mod " + args[1]);
            var problems = service.Uninstall(game!, mod);
            Console.WriteLine(problems.Count == 0 ? "Uninstalled cleanly" : "Problems:\n  " + string.Join("\n  ", problems));
            return 0;
        }
    }
    Console.WriteLine(Help.Text);
    return 1;
}

static int Print(string text)
{
    Console.Write(text);
    return 0;
}

static UsageException Usage(string message) => new("usage: " + message);

sealed class UsageException(string message) : Exception(message);

/// <summary>Command line: a command, an optional subcommand, --options with values, flags, and the rest positional.</summary>
sealed class Args
{
    static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase) { "--keep", "--fresh", "--force", "--verbose", "--xml", "--index", "--all", "--enhanced" };
    static readonly HashSet<string> WithSub = new(StringComparer.OrdinalIgnoreCase) { "oiv", "rpf", "res", "shvdn-cache" };

    public string[] Raw { get; }
    public string? Command { get; }
    public string? Sub { get; }
    public List<string> Positional { get; } = new();
    readonly List<(string Name, string? Value)> options = new();

    public Args(string[] args)
    {
        Raw = args;
        if (args.Length == 0) return;
        Command = args[0];
        int i = 1;
        if (WithSub.Contains(Command) && args.Length > 1 && !args[1].StartsWith("--")) Sub = args[i++];
        // the engine's commands take their archive / mod id as the first positional, after the command
        for (; i < args.Length; i++)
        {
            var x = args[i];
            if (x.StartsWith("--"))
            {
                if (Flags.Contains(x) || i + 1 >= args.Length) options.Add((x, null));
                else options.Add((x, args[++i]));
            }
            else Positional.Add(x);
        }
    }

    public string? Opt(string name) => options.LastOrDefault(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
    public IEnumerable<string> Opts(string name) => options.Where(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && o.Value != null).Select(o => o.Value!);
    public bool Flag(string name) => options.Any(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    public string? Pos(int i) => i < Positional.Count ? Positional[i] : null;
    public string? Game => Opt("--game");

    public Dictionary<string, string> Sets() => Opts("--set").Select(s => s.Split('=', 2))
        .ToDictionary(kv => kv[0], kv => kv.Length > 1 ? kv[1] : "", StringComparer.OrdinalIgnoreCase);
}

static class Help
{
    public const string Text = """
        vml: V Mod Launcher's engine and dev kit (docs/devkit.md)

        Packages (OIV)
          vml oiv init <project.oiv.json> [--name N] [--edition Legacy|Enhanced|Any] [--force]
          vml oiv build <project.oiv.json> [--out file.oiv] [--set name=value]...
          vml oiv plan <project.oiv.json> [--set name=value]... [--xml]     what a build would pack, without building
          vml oiv info <package.oiv>                                       metadata, steps, lints
          vml oiv diff <old.oiv> <new.oiv>                                 file by file, and assembly.xml
          vml oiv verify <package.oiv> --project p.oiv.json | --map prefix=folder... [--hashes out.txt]
          vml oiv test <package.oiv> [--game <GTA V folder>] [--fresh] [--keep]  install, reinstall, uninstall in a sandbox

        Games and archives (games are only read)
          vml games                                                        installs and prerequisites
          vml reg --game <root> [--pools a,b|all]                          dlclist.xml and gameconfig.xml limits
          vml rpf ls <archive.rpf> [--game root] [--filter glob]...
          vml rpf cat <archive.rpf> <inner/path> [--game root]
          vml rpf extract <archive.rpf> <out folder> [--game root] [--filter glob]...
          vml rpf diff <a.rpf> <b.rpf> [--game root]
          vml res info <files|folders>...                                  resource versions (ytd: 13 Legacy, 5 Enhanced)
          vml res gen9 <in.ytd|folder> <out>                               Legacy textures to Enhanced

        Releases and scripts
          vml scan --blocklist <folder> <file|folder|package|git:repo>...  no forbidden file ships
          vml shvdn-cache [list|clear] [--filter name]                     Script Hook V .NET's script cache
          vml scriptcheck <script.dll> --shvdn <folder> [--refs folder]... JIT-check against a ScriptHookVDotNet3.dll

        MCP
          vml mcp                                                          every dev kit tool over stdio, for AI agents

        Launcher engine
          vml analyze <archive|folder> [--game root] [--pick group=option]  install plan, dry run
          vml install <archive> --game <root> --data <dir> [--pick group=option]
          vml list --data <dir> | vml uninstall <modId> --game <root> --data <dir>
          vml index --game <root> <name>...  |  vml sandbox <dir> [--enhanced]
        """;
}
