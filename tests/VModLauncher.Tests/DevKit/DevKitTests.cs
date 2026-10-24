using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using CodeWalker.GameFiles;
using VModLauncher.Core.Install;
using VModLauncher.Core.Install.Oiv;
using VModLauncher.DevKit;
using VModLauncher.DevKit.Mcp;
using VModLauncher.DevKit.Oiv;
using VModLauncher.DevKit.Scan;
using VModLauncher.DevKit.Shvdn;
using Xunit;

namespace VModLauncher.Tests.DevKit;

/// <summary>A mod folder and a project file in a temp folder.</summary>
sealed class ModWorkspace : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "vml-devkit-" + Guid.NewGuid().ToString("N")[..8]);

    public ModWorkspace() => Directory.CreateDirectory(Root);

    public string File(string rel, string text = "x")
    {
        var p = Path.Combine(Root, rel.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, text);
        return p;
    }

    public string Bytes(string rel, byte[] data)
    {
        var p = Path.Combine(Root, rel.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllBytes(p, data);
        return p;
    }

    public OivProject Project(string json, Dictionary<string, string>? set = null)
    {
        var p = File("mod.oiv.json", json);
        return OivProject.Load(p, set);
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch { }
    }
}

public class OivBuilderTests
{
    const string Basic = """
        {
          // comments and trailing commas are fine
          "name": "Test Mod", "version": "1.2.3", "edition": "Legacy", "author": "Me",
          "description": "Does things.",
          "output": "out/{name}-{version}-{edition}.oiv",
          "files": [
            { "from": "scripts", "to": "scripts", "exclude": ["**/Save/**", "*.pdb"] },
            { "from": "pack/dlc.rpf", "to": "update/x64/dlcpacks/testpack/dlc.rpf", "content": "dlcpacks/testpack/dlc.rpf" },
          ],
          "registerPacks": ["testpack"],
          "raise": { "TxdStore": 150000, "ConfigModelInfo/MaxExtraPedModelInfos": 600 },
        }
        """;

    static ModWorkspace Mod()
    {
        var w = new ModWorkspace();
        w.File("scripts/MyMod.dll", "dll");
        w.File("scripts/MyMod.pdb", "symbols");
        w.File("scripts/MyMod/config.ini", "[a]");
        w.File("scripts/MyMod/Save/progress.txt", "player data");
        Sandbox.MakeDlcRpf(Path.Combine(w.Root, "pack", "dlc.rpf"), "testpack");
        return w;
    }

    [Fact]
    public void Builds_a_package_OpenIV_and_the_launcher_can_read()
    {
        using var w = Mod();
        var r = OivBuilder.Build(w.Project(Basic));

        Assert.Equal(Path.Combine(w.Root, "out", "Test-Mod-1.2.3-Legacy.oiv"), r.Output);
        using var zip = ZipFile.OpenRead(r.Output);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("assembly.xml", names);
        Assert.Contains("content/scripts/MyMod.dll", names);
        Assert.Contains("content/scripts/MyMod/config.ini", names);
        Assert.Contains("content/dlcpacks/testpack/dlc.rpf", names);
        Assert.DoesNotContain(names, n => n.Contains("Save/") || n.EndsWith(".pdb"));
        // real dates, so Script Hook V .NET's cache sees an update
        var dll = zip.GetEntry("content/scripts/MyMod.dll")!;
        Assert.True(Math.Abs((dll.LastWriteTime.LocalDateTime - File.GetLastWriteTime(Path.Combine(w.Root, "scripts", "MyMod.dll"))).TotalSeconds) <= 2);
        // archives stored, not deflated again
        Assert.Equal(zip.GetEntry("content/dlcpacks/testpack/dlc.rpf")!.Length, zip.GetEntry("content/dlcpacks/testpack/dlc.rpf")!.CompressedLength);

        using var pkg = OivPackage.Open(r.Output);
        Assert.Equal("Test Mod", pkg.Name);
        Assert.Equal("2.1", pkg.FormatVersion);
        Assert.Equal(1, pkg.Major);
        Assert.Equal(2, pkg.Minor);
        Assert.Equal("1.2.3", pkg.Tag);
        Assert.Equal(OivBuilder.StableId("Test Mod"), pkg.Id);
        var adds = pkg.Content.Elements("add").ToList();
        Assert.Contains(adds, a => a.Attribute("source")!.Value == @"scripts\MyMod.dll" && a.Value == @"scripts\MyMod.dll");
        Assert.Contains(adds, a => a.Value == @"update\x64\dlcpacks\testpack\dlc.rpf");
        var reg = pkg.Content.Elements("archive").Single(a => a.Attribute("path")!.Value == @"update\update.rpf");
        Assert.Equal("True", reg.Attribute("createIfNotExist")!.Value);
        Assert.Contains("dlcpacks:/testpack/", reg.ToString());
    }

    [Fact]
    public void The_same_name_gives_the_same_id_so_versions_update_each_other()
    {
        Assert.Equal(OivBuilder.StableId("My Mod"), OivBuilder.StableId(" my mod "));
        Assert.NotEqual(OivBuilder.StableId("My Mod"), OivBuilder.StableId("Other"));
        Assert.Matches(@"^\{[0-9a-f-]{36}\}$", OivBuilder.StableId("My Mod"));
    }

    [Fact]
    public void Two_files_installing_to_one_place_stop_the_build()
    {
        using var w = Mod();
        w.File("other/MyMod.dll", "another dll");
        var p = w.Project("""{"name":"x","version":"1.0","files":[{"from":"scripts","to":"scripts"},{"from":"other/MyMod.dll","to":"scripts/MyMod.dll","content":"other/MyMod.dll"}]}""");
        var ex = Assert.Throws<InvalidDataException>(() => OivBuilder.Plan(p));
        Assert.Contains(@"scripts\MyMod.dll", ex.Message);
    }

    [Fact]
    public void A_texture_dictionary_in_the_other_editions_format_stops_the_build()
    {
        using var w = new ModWorkspace();
        var header = new byte[32];
        BitConverter.GetBytes(0x37435352u).CopyTo(header, 0); // RSC7
        BitConverter.GetBytes(5).CopyTo(header, 4); // gen9
        w.Bytes("textures/a.ytd", header);
        var legacy = w.Project("""{"name":"x","version":"1.0","edition":"Legacy","files":[{"from":"textures","to":"textures"}]}""");
        Assert.Contains("needs 13", Assert.Throws<InvalidDataException>(() => OivBuilder.Plan(legacy)).Message);
        var enhanced = w.Project("""{"name":"x","version":"1.0","edition":"Enhanced","files":[{"from":"textures","to":"textures"}]}""");
        OivBuilder.Plan(enhanced);
    }

    [Fact]
    public void Variables_come_from_vars_overrides_and_the_environment_and_unknown_ones_are_errors()
    {
        using var w = Mod();
        var p = w.Project("""{"name":"x","version":"1.0","vars":{"dir":"scripts"},"files":[{"from":"${dir}","to":"scripts"}]}""");
        Assert.Equal(Path.Combine(w.Root, "scripts"), p.ResolvePath("${dir}"));
        var o = w.Project("""{"name":"x","version":"1.0","vars":{"dir":"nope"},"files":[{"from":"${dir}","to":"scripts"}]}""", new() { ["dir"] = "scripts" });
        Assert.NotEmpty(OivBuilder.Plan(o).Files);
        Environment.SetEnvironmentVariable("VML_TEST_DIR", "scripts");
        Assert.Equal("scripts", p.Expand("${env:VML_TEST_DIR}"));
        Assert.Throws<InvalidDataException>(() => p.Expand("${missing}"));
    }

    [Fact]
    public void A_template_keeps_its_metadata_and_own_steps_and_takes_the_generated_ones_at_the_marker()
    {
        using var w = Mod();
        w.File("template.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <package version="2.0" id="{00000000-0000-0000-0000-000000000001}" target="Five">
              <metadata><name>From Template</name><version><major>3</major><minor>1</minor><tag>LEGACY 3.1.0</tag></version><author><displayName>T</displayName></author><description>d</description></metadata>
              <content>
                <delete>scripts\Old.dll</delete>
                <!-- vml:content -->
              </content>
            </package>
            """);
        var p = w.Project("""{"assemblyTemplate":"template.xml","edition":"Legacy","files":[{"from":"scripts/MyMod.dll","to":"scripts/MyMod.dll"}]}""");
        var plan = OivBuilder.Plan(p);
        var doc = XDocument.Parse(plan.AssemblyXml);
        Assert.Equal("From Template", doc.Root!.Element("metadata")!.Element("name")!.Value);
        var steps = doc.Root.Element("content")!.Elements().ToList();
        Assert.Equal("delete", steps[0].Name.LocalName);
        Assert.Equal("add", steps[1].Name.LocalName);
        Assert.EndsWith(@"build\From-Template-3.1.0.oiv", OivBuilder.OutputPath(p, plan)); // the version from the template's tag
    }

    [Fact]
    public void Globs_match_like_gitignore()
    {
        Assert.True(new Glob("**/Save/**").IsMatch("Save/a.txt"));
        Assert.True(new Glob("**/Save/**").IsMatch("x/Save/y/b.txt"));
        Assert.False(new Glob("**/Save/**").IsMatch("Saver/a.txt"));
        Assert.True(new Glob("Art/**/*.png").IsMatch("Art/a.png"));
        Assert.True(new Glob("Art/**/*.png").IsMatch("art/x/y/a.PNG"));
        Assert.False(new Glob("*.pdb").IsMatch("sub/a.pdb"));
        Assert.True(new Glob("**/*.pdb").IsMatch("sub/a.pdb"));
    }
}

public class OivStepsTests
{
    const string GameConfig = """
        <?xml version="1.0" encoding="UTF-8"?>
        <fwAllConfigs><ConfigArray><Item><Config>
          <PoolSizes><Entries>
            <Item><PoolName>TxdStore</PoolName><PoolSize value="100000" /></Item>
            <Item><PoolName>DwdStore</PoolName><PoolSize value="50000" /></Item>
          </Entries></PoolSizes>
          <ConfigModelInfo><MaxExtraPedModelInfos value="300" /></ConfigModelInfo>
        </Config></Item></ConfigArray></fwAllConfigs>
        """;

    const string List = """
        <?xml version="1.0" encoding="UTF-8"?>
        <SMandatoryPacksData>
          <Paths>
            <Item>dlcpacks:/mpbeach/</Item>
            <Item>dlcpacks:/already/</Item>
          </Paths>
        </SMandatoryPacksData>
        """;

    static string Apply(string xml, string step)
    {
        var warnings = new List<string>();
        var result = OivEdits.ApplyXml(Encoding.UTF8.GetBytes(xml), XElement.Parse(step), warnings.Add);
        Assert.Empty(warnings); // every step found its node: OpenIV would call the install FAILED otherwise
        return Encoding.UTF8.GetString(result);
    }

    static int Value(string xml, string pool) => int.Parse(XDocument.Parse(xml).Descendants("Item")
        .Single(i => i.Element("PoolName")?.Value == pool).Element("PoolSize")!.Attribute("value")!.Value);

    [Fact]
    public void Raises_a_lower_limit_keeps_a_higher_one_and_leaves_one_element_each_time()
    {
        var step = OivSteps.RaiseGameConfig(new Dictionary<string, int> { ["TxdStore"] = 150000, ["DwdStore"] = 30000, ["ConfigModelInfo/MaxExtraPedModelInfos"] = 600 }, "VmlScratch", "");
        var once = Apply(GameConfig, step);
        var twice = Apply(once, step);
        foreach (var xml in new[] { once, twice })
        {
            Assert.Equal(150000, Value(xml, "TxdStore"));
            Assert.Equal(50000, Value(xml, "DwdStore")); // another mod's higher limit stays
            var doc = XDocument.Parse(xml);
            Assert.Single(doc.Descendants("ConfigModelInfo").Elements("MaxExtraPedModelInfos"));
            Assert.Equal("600", doc.Descendants("MaxExtraPedModelInfos").Single().Attribute("value")!.Value);
            Assert.All(doc.Descendants("Item").Where(i => i.Element("PoolName") != null), i => Assert.Single(i.Elements("PoolSize")));
            Assert.Empty(doc.Descendants("VmlScratch"));
        }
    }

    [Fact]
    public void Registers_each_pack_once_however_often_it_installs()
    {
        var step = OivSteps.RegisterPacks(new[] { "already", "newpack", "dlcpacks:/other/" }, "VmlScratch", "");
        var once = Apply(List, step);
        var twice = Apply(once, step);
        foreach (var xml in new[] { once, twice })
        {
            var items = DlcList.Items(xml);
            Assert.Equal(new[] { "dlcpacks:/mpbeach/", "dlcpacks:/already/", "dlcpacks:/newpack/", "dlcpacks:/other/" }, items);
            Assert.DoesNotContain("VmlScratch", xml);
        }
    }

    [Fact]
    public void Pack_names_that_would_break_the_xpath_are_refused()
    {
        Assert.Throws<InvalidDataException>(() => OivSteps.PackItem("a'b"));
        Assert.Throws<InvalidDataException>(() => OivSteps.PackItem("a/b"));
        Assert.Equal("dlcpacks:/x/", OivSteps.PackItem("x"));
    }
}

public class OivInspectorTests
{
    static string Package(ModWorkspace w, string content, bool sameDates = false, params (string Path, string Text)[] files)
    {
        var path = Path.Combine(w.Root, "test.oiv");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var asm = zip.CreateEntry("assembly.xml");
        using (var s = new StreamWriter(asm.Open()))
            s.Write($"<?xml version=\"1.0\"?><package version=\"2.1\" target=\"Five\"><metadata><name>T</name><version><major>1</major><minor>0</minor></version><author><displayName>a</displayName></author></metadata><content>{content}</content></package>");
        int i = 0;
        foreach (var (p, text) in files)
        {
            var e = zip.CreateEntry("content/" + p);
            e.LastWriteTime = sameDates ? new DateTime(2020, 1, 1) : new DateTime(2020, 1, 1).AddMinutes(i++);
            using var s = new StreamWriter(e.Open());
            s.Write(text);
        }
        return path;
    }

    [Fact]
    public void Missing_files_are_errors_and_unused_ones_warnings()
    {
        using var w = new ModWorkspace();
        var r = OivInspector.Inspect(Package(w, """<add source="a.dll">scripts\a.dll</add><add source="gone.dll">scripts\gone.dll</add>""", false, ("a.dll", "1"), ("unused.txt", "2")));
        Assert.Contains(r.Lints, l => l.Level == LintLevel.Error && l.Message.Contains("gone.dll"));
        Assert.Contains(r.Lints, l => l.Level == LintLevel.Warning && l.Message.Contains("unused.txt"));
        Assert.Contains(r.Lints, l => l.Level == LintLevel.Info && l.Message.Contains("<gameversion>"));
    }

    [Fact]
    public void An_unguarded_dlclist_add_and_one_date_for_every_file_are_flagged()
    {
        using var w = new ModWorkspace();
        var content = """<archive path="update\update.rpf" createIfNotExist="True" type="RPF7"><xml path="common\data\dlclist.xml"><add xpath="/SMandatoryPacksData/Paths" append="Last"><Item>dlcpacks:/x/</Item></add></xml></archive>""" +
                      string.Concat(Enumerable.Range(0, 5).Select(i => $"<add source=\"f{i}.txt\">f{i}.txt</add>"));
        var r = OivInspector.Inspect(Package(w, content, true, Enumerable.Range(0, 5).Select(i => ($"f{i}.txt", $"{i}")).ToArray()));
        Assert.Contains(r.Lints, l => l.Message.Contains("every reinstall lists the pack again"));
        Assert.Contains(r.Lints, l => l.Message.Contains("same date"));
        Assert.Contains("dlcpacks:/x/", r.RegisteredPacks);
    }

    [Fact]
    public void A_package_vml_builds_has_no_warnings()
    {
        using var w = new ModWorkspace();
        w.File("scripts/A.dll", "a");
        Sandbox.MakeDlcRpf(Path.Combine(w.Root, "p", "dlc.rpf"), "p");
        var r = OivBuilder.Build(w.Project("""{"name":"Clean","version":"1.0","edition":"Legacy","gameVersionElement":true,"files":[{"from":"scripts","to":"scripts"},{"from":"p/dlc.rpf","to":"update/x64/dlcpacks/p/dlc.rpf"}],"registerPacks":["p"],"raise":{"TxdStore":150000}}"""));
        var report = OivInspector.Inspect(r.Output);
        Assert.Empty(report.Lints);
        Assert.Equal("Legacy", report.GameVersion);
    }

    [Fact]
    public void Diff_lists_what_changed_between_two_builds()
    {
        using var w = new ModWorkspace();
        w.File("scripts/A.dll", "a");
        w.File("scripts/B.dll", "b");
        var p = w.Project("""{"name":"D","version":"1.0","output":"one.oiv","files":[{"from":"scripts","to":"scripts"}]}""");
        var one = OivBuilder.Build(p).Output;
        w.File("scripts/B.dll", "b changed");
        w.File("scripts/C.dll", "c");
        File.Delete(Path.Combine(w.Root, "scripts", "A.dll"));
        var two = OivBuilder.Build(w.Project("""{"name":"D","version":"1.1","output":"two.oiv","files":[{"from":"scripts","to":"scripts"}]}""")).Output;
        var d = OivDiff.Compare(one, two);
        Assert.Contains(d.Added, x => x.Entry == "content/scripts/C.dll");
        Assert.Contains("content/scripts/A.dll", d.Removed);
        Assert.Contains(d.Changed, x => x.Entry == "content/scripts/B.dll");
        Assert.Contains(d.AssemblyDiff, l => l.StartsWith("-") && l.Contains("A.dll"));
        Assert.True(d.AssemblyDiff.FindIndex(l => l.StartsWith("-")) < d.AssemblyDiff.FindIndex(l => l.StartsWith("+")));
    }

    [Fact]
    public void Verify_finds_a_file_that_changed_since_the_build()
    {
        using var w = new ModWorkspace();
        w.File("scripts/A.dll", "a");
        var p = w.Project("""{"name":"V","version":"1.0","files":[{"from":"scripts","to":"scripts"}]}""");
        var oiv = OivBuilder.Build(p).Output;
        Assert.True(OivVerify.AgainstProject(oiv, p).Ok);
        Assert.True(OivVerify.AgainstFolders(oiv, new Dictionary<string, string> { ["scripts"] = Path.Combine(w.Root, "scripts") }).Ok);
        w.File("scripts/A.dll", "edited");
        var r = OivVerify.AgainstProject(oiv, p);
        Assert.False(r.Ok);
        Assert.Single(r.Differ);
        Assert.Single(r.Hashes);
    }
}

public class OivSandboxTestTests
{
    /// <summary>A stand-in "real" game: an OPEN update.rpf with a dlclist and a gameconfig.</summary>
    static Sandbox RealGame()
    {
        var s = new Sandbox();
        var svc = new RpfService(s.Game);
        svc.Write(s.Game.ModsUpdateRpf, "common/data/gameconfig.xml", Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<fwAllConfigs><ConfigArray><Item><Config><PoolSizes><Entries>" +
            "<Item><PoolName>TxdStore</PoolName><PoolSize value=\"100000\" /></Item></Entries></PoolSizes>" +
            "<ConfigModelInfo><MaxExtraPedModelInfos value=\"300\" /></ConfigModelInfo></Config></Item></ConfigArray></fwAllConfigs>\r\n"));
        return s;
    }

    static string Build(ModWorkspace w)
    {
        w.File("scripts/A.dll", "a");
        w.File("readme.txt", "hi");
        Sandbox.MakeDlcRpf(Path.Combine(w.Root, "pack", "dlc.rpf"), "mypack");
        return OivBuilder.Build(w.Project("""
            {"name":"Sandboxed","version":"1.0","edition":"Legacy",
             "files":[{"from":"scripts","to":"scripts"},{"from":"pack/dlc.rpf","to":"update/x64/dlcpacks/mypack/dlc.rpf"}],
             "archives":[{"path":"update/update.rpf","create":true,"files":[{"from":"readme.txt","to":"common/data/vml_test.txt"}]}],
             "registerPacks":["mypack"],"raise":{"TxdStore":150000,"ConfigModelInfo/MaxExtraPedModelInfos":600}}
            """)).Output;
    }

    static string Fingerprint(string root) => string.Join("\n", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order()
        .Select(f => f + " " + Journal.Sha256File(f)));

    [Fact]
    public void Installs_reinstalls_and_uninstalls_cleanly_and_never_touches_the_real_game()
    {
        using var real = RealGame();
        using var w = new ModWorkspace();
        var oiv = Build(w);
        var before = Fingerprint(real.GameRoot);

        var r = OivSandboxTest.Run(oiv, real.GameRoot);

        Assert.True(r.Passed, r.ToText());
        Assert.Contains(r.Checks, c => c.What.Contains("TxdStore = 150000"));
        Assert.Contains(r.Checks, c => c.What.Contains("lists dlcpacks:/mypack/"));
        Assert.Equal(before, Fingerprint(real.GameRoot));
        Assert.False(Directory.Exists(r.Sandbox));
    }

    [Fact]
    public void A_fresh_install_raises_the_lowered_limits()
    {
        using var real = RealGame();
        using var w = new ModWorkspace();
        var r = OivSandboxTest.Run(Build(w), real.GameRoot, fresh: true);
        Assert.True(r.Passed, r.ToText());
        Assert.Contains(r.Notes, n => n.Contains("lowered TxdStore 75000"));
    }

    [Fact]
    public void A_package_that_lists_a_pack_again_on_every_install_fails()
    {
        using var real = RealGame();
        using var w = new ModWorkspace();
        w.File("steps.xml", """
            <archive path="update\update.rpf" createIfNotExist="True" type="RPF7">
              <xml path="common\data\dlclist.xml">
                <add xpath="/SMandatoryPacksData/Paths" append="Last"><Item>dlcpacks:/naive/</Item></add>
              </xml>
            </archive>
            """);
        var oiv = OivBuilder.Build(w.Project("""{"name":"Naive","version":"1.0","steps":["@steps.xml"]}""")).Output;
        var r = OivSandboxTest.Run(oiv, real.GameRoot);
        Assert.False(r.Passed);
        Assert.Contains(r.Checks, c => !c.Ok && c.What.Contains("dlcpacks:/naive/"));
    }
}

public class ToolBitsTests
{
    [Fact]
    public void Reads_the_shadow_cache_info_file_the_NET_Framework_writes()
    {
        var data = Encoding.Unicode.GetBytes("ViCrouch,2.6.0.0,, file:///D:/Games/GTAV/scripts/ViCrouch.DLL\0");
        var (name, version, url) = ShvdnCache.ParseInfo(data);
        Assert.Equal("ViCrouch", name);
        Assert.Equal("2.6.0.0", version);
        Assert.Equal(@"D:\Games\GTAV\scripts\ViCrouch.DLL", url);
    }

    [Fact]
    public void Blocklist_scan_finds_renamed_copies_and_copies_inside_packages()
    {
        using var w = new ModWorkspace();
        var secret = w.File("private/tool.cfg", "do not ship this " + Guid.NewGuid());
        w.File("release/renamed.txt", File.ReadAllText(secret));
        w.File("release/fine.txt", "public");
        using (var zip = ZipFile.Open(Path.Combine(w.Root, "release", "pkg.oiv"), ZipArchiveMode.Create))
            zip.CreateEntryFromFile(secret, "content/x.cfg");
        var r = BlocklistScan.Run(Path.Combine(w.Root, "private"), new[] { Path.Combine(w.Root, "release") });
        Assert.False(r.Clean);
        Assert.Equal(2, r.Matches.Count);
        Assert.Contains(r.Matches, m => m.Found.EndsWith("renamed.txt"));
        Assert.Contains(r.Matches, m => m.Found.EndsWith("pkg.oiv > content/x.cfg"));
    }

    [Fact]
    public void The_tools_refuse_to_write_inside_a_game_folder()
    {
        using var s = new Sandbox();
        Assert.Throws<InvalidOperationException>(() => Safety.EnsureNotInGame(Path.Combine(s.GameRoot, "scripts", "x.oiv")));
        Safety.EnsureNotInGame(Path.Combine(s.Root, "elsewhere", "x.oiv"));
    }
}

public class McpServerTests
{
    static readonly McpServer Server = DevKitTools.Server("test");

    static JsonObject Send(string json) => (JsonObject)Server.HandleLine(json)!;

    [Fact]
    public void Initialize_agrees_a_protocol_version()
    {
        var r = Send("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{}}}""");
        Assert.Equal("2025-03-26", r["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.NotNull(r["result"]!["capabilities"]!["tools"]);
        var unknown = Send("""{"jsonrpc":"2.0","id":2,"method":"initialize","params":{"protocolVersion":"1999-01-01"}}""");
        Assert.Equal("2025-06-18", unknown["result"]!["protocolVersion"]!.GetValue<string>());
    }

    [Fact]
    public void Lists_every_tool_with_a_schema()
    {
        var tools = (JsonArray)Send("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")["result"]!["tools"]!;
        Assert.True(tools.Count >= 18);
        Assert.All(tools, t =>
        {
            Assert.Equal("object", t!["inputSchema"]!["type"]!.GetValue<string>());
            Assert.False(string.IsNullOrWhiteSpace(t["description"]!.GetValue<string>()));
        });
        Assert.Contains(tools, t => t!["name"]!.GetValue<string>() == "oiv_sandbox_test");
    }

    [Fact]
    public void Tool_problems_come_back_as_tool_errors_and_unknown_methods_as_protocol_errors()
    {
        var missing = Send("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"oiv_info","arguments":{}}}""");
        Assert.True(missing["result"]!["isError"]!.GetValue<bool>());
        var notFound = Send("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"oiv_info","arguments":{"oiv":"C:\\nope\\x.oiv"}}}""");
        Assert.True(notFound["result"]!["isError"]!.GetValue<bool>());
        var method = Send("""{"jsonrpc":"2.0","id":3,"method":"nope"}""");
        Assert.Equal(-32601, method["error"]!["code"]!.GetValue<int>());
        Assert.Null(Server.HandleLine("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
        Assert.Equal(-32700, ((JsonObject)Server.HandleLine("{not json")!)["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void A_tool_call_returns_its_text()
    {
        using var w = new ModWorkspace();
        w.File("scripts/A.dll", "a");
        var oiv = OivBuilder.Build(w.Project("""{"name":"M","version":"1.0","files":[{"from":"scripts","to":"scripts"}]}""")).Output;
        var call = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 9, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = "oiv_info", ["arguments"] = new JsonObject { ["oiv"] = oiv } },
        };
        var r = Send(call.ToJsonString());
        Assert.False(r["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("M 1.0", r["result"]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(9, r["id"]!.GetValue<int>());
    }
}
