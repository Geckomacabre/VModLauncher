using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using CodeWalker.GameFiles;
using VModLauncher.Core.Game;
using VModLauncher.Core.Install;
using VModLauncher.Core.Install.Oiv;
using VModLauncher.Core.Library;
using VModLauncher.DevKit.Rpf;

namespace VModLauncher.DevKit.Oiv;

public sealed class SandboxTestResult
{
    public string Package { get; init; } = "";
    public string Sandbox { get; init; } = "";
    public List<(bool Ok, string What)> Checks { get; } = new();
    public List<string> Notes { get; } = new();
    public bool Passed => Checks.All(c => c.Ok);

    internal void Check(bool ok, string what) => Checks.Add((ok, what));

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Sandbox test of {Path.GetFileName(Package)}");
        foreach (var n in Notes) sb.AppendLine("  " + n);
        foreach (var (ok, what) in Checks) sb.AppendLine((ok ? "  PASS " : "  FAIL ") + what);
        sb.AppendLine(Passed ? "ALL PASSED" : $"{Checks.Count(c => !c.Ok)} FAILED");
        return sb.ToString();
    }
}

/// <summary>
/// Installs a package into a throwaway copy of a game, the way the launcher (and OpenIV) would: twice, to prove an
/// update installs cleanly over itself, then undoes both and checks the game is exactly as before.
/// <para>
/// The real game is only read. The sandbox gets a stand-in exe, and its archives hold just the entries the package
/// edits (gameconfig.xml, dlclist.xml, nested archives), copied from the real game; the real game's exe supplies
/// the keys for Rockstar's encrypted archives. So a test takes seconds, not a 4 GB copy, and never touches the
/// player's install, unlike running an installer against it.
/// </para>
/// </summary>
public static class OivSandboxTest
{
    /// <param name="fresh">
    /// Test a first install: before installing, the sandbox's dlclist.xml loses the packs the package registers and
    /// the limits it raises are set lower (the real game usually has the mod installed already, which tests the
    /// update path).
    /// </param>
    public static SandboxTestResult Run(string oivPath, string? realGameRoot, bool keep = false, IProgress<string>? log = null, bool fresh = false)
    {
        var real = realGameRoot != null
            ? GameInstall.TryOpen(realGameRoot) ?? throw new DirectoryNotFoundException("Not a GTA V folder: " + realGameRoot)
            : null;
        using var pkg = OivPackage.Open(oivPath);
        var edition = real?.Edition ?? pkg.TargetEdition ?? GameEdition.Legacy;
        var sandboxRoot = Path.Combine(Path.GetTempPath(), "vml-sandbox-" + Guid.NewGuid().ToString("N")[..8]);
        if (real != null && sandboxRoot.StartsWith(real.Root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The sandbox would be inside the real game");

        var result = new SandboxTestResult { Package = Path.GetFullPath(oivPath), Sandbox = sandboxRoot };
        if (real != null && pkg.TargetEdition != null && pkg.TargetEdition != real.Edition)
            result.Check(false, $"the package is for {pkg.TargetEdition} but the game is {real.Edition}");

        Directory.CreateDirectory(sandboxRoot);
        File.WriteAllText(Path.Combine(sandboxRoot, edition == GameEdition.Enhanced ? "GTA5_Enhanced.exe" : "GTA5.exe"), "vml sandbox");
        var game = new GameInstall { Root = sandboxRoot, Edition = edition, KeysRoot = real?.Root };
        try
        {
            using (RpfService.Session(real ?? game))
            {
                var rpf = new RpfService(game);
                Seed(pkg, real, game, rpf, result, log);
                if (fresh) Unregister(pkg, game, rpf, result);
                Test(pkg, game, rpf, result, log);
            }
        }
        finally
        {
            RpfService.ForgetKeys();
            if (!keep) { try { Directory.Delete(sandboxRoot, true); } catch { } }
            else result.Notes.Add("sandbox kept: " + sandboxRoot);
        }
        return result;
    }

    // ---------------------------------------------------------------- the sandbox

    /// <summary>Copies into the sandbox what the package's steps read: the edited and nested entries of each archive, and loose edited files.</summary>
    static void Seed(OivPackage pkg, GameInstall? real, GameInstall game, RpfService rpf, SandboxTestResult result, IProgress<string>? log)
    {
        var realRpf = real != null ? new RpfService(real) : null;
        // a package can have several blocks for one archive (files in one, registration in another): seed what all of them need
        var tops = pkg.Content.Elements().Where(e => Is(e, "archive"))
            .GroupBy(e => OivInstaller.Normalize(OivZip.Attr(e, "path") ?? ""), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 0);
        foreach (var block in tops)
        {
            var top = block.Key;
            var sandboxPath = game.Resolve(Journal.ModsRel(top));
            var source = real == null ? null
                : File.Exists(real.Resolve(Journal.ModsRel(top))) ? real.Resolve(Journal.ModsRel(top))
                : File.Exists(real.Resolve(top.StartsWith("mods\\", StringComparison.OrdinalIgnoreCase) ? top[5..] : top)) ? real.Resolve(top.StartsWith("mods\\", StringComparison.OrdinalIgnoreCase) ? top[5..] : top)
                : null;
            Directory.CreateDirectory(Path.GetDirectoryName(sandboxPath)!);
            RpfFile.CreateNew(Path.GetDirectoryName(sandboxPath)!, Path.GetFileName(sandboxPath), RpfEncryption.OPEN);
            if (source == null)
            {
                result.Notes.Add(real == null
                    ? $"{top}: no game given, so the sandbox's copy starts empty (edits of game files can't be checked; give --game)"
                    : $"{top}: the game has none; the sandbox's starts empty");
                continue;
            }
            int copied = 0;
            foreach (var inner in block.SelectMany(b => Needed(b, "")).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var data = realRpf!.TryRead(source, inner);
                if (data == null) continue;
                rpf.Write(sandboxPath, inner, data);
                copied++;
            }
            result.Notes.Add($"{top}: {copied} entries copied from {real!.ToRelative(source)} (read only)");
            log?.Report($"seeded {top}");
        }
        // loose <xml>/<text> edits read the game's own file
        foreach (var edit in pkg.Content.Elements().Where(e => Is(e, "xml") || Is(e, "text")))
        {
            var path = OivZip.Attr(edit, "path");
            if (string.IsNullOrWhiteSpace(path) || real == null) continue;
            var rel = OivInstaller.GameTarget(path);
            var from = File.Exists(real.Resolve(rel)) ? real.Resolve(rel) : File.Exists(real.Resolve(OivInstaller.Normalize(path))) ? real.Resolve(OivInstaller.Normalize(path)) : null;
            if (from == null) continue;
            var to = game.Resolve(rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to, true);
        }
    }

    /// <summary>
    /// Makes the sandbox look like a game that never had the package: its packs out of dlclist.xml, the limits it
    /// raises halved. Written straight into the sandbox, before the baseline, so the uninstall check compares with this.
    /// </summary>
    static void Unregister(OivPackage pkg, GameInstall game, RpfService rpf, SandboxTestResult result)
    {
        var update = game.ModsUpdateRpf;
        if (!File.Exists(update)) { result.Notes.Add("fresh: no update.rpf in the sandbox, nothing to take out"); return; }
        var items = pkg.Content.Descendants().Where(e => Is(e, "Item")).Select(e => e.Value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (rpf.TryRead(update, DlcList.InnerPath) is { } list && items.Count > 0)
        {
            var text = DlcList.Decode(list, out var bom);
            var stripped = items.Aggregate(text, (t, i) => DlcList.Remove(t, i));
            if (stripped != text) rpf.Write(update, DlcList.InnerPath, DlcList.Encode(stripped, bom));
            result.Notes.Add($"fresh: took {string.Join(", ", items)} out of dlclist.xml");
        }
        if (rpf.TryRead(update, "common/data/gameconfig.xml") is { } config)
        {
            var doc = XDocument.Parse(OivZip.Decode(config), LoadOptions.PreserveWhitespace);
            var lowered = new List<string>();
            foreach (var replace in pkg.Content.Descendants().Where(e => Is(e, "replace")))
                foreach (var want in replace.Elements())
                {
                    if (!int.TryParse(OivZip.Attr(want, "value"), out var need)) continue;
                    var pool = System.Text.RegularExpressions.Regex.Match(OivZip.Attr(replace, "xpath") ?? "", @"PoolName='([^']+)'").Groups[1].Value;
                    var found = pool.Length > 0
                        ? doc.Descendants("Item").Where(i => i.Element("PoolName")?.Value == pool).SelectMany(i => i.Elements(want.Name.LocalName))
                        : doc.Descendants(want.Name.LocalName);
                    foreach (var f in found.ToList())
                    {
                        f.SetAttributeValue("value", need / 2);
                        lowered.Add($"{(pool.Length > 0 ? pool : want.Name.LocalName)} {need / 2}");
                    }
                }
            if (lowered.Count > 0)
            {
                var bom = config.Length >= 3 && config[0] == 0xEF && config[1] == 0xBB && config[2] == 0xBF;
                var text = doc.Declaration + doc.ToString(SaveOptions.DisableFormatting);
                rpf.Write(update, "common/data/gameconfig.xml", (bom ? new byte[] { 0xEF, 0xBB, 0xBF } : Array.Empty<byte>()).Concat(Encoding.UTF8.GetBytes(text)).ToArray());
                result.Notes.Add("fresh: lowered " + string.Join(", ", lowered));
            }
        }
    }

    /// <summary>Inner paths an archive step needs from the game: its edited files, deletes and (whole) nested archives.</summary>
    static IEnumerable<string> Needed(XElement archive, string prefix)
    {
        foreach (var step in archive.Elements())
        {
            var path = OivZip.Attr(step, "path") ?? (Is(step, "delete") ? step.Value : null);
            if (string.IsNullOrWhiteSpace(path)) continue;
            var inner = Join(prefix, path);
            if (Is(step, "xml") || Is(step, "text") || Is(step, "delete")) yield return inner;
            else if (Is(step, "archive")) yield return inner; // the nested archive whole: its own edits are inside it
        }
    }

    // ---------------------------------------------------------------- the test

    static void Test(OivPackage pkg, GameInstall game, RpfService rpf, SandboxTestResult result, IProgress<string>? log)
    {
        var backups = Path.Combine(game.Root, "_vml_backups");
        var before = Snapshot(pkg, game, backups);
        // the game's own list may already name a pack twice (Enhanced's names dlcpacks:/mp2025_01_g9ec/ twice): only new ones count
        var alreadyTwice = ListedTwice(game, rpf);
        if (alreadyTwice.Count > 0) result.Notes.Add("the game's dlclist.xml already lists " + string.Join(", ", alreadyTwice) + " twice (not the package's doing)");
        ReplayXml(pkg, game, rpf, result, alreadyTwice);

        // ---- install
        var sw = Stopwatch.StartNew();
        var mod1 = new InstalledMod { Name = pkg.Name, GameId = game.Id };
        var j1 = new Journal(game, rpf, mod1, backups);
        var i1 = new OivInstaller(j1);
        i1.Install(pkg, log);
        result.Notes.Add($"install: {sw.Elapsed.TotalSeconds:F1}s, {mod1.Entries.Count} changes journaled");
        CheckWarnings(i1.Warnings, "install", result);
        CheckAdds(pkg, game, rpf, result);
        var reg1 = Registration(pkg, game, rpf, result, "after install", alreadyTwice);
        var after1 = Snapshot(pkg, game, backups);

        // ---- the same package again: an update over itself
        sw.Restart();
        var mod2 = new InstalledMod { Name = pkg.Name + " (again)", GameId = game.Id, InstalledAt = DateTime.UtcNow.AddSeconds(1) };
        var j2 = new Journal(game, rpf, mod2, backups);
        var i2 = new OivInstaller(j2);
        i2.Install(pkg);
        result.Notes.Add($"install over itself: {sw.Elapsed.TotalSeconds:F1}s");
        CheckWarnings(i2.Warnings, "install over itself", result);
        var reg2 = Registration(pkg, game, rpf, result, "after installing over itself", alreadyTwice);
        foreach (var (file, text) in reg1)
            result.Check(reg2.TryGetValue(file, out var t2) && Canonical(t2) == Canonical(text), $"{file}: the same after installing over itself (no pack twice, no value changed)");
        var after2 = Snapshot(pkg, game, backups);
        var drift = after1.Keys.Union(after2.Keys).Where(k => !after1.TryGetValue(k, out var a) || !after2.TryGetValue(k, out var b) || a != b)
            .Where(k => !k.EndsWith("dlclist.xml", StringComparison.OrdinalIgnoreCase) && !k.EndsWith("gameconfig.xml", StringComparison.OrdinalIgnoreCase)).ToList();
        result.Check(drift.Count == 0, $"every file the same after installing over itself{(drift.Count > 0 ? ": " + string.Join(", ", drift.Take(5)) : "")}");

        // ---- undo both, newest first
        var problems = j2.UndoAll().Concat(j1.UndoAll()).ToList();
        result.Check(problems.Count == 0, $"uninstall: no problems{(problems.Count > 0 ? ": " + string.Join(" | ", problems.Take(5)) : "")}");
        var after = Snapshot(pkg, game, backups);
        var changed = before.Keys.Union(after.Keys).Where(k => !before.TryGetValue(k, out var a) || !after.TryGetValue(k, out var b) || a != b).ToList();
        result.Check(changed.Count == 0, $"uninstall: the game is exactly as before{(changed.Count > 0 ? $" ({changed.Count} differ: {string.Join(", ", changed.Take(5))})" : "")}");
    }

    /// <summary>
    /// The installer's warnings fail the test, except that the archive keys couldn't be loaded (no game given, or a
    /// stand-in exe): then only OPEN archives can be edited, and a step that needed an encrypted one fails on its own.
    /// </summary>
    static void CheckWarnings(List<string> warnings, string when, SandboxTestResult result)
    {
        const string NoKeys = "Couldn't load the game's archive keys";
        if (warnings.Any(w => w.StartsWith(NoKeys, StringComparison.Ordinal)) && !result.Notes.Any(n => n.StartsWith("no archive keys")))
            result.Notes.Add("no archive keys (no game exe to read them from): only OPEN archives can be edited");
        var real = warnings.Where(w => !w.StartsWith(NoKeys, StringComparison.Ordinal)).ToList();
        result.Check(real.Count == 0, $"{when}: no warnings{(real.Count > 0 ? " (" + real.Count + "): " + string.Join(" | ", real.Take(5)) : "")}");
    }

    /// <summary>
    /// Applies every &lt;xml&gt; edit to the game's file twice, literally, the way OpenIV and CodeWalker's installer do
    /// (the launcher journals dlclist.xml item by item, which would hide a pack listed again on every install): every
    /// step must find its node (OpenIV calls the install FAILED otherwise), a second install must list no pack twice,
    /// and no placeholder may be left.
    /// </summary>
    static void ReplayXml(OivPackage pkg, GameInstall game, RpfService rpf, SandboxTestResult result, HashSet<string> alreadyTwice)
    {
        void Edits(XElement parent, string? top, string prefix)
        {
            foreach (var step in parent.Elements())
            {
                var path = OivZip.Attr(step, "path");
                if (string.IsNullOrWhiteSpace(path)) continue;
                if (Is(step, "archive"))
                {
                    if (top == null) Edits(step, OivInstaller.Normalize(path), "");
                    else Edits(step, top, Join(prefix, path));
                    continue;
                }
                if (!Is(step, "xml")) continue;
                byte[]? data = top == null
                    ? (File.Exists(game.Resolve(OivInstaller.GameTarget(path))) ? File.ReadAllBytes(game.Resolve(OivInstaller.GameTarget(path))) : null)
                    : File.Exists(game.Resolve(Journal.ModsRel(top))) ? rpf.TryRead(game.Resolve(Journal.ModsRel(top)), Join(prefix, path)) : null;
                var name = (top != null ? top + " > " + Join(prefix, path) : path).Replace('\\', '/');
                if (data == null) { result.Notes.Add($"{name}: not in the sandbox, its edits weren't replayed"); continue; }
                var warnings = new List<string>();
                var once = OivEdits.ApplyXml(data, step, w => warnings.Add("first: " + w));
                var twice = OivEdits.ApplyXml(once, step, w => warnings.Add("second: " + w));
                result.Check(warnings.Count == 0, $"{name} as OpenIV applies it, twice: every step finds its node{(warnings.Count > 0 ? " (" + string.Join(" | ", warnings.Take(4)) + ")" : "")}");
                var text = OivZip.Decode(twice);
                if (name.EndsWith("dlclist.xml", StringComparison.OrdinalIgnoreCase))
                {
                    var dupes = DlcList.Items(text).GroupBy(i => i, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1 && !alreadyTwice.Contains(g.Key)).Select(g => g.Key).ToList();
                    result.Check(dupes.Count == 0, $"{name} as OpenIV applies it, twice: no pack listed twice{(dupes.Count > 0 ? " (" + string.Join(", ", dupes) + ")" : "")}");
                }
                if (LeftScratch(text)) result.Check(false, $"{name} as OpenIV applies it: a scratch element was left behind");
            }
        }
        Edits(pkg.Content, null, "");
    }

    /// <summary>Every &lt;add&gt; landed: loose files byte for byte, archive files with the same content.</summary>
    static void CheckAdds(OivPackage pkg, GameInstall game, RpfService rpf, SandboxTestResult result)
    {
        int ok = 0;
        var bad = new List<string>();
        foreach (var add in pkg.Content.Elements().Where(e => Is(e, "add")))
        {
            var src = Source(pkg, add);
            if (src == null) continue;
            var target = OivInstaller.GameTarget(add.Value.Trim().Length > 0 ? add.Value : OivZip.Attr(add, "source")!);
            var at = game.Resolve(target);
            if (File.Exists(at) && File.ReadAllBytes(at).AsSpan().SequenceEqual(File.ReadAllBytes(src))) ok++;
            else bad.Add(target);
        }
        void InArchive(XElement archive, string top, string prefix)
        {
            foreach (var step in archive.Elements())
            {
                if (Is(step, "archive"))
                {
                    var p = OivZip.Attr(step, "path") ?? "";
                    InArchive(step, top, Join(prefix, p));
                    continue;
                }
                if (!Is(step, "add")) continue;
                var src = Source(pkg, step);
                if (src == null) continue;
                var inner = Join(prefix, step.Value.Trim().Length > 0 ? step.Value.Trim() : Path.GetFileName(src));
                var data = rpf.TryRead(game.Resolve(Journal.ModsRel(top)), inner);
                if (data != null && RpfService.ContentHash(data) == RpfService.ContentHash(File.ReadAllBytes(src))) ok++;
                else bad.Add(top + " > " + inner);
            }
        }
        foreach (var archive in pkg.Content.Elements().Where(e => Is(e, "archive")))
            InArchive(archive, OivInstaller.Normalize(OivZip.Attr(archive, "path") ?? ""), "");
        result.Check(bad.Count == 0, $"every file installed where it belongs: {ok} OK{(bad.Count > 0 ? $", {bad.Count} not: {string.Join(", ", bad.Take(5))}" : "")}");
    }

    /// <summary>dlclist.xml / gameconfig.xml in the sandbox's update.rpf after an install: no pack listed twice, and what was raised.</summary>
    static Dictionary<string, string> Registration(OivPackage pkg, GameInstall game, RpfService rpf, SandboxTestResult result, string when, HashSet<string> alreadyTwice)
    {
        var texts = new Dictionary<string, string>();
        var update = game.ModsUpdateRpf;
        if (!File.Exists(update)) return texts;
        var list = rpf.TryRead(update, DlcList.InnerPath);
        if (list != null)
        {
            var text = DlcList.Decode(list, out _);
            texts["dlclist.xml"] = text;
            var items = DlcList.Items(text);
            var twice = items.GroupBy(i => i, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).Where(k => !alreadyTwice.Contains(k)).ToList();
            var registered = pkg.Content.Descendants().Where(e => Is(e, "Item")).Select(e => e.Value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var missing = registered.Where(r => !items.Contains(r, StringComparer.OrdinalIgnoreCase)).ToList();
            result.Check(twice.Count == 0, $"dlclist.xml {when}: no pack listed twice{(twice.Count > 0 ? " (" + string.Join(", ", twice) + ")" : "")}");
            if (registered.Count > 0)
                result.Check(missing.Count == 0, $"dlclist.xml {when}: lists {string.Join(", ", registered)}{(missing.Count > 0 ? "; missing " + string.Join(", ", missing) : "")}");
            if (LeftScratch(text)) result.Check(false, $"dlclist.xml {when}: a scratch element was left behind");
        }
        var config = rpf.TryRead(update, "common/data/gameconfig.xml");
        if (config != null)
        {
            var text = OivZip.Decode(config);
            texts["gameconfig.xml"] = text;
            if (LeftScratch(text)) result.Check(false, $"gameconfig.xml {when}: a scratch element was left behind");
            var doc = XDocument.Parse(text);
            foreach (var replace in pkg.Content.Descendants().Where(e => Is(e, "replace")))
                foreach (var want in replace.Elements())
                {
                    var value = OivZip.Attr(want, "value");
                    if (!int.TryParse(value, out var need)) continue;
                    var pool = System.Text.RegularExpressions.Regex.Match(OivZip.Attr(replace, "xpath") ?? "", @"PoolName='([^']+)'").Groups[1].Value;
                    var found = pool.Length > 0
                        ? doc.Descendants("Item").Where(i => i.Element("PoolName")?.Value == pool).SelectMany(i => i.Elements(want.Name.LocalName)).ToList()
                        : doc.Descendants(want.Name.LocalName).ToList();
                    var name = pool.Length > 0 ? pool : want.Name.LocalName;
                    var values = found.Select(f => int.TryParse(f.Attribute("value")?.Value, out var v) ? v : -1).ToList();
                    result.Check(values.Count == 1 && values[0] >= need,
                        $"gameconfig.xml {when}: {name} = {(values.Count == 0 ? "missing" : string.Join(" and ", values))} (needs at least {need}{(values.Count > 1 ? ", and only once" : "")})");
                }
        }
        return texts;
    }

    static HashSet<string> ListedTwice(GameInstall game, RpfService rpf)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(game.ModsUpdateRpf) && rpf.TryRead(game.ModsUpdateRpf, DlcList.InnerPath) is { } list)
            foreach (var g in DlcList.Items(DlcList.Decode(list, out _)).GroupBy(i => i, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                set.Add(g.Key);
        return set;
    }

    /// <summary>
    /// Hash of every file in the sandbox; the archives the package edits are hashed entry by entry (an archive's
    /// layout changes with every write, its files' contents are what count).
    /// </summary>
    static Dictionary<string, string> Snapshot(OivPackage pkg, GameInstall game, string backups)
    {
        var edited = pkg.Content.Elements().Where(e => Is(e, "archive"))
            .Select(a => game.Resolve(Journal.ModsRel(OivInstaller.Normalize(OivZip.Attr(a, "path") ?? ""))))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tools = new RpfTools(game);
        foreach (var file in Directory.EnumerateFiles(game.Root, "*", SearchOption.AllDirectories))
        {
            if (file.StartsWith(backups, StringComparison.OrdinalIgnoreCase)) continue;
            var rel = game.ToRelative(file);
            if (edited.Contains(Path.GetFullPath(file)))
            {
                foreach (var item in RpfTools.Walk(tools.Open(file)))
                    map[rel + " > " + item.InnerPath] = RpfTools.ContentHash(item);
                tools.Service.Forget(file);
            }
            else map[rel] = Journal.Sha256File(file);
        }
        return map;
    }

    /// <summary>A placeholder left in the XML: the generated steps' scratch elements all end in "Scratch".</summary>
    static bool LeftScratch(string xml)
    {
        try { return XDocument.Parse(xml).Descendants().Any(e => e.Name.LocalName.EndsWith("Scratch", StringComparison.OrdinalIgnoreCase)); }
        catch { return false; }
    }

    static string? Source(OivPackage pkg, XElement add)
    {
        var src = OivZip.Attr(add, "source");
        if (string.IsNullOrWhiteSpace(src)) return null;
        var full = Path.Combine(pkg.ContentRoot, OivInstaller.Normalize(src));
        return File.Exists(full) ? full : null;
    }

    static string Canonical(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            foreach (var t in doc.DescendantNodes().OfType<XText>().Where(t => string.IsNullOrWhiteSpace(t.Value)).ToList()) t.Remove();
            return doc.ToString(SaveOptions.DisableFormatting);
        }
        catch { return xml; }
    }

    static bool Is(XElement e, string name) => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase);
    static string Join(string a, string b)
    {
        var p = b.Replace('\\', '/').Trim().Trim('/');
        return a.Length == 0 ? p : a + "/" + p;
    }
}
