using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using VModLauncher.Core.Game;
using VModLauncher.Core.Install;
using VModLauncher.Core.Install.Oiv;
using VModLauncher.Core.Library;

// OivCheck: tests for the OIV installer.
//   OivCheck unit                          - xml/text step semantics on synthetic files (no game needed)
//   OivCheck setup <sandbox> <gameRoot>    - copies GTA5.exe + mods\update\update.rpf into a sandbox (game is only read)
//   OivCheck parse <oiv>...                - prints package metadata
//   OivCheck install <sandbox> <oiv>       - install twice, check dlclist/gameconfig/files, undo both, compare to before
namespace OivCheck;

static class Program
{
    static int failures;

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            switch (args.FirstOrDefault())
            {
                case "unit": Unit(); break;
                case "setup": Setup(args[1], args[2]); break;
                case "parse": foreach (var a in args.Skip(1)) Parse(a); break;
                case "install": InstallScenario(args[1], args[2]); break;
                case "fresh": FreshScenario(args[1], args[2]); break;
                default:
                    Console.WriteLine("usage: unit | setup <sandbox> <gameRoot> | parse <oiv>... | install <sandbox> <oiv>");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("EXCEPTION: " + ex);
            return 3;
        }
        Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what);
        if (!ok) failures++;
    }

    // ================================================================ unit

    const string DlcList =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<SMandatoryPacksData>\r\n\t<Paths>\r\n" +
        "\t\t<Item>platform:/dlcPacks/mpBeach/</Item>\r\n\t\t<Item>dlcpacks:/vihud/</Item>\r\n\t</Paths>\r\n</SMandatoryPacksData>\r\n";

    static byte[] Bom(string s) => new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(s)).ToArray();

    static XElement Step(string xml) => XElement.Parse(xml, LoadOptions.PreserveWhitespace);

    static void Unit()
    {
        Console.WriteLine("== xml: HUD scratch trick (document order, union xpath, first match)");
        var warnings = new List<string>();
        var step = Step(@"<xml path=""common\data\dlclist.xml"">
        <add xpath=""/SMandatoryPacksData"" append=""Last""><ViHudScratch /></add>
        <add xpath=""/SMandatoryPacksData/Paths[not(Item[.='dlcpacks:/vihud/'])] | /SMandatoryPacksData/ViHudScratch"" append=""Last""><Item>dlcpacks:/vihud/</Item></add>
        <add xpath=""/SMandatoryPacksData/Paths[not(Item[.='dlcpacks:/vihudanims/'])] | /SMandatoryPacksData/ViHudScratch"" append=""Last""><Item>dlcpacks:/vihudanims/</Item></add>
        <remove xpath=""/SMandatoryPacksData/ViHudScratch"" />
      </xml>");
        var input = Bom(DlcList);
        var output = OivEdits.ApplyXml(input, step, warnings.Add);
        var text = Encoding.UTF8.GetString(output, 3, output.Length - 3);
        Console.WriteLine(text.Replace("\r", "\\r").Replace("\n", "\\n\n    "));
        Check(output[0] == 0xEF && output[1] == 0xBB && output[2] == 0xBF, "BOM kept");
        Check(text.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<SMandatoryPacksData>"), "declaration kept byte for byte");
        Check(Count(text, "dlcpacks:/vihud/") == 1, "vihud (already there) listed once");
        Check(Count(text, "dlcpacks:/vihudanims/") == 1, "vihudanims added once");
        Check(!text.Contains("ViHudScratch"), "scratch element removed");
        Check(text.Contains("\t\t<Item>dlcpacks:/vihudanims/</Item>\r\n\t</Paths>"), "new item indented like its siblings, before </Paths>, CRLF");
        Check(!text.Replace("\r\n", "").Contains('\n'), "no bare LF introduced");
        Check(warnings.Count == 0, $"no warnings ({warnings.Count})");

        Console.WriteLine("== xml: no match is a warning, later steps still run");
        warnings.Clear();
        step = Step(@"<xml path=""x""><add xpath=""/SMandatoryPacksData/Nope"" append=""Last""><Item>a</Item></add>
                    <add xpath=""/SMandatoryPacksData/Paths"" append=""First""><Item>dlcpacks:/first/</Item></add>
                    <add xpath=""/SMandatoryPacksData/Paths"" append=""Last""><Item>dlcpacks:/last/</Item></add>
                    <frobnicate xpath=""/x"" /></xml>");
        output = OivEdits.ApplyXml(Encoding.UTF8.GetBytes(DlcList), step, warnings.Add);
        text = Encoding.UTF8.GetString(output);
        Check(warnings.Count == 2 && warnings[0].Contains("Unable to find") && warnings[1].Contains("Unknown"), $"2 warnings (no match, unknown step): {string.Join(" / ", warnings)}");
        var items = XDocument.Parse(text).Descendants("Item").Select(e => e.Value).ToList();
        Check(items.First() == "dlcpacks:/first/", "append=First puts it first");
        Check(items.Last() == "dlcpacks:/last/", "append=Last puts it last");
        Check(output.Take(3).SequenceEqual(Encoding.UTF8.GetBytes("<?x")), "no BOM added when there was none");

        Console.WriteLine("== xml: replace and remove (gameconfig pool, attribute)");
        warnings.Clear();
        const string gc = "<?xml version=\"1.0\"?>\n<fwAllConfigs>\n  <Entries>\n    <Item>\n      <PoolName>TxdStore</PoolName>\n      <PoolSize value=\"60000\"/>\n    </Item>\n    <Item>\n      <PoolName>Other</PoolName>\n      <PoolSize value=\"1\"/>\n    </Item>\n  </Entries>\n</fwAllConfigs>\n";
        step = Step(@"<xml path=""g""><replace xpath=""/fwAllConfigs/Entries/Item[PoolName='TxdStore']/PoolSize""><PoolSize value=""150000"" /></replace>
                    <replace xpath=""/fwAllConfigs/Entries/Item[PoolName='Other']/PoolSize/@value"">7</replace>
                    <remove xpath=""/fwAllConfigs/Entries/Item[PoolName='Missing']"" /></xml>");
        output = OivEdits.ApplyXml(Encoding.UTF8.GetBytes(gc), step, warnings.Add);
        text = Encoding.UTF8.GetString(output);
        var x = XDocument.Parse(text);
        Check(x.Descendants("Item").First().Element("PoolSize")!.Attribute("value")!.Value == "150000", "element replace");
        Check(x.Descendants("Item").Last().Element("PoolSize")!.Attribute("value")!.Value == "7", "attribute replace");
        Check(warnings.Count == 1, "remove with no match is a warning");
        Check(!text.Contains('\r'), "LF file stays LF");
        step = Step(@"<xml path=""g""><remove xpath=""/fwAllConfigs/Entries/Item[PoolName='Other']"" /></xml>");
        text = Encoding.UTF8.GetString(OivEdits.ApplyXml(output, step, warnings.Add));
        Check(!text.Contains("Other") && text.Contains("  </Entries>") && !text.Contains("\n\n"), "remove drops the element and its indent line");

        Console.WriteLine("== xml: untouched file comes back as the same bytes");
        step = Step(@"<xml path=""g""><remove xpath=""/nothing"" /></xml>");
        var same = Bom(DlcList);
        Check(ReferenceEquals(OivEdits.ApplyXml(same, step, _ => { }), same), "no-op returns the input");

        Console.WriteLine("== text steps");
        warnings.Clear();
        const string ini = "[Settings]\r\nSpeed=1\r\nDebug=0\r\n\r\n[Keys]\r\nMenu=F5\r\n";
        step = Step(@"<text path=""scripts\a.ini"">
          <insert where=""After"" line=""[Settings]"">NewLine=1</insert>
          <insert where=""Before"" line=""[Keys]"">; keys below</insert>
          <replace line=""Debug=0"">Debug=1</replace>
          <delete>Menu=F5</delete>
          <delete>NotThere=1</delete>
          <insert where=""Bottom"">
            Last=1
          </insert>
        </text>");
        output = OivEdits.ApplyText(Encoding.UTF8.GetBytes(ini), step, warnings.Add);
        text = Encoding.UTF8.GetString(output);
        Console.WriteLine("    " + text.Replace("\r", "\\r").Replace("\n", "\\n"));
        Check(text == "[Settings]\r\nNewLine=1\r\nSpeed=1\r\nDebug=1\r\n\r\n; keys below\r\n[Keys]\r\nLast=1\r\n", "insert/replace/delete result, CRLF kept, trailing newline kept");
        Check(warnings.Count == 1, "missing line is one warning");

        Console.WriteLine("== loose target mapping (mods redirect)");
        (string from, string to)[] map =
        {
            (@"scripts\GTA6Hud.dll", @"scripts\GTA6Hud.dll"),
            (@"ViMinimap.asi", @"ViMinimap.asi"),
            (@"update\x64\dlcpacks\vihud\dlc.rpf", @"mods\update\x64\dlcpacks\vihud\dlc.rpf"),
            (@"x64v.rpf", @"mods\x64v.rpf"),
            (@"x64\audio\sfx\foo.awc", @"mods\x64\audio\sfx\foo.awc"),
            (@"common\data\foo.meta", @"mods\common\data\foo.meta"),
            (@"mods\update\update.rpf", @"mods\update\update.rpf"),
            (@"plugins\LSPDFR\Foo.dll", @"plugins\LSPDFR\Foo.dll"),
            (@"menyooStuff\Spooner\map.xml", @"menyooStuff\Spooner\map.xml"),
            ("/scripts/Sub/x.ini", @"scripts\Sub\x.ini"),
        };
        foreach (var (from, to) in map)
            Check(OivInstaller.GameTarget(from) == to, $"{from} -> {OivInstaller.GameTarget(from)}");
    }

    static int Count(string s, string what)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(what, i, StringComparison.Ordinal)) >= 0) { n++; i += what.Length; }
        return n;
    }

    // ================================================================ setup / parse

    static void Setup(string sandbox, string gameRoot)
    {
        sandbox = Path.GetFullPath(sandbox);
        gameRoot = Path.GetFullPath(gameRoot);
        if (sandbox.StartsWith(gameRoot, StringComparison.OrdinalIgnoreCase) || gameRoot.StartsWith(sandbox, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("sandbox and game folders overlap");
        foreach (var rel in new[] { "GTA5.exe", @"mods\update\update.rpf" })
        {
            var src = Path.Combine(gameRoot, rel);
            var dst = Path.Combine(sandbox, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            var sw = Stopwatch.StartNew();
            using (var i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var o = new FileStream(dst, FileMode.Create, FileAccess.Write))
                i.CopyTo(o, 4 << 20);
            Console.WriteLine($"copied {rel} ({new FileInfo(dst).Length / 1048576} MB, {sw.Elapsed.TotalSeconds:F1}s)");
        }
        Directory.CreateDirectory(Path.Combine(sandbox, "scripts"));
    }

    static void Parse(string path)
    {
        using var pkg = OivPackage.Open(path);
        Console.WriteLine($"== {Path.GetFileName(path)}");
        Console.WriteLine($"  {pkg}");
        Console.WriteLine($"  id={pkg.Id} target={pkg.Target} edition={pkg.TargetEdition?.ToString() ?? "-"} icon={pkg.Icon?.Length ?? 0}B");
        Console.WriteLine($"  author link={pkg.AuthorLink ?? "-"} licence={(pkg.Licence?.Length ?? 0)} chars");
        Console.WriteLine($"  content root exists={Directory.Exists(pkg.ContentRoot)}; steps: " +
            string.Join(", ", pkg.Content.Descendants().GroupBy(e => e.Name.LocalName).Select(g => $"{g.Key}={g.Count()}")));
        Check(pkg.Name.Length > 0 && pkg.FormatVersion.Length > 0, "metadata read");
    }

    // ================================================================ install scenario

    static readonly string[] HudPacks = { "dlcpacks:/vihud/", "dlcpacks:/vihudanims/", "dlcpacks:/vihudfonts/" };

    static void InstallScenario(string sandbox, string oivPath)
    {
        var game = GameInstall.TryOpen(sandbox) ?? throw new InvalidOperationException("no GTA5.exe in sandbox");
        if (game.Root.StartsWith(@"D:\Games\GTAV\", StringComparison.OrdinalIgnoreCase) || game.Root.Equals(@"D:\Games\GTAV", StringComparison.OrdinalIgnoreCase)
            || game.Root.Contains("Program Files", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("refusing to run against a real game folder");
        var rpf = new RpfService(game);
        rpf.EnsureKeys();
        var modsRpf = game.ModsUpdateRpf;
        var backups = Path.Combine(sandbox, "_vml_backups");

        // ---- baseline: a dlclist without the HUD packs (written directly, not journaled)
        var listBytes = rpf.TryRead(modsRpf, VModLauncher.Core.Install.DlcList.InnerPath)!;
        var listText = VModLauncher.Core.Install.DlcList.Decode(listBytes, out var bom);
        var stripped = HudPacks.Aggregate(listText, (t, p) => VModLauncher.Core.Install.DlcList.Remove(t, p));
        if (stripped != listText) rpf.Write(modsRpf, VModLauncher.Core.Install.DlcList.InnerPath, VModLauncher.Core.Install.DlcList.Encode(stripped, bom));
        Console.WriteLine($"baseline dlclist: {VModLauncher.Core.Install.DlcList.Items(stripped).Count} items, HUD packs removed: {stripped != listText}");

        using var pkg = OivPackage.Open(oivPath);
        Console.WriteLine($"package: {pkg}");
        var scaleformEntries = pkg.Content.Descendants()
            .Where(e => e.Name.LocalName == "archive" && (e.Attribute("path")?.Value ?? "").EndsWith("scaleform_frontend.rpf"))
            .SelectMany(a => a.Elements("add")).Select(a => "x64/data/cdimages/scaleform_frontend.rpf/" + a.Value.Trim()).ToList();

        var before = Snapshot(game, rpf, scaleformEntries);
        Console.WriteLine($"baseline: {before.Files.Count} loose files, dlclist {before.DlcList[..12]}, gameconfig {before.GameConfig[..12]}, {before.Entries.Count(e => e.Value != null)}/{scaleformEntries.Count} scaleform entries present");

        // ---- install #1
        var mod1 = new InstalledMod { Name = pkg.Name, GameId = game.Id };
        var j1 = new Journal(game, rpf, mod1, backups);
        var sw = Stopwatch.StartNew();
        var inst1 = new OivInstaller(j1);
        inst1.Install(pkg, new Progress<string>(_ => { }));
        Console.WriteLine($"install #1: {sw.Elapsed.TotalSeconds:F1}s, {mod1.Entries.Count} journal entries, {inst1.Warnings.Count} warnings");
        foreach (var w in inst1.Warnings.Take(10)) Console.WriteLine("    warning: " + w);
        foreach (var g in mod1.Entries.GroupBy(e => e.Type)) Console.WriteLine($"    {g.Key}: {g.Count()}");

        var looseAdds = pkg.Content.Elements("add").Count();
        Check(mod1.Entries.Count(e => e.Type == JournalEntry.File) == looseAdds, $"one file entry per loose <add> ({looseAdds})");
        Check(File.Exists(Path.Combine(game.Root, @"scripts\GTA6Hud.dll")), @"scripts\GTA6Hud.dll in the game folder");
        Check(File.Exists(Path.Combine(game.Root, "ViMinimap.asi")), "ViMinimap.asi next to GTA5.exe");
        Check(File.Exists(Path.Combine(game.Root, @"mods\update\x64\dlcpacks\vihud\dlc.rpf")), @"vihud\dlc.rpf in mods\update\x64\dlcpacks");
        Check(!File.Exists(Path.Combine(game.Root, @"update\x64\dlcpacks\vihud\dlc.rpf")), "nothing written to the game's own update folder");
        CheckList(rpf, modsRpf, "after install #1");
        var gcText = Encoding.UTF8.GetString(rpf.TryRead(modsRpf, "common/data/gameconfig.xml")!);
        Check(gcText.Contains("<PoolName>TxdStore</PoolName>") && PoolValue(gcText, "TxdStore") == "150000", $"TxdStore pool = {PoolValue(gcText, "TxdStore")}");
        Check(PoolValue(gcText, "MetaDataStore") == "4000", $"MetaDataStore pool = {PoolValue(gcText, "MetaDataStore")}");
        var after1 = Snapshot(game, rpf, scaleformEntries);
        var changedSf = scaleformEntries.Count(e => after1.Entries[e] != before.Entries[e]);
        Check(scaleformEntries.All(e => after1.Entries[e] != null), $"all {scaleformEntries.Count} start-screen files inside scaleform_frontend.rpf ({changedSf} changed)");

        // ---- install #2 on top (packs already listed)
        var mod2 = new InstalledMod { Name = pkg.Name + " (again)", GameId = game.Id, InstalledAt = DateTime.UtcNow.AddSeconds(1) };
        var j2 = new Journal(game, rpf, mod2, backups);
        sw.Restart();
        var inst2 = new OivInstaller(j2);
        inst2.Install(pkg);
        Console.WriteLine($"install #2: {sw.Elapsed.TotalSeconds:F1}s, {mod2.Entries.Count} journal entries, {inst2.Warnings.Count} warnings");
        CheckList(rpf, modsRpf, "after install #2 (reinstall over itself)");

        // ---- undo both, newest first
        sw.Restart();
        var p2 = j2.UndoAll();
        var p1 = j1.UndoAll();
        Console.WriteLine($"undo: {sw.Elapsed.TotalSeconds:F1}s, problems: {p2.Count + p1.Count}");
        foreach (var p in p2.Concat(p1).Take(10)) Console.WriteLine("    problem: " + p);

        var after = Snapshot(game, rpf, scaleformEntries);
        Check(after.DlcList == before.DlcList, "dlclist bytes back to the baseline");
        Check(after.GameConfig == before.GameConfig, "gameconfig bytes back to the baseline");
        var notRestored = scaleformEntries.Where(e => after.Entries[e] != before.Entries[e]).ToList();
        Check(notRestored.Count == 0, $"scaleform_frontend entries back to the baseline ({scaleformEntries.Count - notRestored.Count}/{scaleformEntries.Count})");
        foreach (var e in notRestored.Take(5))
            Console.WriteLine($"    differs: {e} before={Short(before.Entries[e])} after={Short(after.Entries[e])}");
        var extra = after.Files.Keys.Except(before.Files.Keys).ToList();
        var missing = before.Files.Keys.Except(after.Files.Keys).ToList();
        Check(extra.Count == 0 && missing.Count == 0, $"loose files back to the baseline (extra {extra.Count}, missing {missing.Count})");
        foreach (var f in extra.Take(5)) Console.WriteLine("    extra: " + f);
        var leftDirs = Directory.EnumerateDirectories(game.Root, "*", SearchOption.AllDirectories)
            .Where(d => !d.StartsWith(backups, StringComparison.OrdinalIgnoreCase) && !Directory.EnumerateFileSystemEntries(d).Any()).ToList();
        Check(leftDirs.Count == 0 || leftDirs.All(d => d.EndsWith("scripts")), $"no empty folders left behind ({leftDirs.Count})");

        // ---- resource round-trip probe: does ReadRaw give back the bytes that were written?
        var ytd = Directory.EnumerateFiles(pkg.ContentRoot, "viload_0.ytd", SearchOption.AllDirectories).FirstOrDefault();
        if (ytd != null)
        {
            var src = File.ReadAllBytes(ytd);
            var probeMod = new InstalledMod { Name = "probe", GameId = game.Id };
            var jp = new Journal(game, rpf, probeMod, backups);
            jp.WriteRpf(@"update\update.rpf", "x64/data/cdimages/scaleform_frontend.rpf/vml_probe.ytd", src);
            var back = rpf.TryRead(modsRpf, "x64/data/cdimages/scaleform_frontend.rpf/vml_probe.ytd")!;
            Console.WriteLine($"resource round trip: wrote {src.Length}B sha {Journal.Sha256(src)[..12]}, read back {back.Length}B sha {Journal.Sha256(back)[..12]}");
            Console.WriteLine($"  content hash (flags + decompressed payload): wrote {ContentHash(src)[..12]}, read back {ContentHash(back)[..12]}, equal={ContentHash(src) == ContentHash(back)}");
            var probs = jp.UndoAll();
            var gone = rpf.TryRead(modsRpf, "x64/data/cdimages/scaleform_frontend.rpf/vml_probe.ytd") == null;
            Check(gone, $"probe resource removed by undo (problems {probs.Count})");
            if (!gone) rpf.Delete(modsRpf, "x64/data/cdimages/scaleform_frontend.rpf/vml_probe.ytd");
        }
        try { Directory.Delete(backups, true); } catch { }
    }

    static string Short(string? s) => s == null ? "absent" : s[..12];

    /// <summary>Hash that survives CodeWalker's recompression: RSC7 flags + the decompressed payload.</summary>
    static string ContentHash(byte[] data)
    {
        if (data.Length >= 16 && BitConverter.ToUInt32(data, 0) == 0x37435352)
        {
            var payload = CodeWalker.GameFiles.ResourceBuilder.Decompress(data.AsSpan(16).ToArray());
            return Journal.Sha256(data.AsSpan(4, 12).ToArray().Concat(payload).ToArray());
        }
        return Journal.Sha256(data);
    }

    static string GameFileStamp(string path)
    {
        var fi = new FileInfo(path);
        var head = new byte[65536];
        using (var s = File.OpenRead(path)) s.ReadExactly(head);
        return $"{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{Journal.Sha256(head)}";
    }

    // ================================================================ fresh game (no mods folder yet)

    static void FreshScenario(string sandbox, string oivPath)
    {
        var game = GameInstall.TryOpen(sandbox) ?? throw new InvalidOperationException("no GTA5.exe in sandbox");
        if (!game.Root.Contains("_vml_sandbox", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("sandbox only");
        if (Directory.Exists(game.ModsDir)) throw new InvalidOperationException("fresh scenario expects no mods folder");
        var rpf = new RpfService(game);
        var backups = Path.Combine(sandbox, "_vml_backups");
        rpf.EnsureKeys();
        var original = rpf.Open(game.GameUpdateRpf);
        Console.WriteLine($"game update.rpf: encryption {original.Encryption}");
        var origList = Journal.Sha256(rpf.TryRead(game.GameUpdateRpf, VModLauncher.Core.Install.DlcList.InnerPath)!);
        var origStamp = GameFileStamp(game.GameUpdateRpf);

        using var pkg = OivPackage.Open(oivPath);
        var mod = new InstalledMod { Name = pkg.Name, GameId = game.Id };
        var j = new Journal(game, rpf, mod, backups);
        var sw = Stopwatch.StartNew();
        var inst = new OivInstaller(j);
        inst.Install(pkg);
        Console.WriteLine($"install: {sw.Elapsed.TotalSeconds:F1}s, {mod.Entries.Count} entries ({string.Join(", ", mod.Entries.GroupBy(e => e.Type).Select(g => g.Key + "=" + g.Count()))}), {inst.Warnings.Count} warnings");
        foreach (var w in inst.Warnings.Take(10)) Console.WriteLine("    warning: " + w);

        Check(mod.Entries.Count(e => e.Type == JournalEntry.RpfCopy) == 1, "update.rpf copied into mods once (journaled)");
        CheckList(rpf, game.ModsUpdateRpf, "fresh install");
        Check(GameFileStamp(game.GameUpdateRpf) == origStamp && rpf.Open(game.GameUpdateRpf).Encryption == original.Encryption,
              "game's own update.rpf untouched (same size/time/header, still " + original.Encryption + ")");

        // encryption state after the edit (what the game + OpenIV.asi / OpenRPF will see)
        var top = rpf.Open(game.ModsUpdateRpf);
        var sf = top.Children.First(c => c.Name.Equals("scaleform_frontend.rpf", StringComparison.OrdinalIgnoreCase));
        var sfFiles = new List<CodeWalker.GameFiles.RpfFileEntry>();
        sf.GetFiles(sf.Root, sfFiles, true);
        var stillEncrypted = sfFiles.Count(f => f.IsEncrypted);
        var topFiles = new List<CodeWalker.GameFiles.RpfFileEntry>();
        top.GetFiles(top.Root, topFiles, false);
        Console.WriteLine($"mods update.rpf: {top.Encryption}; scaleform_frontend.rpf: {sf.Encryption}, {sfFiles.Count} entries, {stillEncrypted} still flagged NG-encrypted; top-level entries flagged encrypted: {topFiles.Count(f => f.IsEncrypted)}/{topFiles.Count}");
        Check(top.Encryption == CodeWalker.GameFiles.RpfEncryption.OPEN, "mods update.rpf header is OPEN");
        var landing = rpf.TryRead(game.ModsUpdateRpf, "x64/data/cdimages/scaleform_frontend.rpf/landing_page.gfx");
        var landingSrc = File.ReadAllBytes(Directory.EnumerateFiles(pkg.ContentRoot, "landing_page.gfx", SearchOption.AllDirectories).First());
        Check(landing != null && landing.SequenceEqual(landingSrc), "landing_page.gfx reads back identical from the converted nested archive");

        sw.Restart();
        var problems = j.UndoAll();
        Console.WriteLine($"undo: {sw.Elapsed.TotalSeconds:F1}s, problems {problems.Count}");
        foreach (var p in problems.Take(5)) Console.WriteLine("    problem: " + p);
        var listNow = Journal.Sha256(rpf.TryRead(game.ModsUpdateRpf, VModLauncher.Core.Install.DlcList.InnerPath)!);
        Check(listNow == origList, "mods copy's dlclist back to the game's original content");
        var leftover = Directory.EnumerateFiles(game.Root, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(@"\_vml_backups\") && !f.Equals(game.ModsUpdateRpf, StringComparison.OrdinalIgnoreCase)
                        && !f.Equals(game.GameUpdateRpf, StringComparison.OrdinalIgnoreCase) && !f.EndsWith("GTA5.exe"))
            .ToList();
        Check(leftover.Count == 0, $"no installed files left ({leftover.Count}); the mods update.rpf copy is kept by design");
        try { Directory.Delete(backups, true); } catch { }
    }

    static void CheckList(RpfService rpf, string modsRpf, string when)
    {
        var text = VModLauncher.Core.Install.DlcList.Decode(rpf.TryRead(modsRpf, VModLauncher.Core.Install.DlcList.InnerPath)!, out _);
        var items = VModLauncher.Core.Install.DlcList.Items(text);
        foreach (var p in HudPacks)
            Check(items.Count(i => i.Equals(p, StringComparison.OrdinalIgnoreCase)) == 1, $"{when}: {p} listed exactly once");
        Check(!text.Contains("ViHudScratch"), $"{when}: no ViHudScratch left");
        Check(XDocument.Parse(text.TrimStart('\uFEFF')).Root!.Name == "SMandatoryPacksData", $"{when}: dlclist still parses");
    }

    static string? PoolValue(string gameconfig, string pool)
    {
        var doc = XDocument.Parse(gameconfig.TrimStart('\uFEFF'));
        return doc.Descendants("Item").FirstOrDefault(i => (string?)i.Element("PoolName") == pool)?.Element("PoolSize")?.Attribute("value")?.Value;
    }

    sealed record Snap(Dictionary<string, string> Files, string DlcList, string GameConfig, Dictionary<string, string?> Entries);

    static Snap Snapshot(GameInstall game, RpfService rpf, List<string> entries)
    {
        var files = Directory.EnumerateFiles(game.Root, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(@"\_vml_backups\", StringComparison.OrdinalIgnoreCase)
                        && !f.Equals(game.ModsUpdateRpf, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => game.ToRelative(f), f => new FileInfo(f).Length.ToString(), StringComparer.OrdinalIgnoreCase);
        // one scan of the archive for all reads
        var top = rpf.Open(game.ModsUpdateRpf);
        string Hash(string inner)
        {
            var loc = rpf.Locate(top, inner, false);
            return Journal.Sha256(RpfService.ReadRaw(loc.Archive, loc.Entry!));
        }
        var map = new Dictionary<string, string?>();
        foreach (var e in entries)
        {
            try
            {
                var loc = rpf.Locate(top, e, false);
                map[e] = loc.Entry == null ? null : Journal.Sha256(RpfService.ReadRaw(loc.Archive, loc.Entry));
            }
            catch { map[e] = null; }
        }
        return new Snap(files, Hash("common/data/dlclist.xml"), Hash("common/data/gameconfig.xml"), map);
    }
}
