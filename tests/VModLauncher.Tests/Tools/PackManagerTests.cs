using System.Text;
using VModLauncher.Core;
using VModLauncher.Core.Install;
using VModLauncher.Core.Library;
using VModLauncher.Core.Tools;
using Xunit;

namespace VModLauncher.Tests.Tools;

public class PackManagerTests
{
    /// <summary>A file that starts like one of Rockstar's encrypted (NG) archives.</summary>
    static void FakeRockstarArchive(string path, uint encryption = 0x0FEFFFFF)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = new byte[64];
        BitConverter.GetBytes(0x52504637u).CopyTo(data, 0);  // RPF7
        BitConverter.GetBytes(encryption).CopyTo(data, 12);
        File.WriteAllBytes(path, data);
    }

    static void SetList(Sandbox s, params string[] items)
    {
        var xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<SMandatoryPacksData>\r\n\t<Paths>\r\n"
                  + string.Concat(items.Select(i => $"\t\t<Item>{i}</Item>\r\n")) + "\t</Paths>\r\n</SMandatoryPacksData>\r\n";
        new RpfService(s.Game).Write(s.Game.ModsUpdateRpf, DlcList.InnerPath, Encoding.UTF8.GetBytes(xml));
    }

    static List<string> ListItems(Sandbox s) =>
        DlcList.Items(DlcList.Decode(new RpfService(s.Game).TryRead(s.Game.ModsUpdateRpf, DlcList.InnerPath)!, out _));

    static PackManagerReport Report(Sandbox s)
    {
        using (RpfService.Session(s.Game)) return PackManager.List(s.Game, s.Library);
    }

    static InstalledMod InstallPack(Sandbox s, string name)
    {
        Directory.CreateDirectory(s.ModRoot);
        foreach (var d in Directory.GetDirectories(s.ModRoot)) Directory.Delete(d, true);
        Sandbox.MakeDlcRpf(Path.Combine(s.ModRoot, name, "dlc.rpf"), name);
        var plan = new ModAnalyzer(s.Game).Analyze(s.ModRoot, name);
        return new ModInstaller(s.Game, s.Paths, s.Library).Install(plan, new InstalledMod { Name = name });
    }

    [Fact]
    public void Lists_every_status_the_player_needs_to_know_about()
    {
        using var s = new Sandbox();
        FakeRockstarArchive(Path.Combine(s.GameRoot, @"update\x64\dlcpacks\mpbeach\dlc.rpf"));
        var car = InstallPack(s, "car");
        Sandbox.MakeDlcRpf(Path.Combine(s.GameRoot, @"mods\update\x64\dlcpacks\orphan\dlc.rpf"), "orphan");
        SetList(s, "platform:/dlc_patch/patchday1ng/", "dlcpacks:/mpbeach/", "dlcpacks:/car/", "dlcpacks:/car/", "dlcpacks:/ghost/");

        var r = Report(s);
        Assert.Equal("mods", r.List.Kind);
        PackManagerRow Row(string n) => r.Packs.Single(p => p.Name == n);
        Assert.Equal((PackManager.StatusOk, PackManager.SourceRockstar), (Row("mpbeach").Status, Row("mpbeach").Source));
        Assert.Equal(PackManager.SourceRockstar, Row("patchday1ng").Source);
        Assert.Equal(PackManager.StatusDuplicate, Row("car").Status);
        Assert.Equal(PackManager.SourceLauncher, Row("car").Source);
        Assert.Equal(car.Id, Row("car").Owner!.ModId);
        Assert.Equal(PackManager.StatusMissing, Row("ghost").Status);
        Assert.Equal(PackManager.StatusNotRegistered, Row("orphan").Status);
        Assert.Contains(r.Problems, p => p.Fix == "duplicates");
        Assert.Contains(r.Problems, p => p.Fix == "missing:dlcpacks:/ghost/");
        Assert.Contains(r.Problems, p => p.Fix == "register:orphan");
    }

    [Fact]
    public void Switching_a_pack_off_and_on_is_journaled_and_undone_by_uninstalling_the_record()
    {
        using var s = new Sandbox();
        InstallPack(s, "car");
        Assert.Contains("dlcpacks:/car/", ListItems(s));

        PackManager.SetEnabled(s.Game, s.Paths, s.Library, "dlcpacks:/car/", false);
        Assert.DoesNotContain("dlcpacks:/car/", ListItems(s));
        Assert.Equal(PackManager.StatusDisabled, Report(s).Packs.Single(p => p.Name == "car").Status);

        PackManager.SetEnabled(s.Game, s.Paths, s.Library, "dlcpacks:/car/", true);
        Assert.Contains("dlcpacks:/car/", ListItems(s));
        PackManager.SetEnabled(s.Game, s.Paths, s.Library, "dlcpacks:/car/", false);

        var record = s.Library.All().Single(m => m.Name == PackManager.RecordName);
        Assert.Equal("tool", record.Source);
        Assert.Empty(new ModService(s.Paths, s.Library).Uninstall(s.Game, record));
        Assert.Contains("dlcpacks:/car/", ListItems(s));
        Assert.Single(ListItems(s), i => i == "dlcpacks:/car/");
    }

    [Fact]
    public void Switching_back_on_and_undoing_keep_the_load_order()
    {
        using var s = new Sandbox();
        InstallPack(s, "car");
        SetList(s, "dlcpacks:/a/", "dlcpacks:/car/", "dlcpacks:/b/");
        var order = new[] { "dlcpacks:/a/", "dlcpacks:/car/", "dlcpacks:/b/" };

        PackManager.SetEnabled(s.Game, s.Paths, s.Library, "dlcpacks:/car/", false);
        PackManager.SetEnabled(s.Game, s.Paths, s.Library, "dlcpacks:/car/", true);
        Assert.Equal(order, ListItems(s));

        PackManager.SetEnabled(s.Game, s.Paths, s.Library, "dlcpacks:/car/", false);
        var record = s.Library.All().Single(m => m.Name == PackManager.RecordName);
        Assert.Empty(new ModService(s.Paths, s.Library).Uninstall(s.Game, record));
        Assert.Equal(order, ListItems(s));
    }

    [Fact]
    public void Rockstars_own_packs_are_refused()
    {
        using var s = new Sandbox();
        FakeRockstarArchive(Path.Combine(s.GameRoot, @"update\x64\dlcpacks\mpbeach\dlc.rpf"));
        Assert.Throws<InvalidOperationException>(() => PackManager.SetEnabled(s.Game, s.Paths, s.Library, "dlcpacks:/mpbeach/", false));
        Assert.DoesNotContain(s.Library.All(), m => m.Name == PackManager.RecordName); // nothing was changed, so no record
    }

    [Fact]
    public void Duplicates_fix_keeps_the_first_line_where_it_was()
    {
        using var s = new Sandbox();
        InstallPack(s, "car");
        SetList(s, "dlcpacks:/a/", "dlcpacks:/car/", "dlcpacks:/b/", "dlcpacks:/car/", "dlcpacks:/a/");
        PackManager.Fix(s.Game, s.Paths, s.Library, "duplicates");
        Assert.Equal(new[] { "dlcpacks:/a/", "dlcpacks:/car/", "dlcpacks:/b/" }, ListItems(s));
        Assert.Throws<InvalidOperationException>(() => PackManager.Fix(s.Game, s.Paths, s.Library, "duplicates"));
    }

    [Fact]
    public void Keep_first_occurrences_ignores_commented_lines()
    {
        var xml = "<Paths>\n<Item>dlcpacks:/x/</Item>\n<!--<Item>dlcpacks:/x/</Item>-->\n<Item>dlcpacks:/x/</Item>\n</Paths>";
        var fixedXml = PackManager.KeepFirstOccurrences(xml);
        Assert.Equal("<Paths>\n<Item>dlcpacks:/x/</Item>\n<!--<Item>dlcpacks:/x/</Item>-->\n</Paths>", fixedXml);
    }

    [Fact]
    public void Orphan_pack_can_be_registered_and_missing_one_unlisted()
    {
        using var s = new Sandbox();
        Sandbox.MakeDlcRpf(Path.Combine(s.GameRoot, @"mods\update\x64\dlcpacks\orphan\dlc.rpf"), "orphan");
        SetList(s, "dlcpacks:/ghost/");
        PackManager.Fix(s.Game, s.Paths, s.Library, "register:orphan");
        PackManager.Fix(s.Game, s.Paths, s.Library, "missing:dlcpacks:/ghost/");
        Assert.Equal(new[] { "dlcpacks:/orphan/" }, ListItems(s));
        Assert.Throws<ArgumentException>(() => PackManager.Register(s.Game, s.Paths, s.Library, @"..\evil"));
    }

    [Fact]
    public void NaturalVision_list_dropping_packs_is_found_and_synced()
    {
        using var s = new Sandbox();
        InstallPack(s, "car");                          // registered in mods\update\update.rpf
        File.WriteAllText(Path.Combine(s.GameRoot, "onigiri.asi"), "nve");
        Directory.CreateDirectory(Path.GetDirectoryName(s.Game.OnigiriDlcList)!);
        File.WriteAllText(s.Game.OnigiriDlcList, Sandbox.OriginalDlcList); // NVE's own list, without "car"
        var before = File.ReadAllText(s.Game.OnigiriDlcList);

        var r = Report(s);
        Assert.Equal("onigiri", r.List.Kind);
        Assert.Equal(PackManager.StatusNotInActiveList, r.Packs.Single(p => p.Name == "car").Status);
        Assert.Contains(r.Problems, p => p.Fix == "onigiri-sync" && p.Level == "Block");

        PackManager.Fix(s.Game, s.Paths, s.Library, "onigiri-sync");
        Assert.Contains("dlcpacks:/car/", File.ReadAllText(s.Game.OnigiriDlcList));
        Assert.True(File.Exists(Path.Combine(s.GameRoot, @"onigiri\dlcpacks\car\dlc.rpf")));
        Assert.Equal(PackManager.StatusOk, Report(s).Packs.Single(p => p.Name == "car").Status);

        var record = s.Library.All().Single(m => m.Name == PackManager.RecordName);
        Assert.Empty(new ModService(s.Paths, s.Library).Uninstall(s.Game, record));
        Assert.Equal(before, File.ReadAllText(s.Game.OnigiriDlcList));
        Assert.False(File.Exists(Path.Combine(s.GameRoot, @"onigiri\dlcpacks\car\dlc.rpf")));
    }

    [Fact]
    public void Game_file_checker_tells_edited_original_stray_and_unreadable_apart()
    {
        using var s = new Sandbox();
        FakeRockstarArchive(Path.Combine(s.GameRoot, "x64a.rpf"));
        FakeRockstarArchive(Path.Combine(s.GameRoot, "x64b.rpf"), 0x4E45504F);       // OPEN: edited in place
        File.WriteAllText(Path.Combine(s.GameRoot, "x64c.rpf"), "not an archive");
        FakeRockstarArchive(Path.Combine(s.GameRoot, @"update\x64\dlcpacks\mpbeach\dlc.rpf"));
        Sandbox.MakeDlcRpf(Path.Combine(s.GameRoot, @"update\x64\dlcpacks\addedcar\dlc.rpf"), "addedcar"); // a mod's pack dropped in the game folder
        Sandbox.MakeUpdateRpf(Path.Combine(s.GameRoot, "update"), "update.rpf", Sandbox.OriginalDlcList);   // OPEN too: edited

        var r = GameFileChecker.Check(s.Game);
        string State(string rel) => r.Archives.Single(a => a.Path.Equals(rel, StringComparison.OrdinalIgnoreCase)).State;
        Assert.Equal("original", State("x64a.rpf"));
        Assert.Equal("edited", State("x64b.rpf"));
        Assert.Equal("unreadable", State("x64c.rpf"));
        Assert.Equal("original", State(@"update\x64\dlcpacks\mpbeach\dlc.rpf"));
        Assert.Equal("edited", State(@"update\update.rpf"));
        var stray = Assert.Single(r.StrayPacks);
        Assert.Equal("addedcar", stray.Name);
        Assert.DoesNotContain(r.Archives, a => a.Path.Contains("addedcar"));
        Assert.DoesNotContain(r.Archives, a => a.Path.StartsWith("mods", StringComparison.OrdinalIgnoreCase));

        Assert.True(GameFileChecker.MoveToMods(s.Game, s.Paths, s.Library, "x64b.rpf"));
        Assert.True(File.Exists(Path.Combine(s.GameRoot, @"mods\x64b.rpf")));
        Assert.False(GameFileChecker.MoveToMods(s.Game, s.Paths, s.Library, "x64b.rpf")); // already has a copy
        Assert.Single(s.Library.All(), m => m.Name == GameFileChecker.RecordName);
        Assert.Throws<InvalidOperationException>(() => GameFileChecker.MoveToMods(s.Game, s.Paths, s.Library, "x64a.rpf"));
        Assert.Throws<ArgumentException>(() => GameFileChecker.MoveToMods(s.Game, s.Paths, s.Library, @"..\x.rpf"));
    }

    [Fact]
    public void Verify_points_at_the_right_store()
    {
        var steam = new VModLauncher.Core.Game.GameInstall { Root = @"C:\g", Edition = VModLauncher.Core.Game.GameEdition.Enhanced, Store = "Steam" };
        Assert.Equal("steam://validate/3240220", GameFileChecker.Verify(steam).Url);
        var epic = new VModLauncher.Core.Game.GameInstall { Root = @"C:\g", Edition = VModLauncher.Core.Game.GameEdition.Legacy, Store = "Epic" };
        var (how, url) = GameFileChecker.Verify(epic);
        Assert.Null(url);
        Assert.Contains("Verify", how);
    }
}
