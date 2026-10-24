using System.Text;
using System.Xml.Linq;
using VModLauncher.Core.Game;
using VModLauncher.Core.Install;
using VModLauncher.Core.Library;
using VModLauncher.Core.Tools;
using Xunit;

namespace VModLauncher.Tests.Tools;

public class PackBuilderTests
{
    /// <summary>A small but well-formed RSC7 resource, like a real .yft/.ytd.</summary>
    static byte[] Resource(string body)
    {
        var packed = CodeWalker.GameFiles.ResourceBuilder.Compress(Encoding.UTF8.GetBytes(body));
        var data = new byte[16 + packed.Length];
        BitConverter.GetBytes(0x37435352u).CopyTo(data, 0);
        BitConverter.GetBytes(13u).CopyTo(data, 4);
        BitConverter.GetBytes(CodeWalker.GameFiles.RpfResourceFileEntry.GetFlagsFromSize(body.Length, 0)).CopyTo(data, 8);
        BitConverter.GetBytes(CodeWalker.GameFiles.RpfResourceFileEntry.GetFlagsFromSize(0, 13)).CopyTo(data, 12);
        packed.CopyTo(data, 16);
        return data;
    }

    static string VehiclesMeta(params (string Model, string Handling)[] cars) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<CVehicleModelInfo__InitDataList>\n  <residentTxd>vehshare</residentTxd>\n  <InitDatas>\n" +
        string.Concat(cars.Select(c =>
            $"    <Item>\n      <modelName>{c.Model}</modelName>\n      <txdName>{c.Model}</txdName>\n      <handlingId>{c.Handling}</handlingId>\n" +
            $"      <gameName>{c.Model.ToUpperInvariant()}</gameName>\n      <layout>LAYOUT_STANDARD</layout>\n      <type>VEHICLE_TYPE_CAR</type>\n    </Item>\n")) +
        "  </InitDatas>\n  <txdRelationships>\n" +
        string.Concat(cars.Select(c => $"    <Item><parent>vehicles_race_generic</parent><child>{c.Model}</child></Item>\n")) +
        "  </txdRelationships>\n</CVehicleModelInfo__InitDataList>\n";

    static string HandlingMeta(params string[] names) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<CHandlingDataMgr>\n  <HandlingData>\n" +
        string.Concat(names.Select(n => $"    <Item type=\"CHandlingData\">\n      <handlingName>{n}</handlingName>\n      <fMass value=\"1500.000000\" />\n    </Item>\n")) +
        "  </HandlingData>\n</CHandlingDataMgr>\n";

    static void WriteCar(Sandbox s, string folder, string model, string? handling = null, bool withMetas = true)
    {
        void Bin(string rel, string body)
        {
            var p = Path.Combine(s.ModRoot, rel.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, Resource(body));
        }
        Bin($"{folder}/{model}.yft", "model " + model);
        Bin($"{folder}/{model}_hi.yft", "hi model " + model);
        Bin($"{folder}/{model}.ytd", "textures " + model);
        if (!withMetas) return;
        var h = handling ?? model.ToUpperInvariant();
        s.ModFile($"{folder}/vehicles.meta", VehiclesMeta((model, h)));
        s.ModFile($"{folder}/handling.meta", HandlingMeta(h));
        s.ModFile($"{folder}/carvariations.meta", $"<?xml version=\"1.0\"?>\n<CVehicleModelInfoVariation>\n  <variationData>\n    <Item><modelName>{model}</modelName></Item>\n  </variationData>\n</CVehicleModelInfoVariation>\n");
        s.ModFile($"{folder}/carcols.meta", "<?xml version=\"1.0\"?>\n<CVehicleModelInfoVarGlobal>\n  <Kits />\n</CVehicleModelInfoVarGlobal>\n");
    }

    sealed class FakeIndex(params string[] vanilla) : IGameIndex
    {
        public IReadOnlyList<string> Find(string fileName) =>
            vanilla.Contains(fileName, StringComparer.OrdinalIgnoreCase) ? new[] { "x64e.rpf|levels/gta5/vehicles.rpf/" + fileName } : Array.Empty<string>();
        public byte[]? Read(string location) => null;
    }

    static string DlcListOf(Sandbox s) =>
        DlcList.Decode(new RpfService(s.Game).TryRead(s.Game.ModsUpdateRpf, DlcList.InnerPath)!, out _);

    [Fact]
    public void Loose_addon_car_is_packed_and_installs_like_any_pack()
    {
        using var s = new Sandbox();
        WriteCar(s, "VML Car", "vmlcar");
        s.ModFile("VML Car/readme.txt", "drive it");
        var p = new ModAnalyzer(s.Game).Analyze(s.ModRoot, "VML Car");

        var pack = Assert.Single(p.Ops, o => o.Kind == OpKind.DlcPack);
        Assert.Equal("vmlcar", pack.Target);
        Assert.StartsWith("Built from the mod's loose files", pack.Reason);
        Assert.Equal($"{AddonPackBuilder.BuildFolder}/vmlcar/dlc.rpf", pack.SourceRel);
        Assert.Equal(7, p.Ops.Count(o => o.Kind == OpKind.Skip && o.Reason == "Packed into vmlcar"));
        Assert.Equal("Add-on vehicle", p.Summary);
        Assert.DoesNotContain(p.Warnings, w => w.Level == WarningLevel.Block);
        Assert.Contains(VModLauncher.Core.Game.Prerequisites.ModsFolder, p.Requires);

        // what's inside the built pack
        var rpf = new RpfService(s.Game);
        var content = XDocument.Parse(Encoding.UTF8.GetString(rpf.TryRead(pack.Source, "content.xml")!));
        var files = content.Descendants("filename").Select(e => e.Value).ToList();
        Assert.Contains("dlc_vmlcar:/common/data/vehicles.meta", files);
        Assert.Contains("dlc_vmlcar:/common/data/handling.meta", files);
        Assert.Contains("dlc_vmlcar:/%PLATFORM%/vehicles.rpf", files);
        Assert.Equal(files.Count, content.Descendants("filesToEnable").Elements().Count());
        Assert.Equal("RPF_FILE", content.Descendants("Item").First(i => i.Element("filename")?.Value.EndsWith("vehicles.rpf") == true).Element("fileType")!.Value);
        var setup = XDocument.Parse(Encoding.UTF8.GetString(rpf.TryRead(pack.Source, "setup2.xml")!));
        Assert.Equal("dlc_vmlcar", setup.Root!.Element("deviceName")!.Value);
        Assert.Equal("vmlcar", setup.Root!.Element("nameHash")!.Value);
        Assert.Equal("VMLCAR_AUTOGEN", setup.Descendants("ContentChangeSets").Elements().Single().Value);
        Assert.Equal(VehiclesMeta(("vmlcar", "VMLCAR")), Encoding.UTF8.GetString(rpf.TryRead(pack.Source, "common/data/vehicles.meta")!));
        foreach (var model in new[] { "vmlcar.yft", "vmlcar_hi.yft", "vmlcar.ytd" })
        {
            var inside = rpf.TryRead(pack.Source, "x64/vehicles.rpf/" + model);
            Assert.NotNull(inside);
            Assert.Equal(RpfService.ContentHash(File.ReadAllBytes(Path.Combine(s.ModRoot, "VML Car", model))), RpfService.ContentHash(inside!));
        }

        // installs and uninstalls like any add-on pack
        var mod = new ModInstaller(s.Game, s.Paths, s.Library).Install(p, new InstalledMod { Name = "VML Car" });
        Assert.True(File.Exists(Path.Combine(s.GameRoot, @"mods\update\x64\dlcpacks\vmlcar\dlc.rpf")));
        Assert.Contains("<Item>dlcpacks:/vmlcar/</Item>", DlcListOf(s));
        Assert.Empty(new VModLauncher.Core.ModService(s.Paths, s.Library).Uninstall(s.Game, mod));
        Assert.Equal(Sandbox.OriginalDlcList, DlcListOf(s));
        Assert.False(Directory.Exists(Path.Combine(s.GameRoot, @"mods\update\x64\dlcpacks\vmlcar")));
    }

    [Fact]
    public void Data_and_stream_folders_count_as_one_car()
    {
        using var s = new Sandbox();
        WriteCar(s, "Car/stream", "vmlcar", withMetas: false);
        s.ModFile("Car/data/vehicles.meta", VehiclesMeta(("vmlcar", "VMLCAR")));
        s.ModFile("Car/data/handling.meta", HandlingMeta("VMLCAR"));
        var p = new ModAnalyzer(s.Game).Analyze(s.ModRoot, "Car");
        Assert.Single(p.Ops, o => o.Kind == OpKind.DlcPack);
        Assert.Equal(5, p.Ops.Count(o => o.Reason == "Packed into vmlcar"));
    }

    [Fact]
    public void Loose_addon_ped_gets_a_ped_pack()
    {
        using var s = new Sandbox();
        foreach (var ext in new[] { ".ydd", ".yft", ".ymt", ".ytd" })
        {
            var path = Path.Combine(s.ModRoot, "Ped", "vml_ped" + ext);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Resource("ped " + ext));
        }
        s.ModFile("Ped/peds.meta", "<?xml version=\"1.0\"?>\n<CPedModelInfo__InitDataList>\n  <InitDatas>\n    <Item>\n      <Name>vml_ped</Name>\n      <PropsName>null</PropsName>\n      <Pedtype>CIVMALE</Pedtype>\n    </Item>\n  </InitDatas>\n</CPedModelInfo__InitDataList>\n");
        var p = new ModAnalyzer(s.Game).Analyze(s.ModRoot, "Ped");

        var pack = Assert.Single(p.Ops, o => o.Kind == OpKind.DlcPack);
        Assert.Equal("vml_ped", pack.Target);
        Assert.Equal("Add-on ped", p.Summary);
        var rpf = new RpfService(s.Game);
        var content = Encoding.UTF8.GetString(rpf.TryRead(pack.Source, "content.xml")!);
        Assert.Contains("<fileType>PED_METADATA_FILE</fileType>", content);
        Assert.Contains("dlc_vml_ped:/%PLATFORM%/peds.rpf", content);
        Assert.NotNull(rpf.TryRead(pack.Source, "x64/peds.rpf/vml_ped.ydd"));
        Assert.NotNull(rpf.TryRead(pack.Source, "common/data/peds.meta"));
    }

    [Fact]
    public void Script_and_loose_car_in_one_mod_install_together()
    {
        using var s = new Sandbox();
        s.ModFile("Mod/Mod.asi");
        WriteCar(s, "Mod/Car", "vmlcar");
        var p = new ModAnalyzer(s.Game).Analyze(s.ModRoot, "Mod");
        Assert.Equal("Mod.asi", p.Ops.Single(o => o.SourceRel == "Mod/Mod.asi").Target);
        Assert.Single(p.Ops, o => o.Kind == OpKind.DlcPack && o.Target == "vmlcar");
        Assert.Equal(2, p.ActiveOps.Count());
    }

    [Fact]
    public void Cars_the_game_already_has_stay_replace_files()
    {
        // with a game scan: the index knows adder
        using var s = new Sandbox();
        WriteCar(s, "Adder", "adder");
        var p = new ModAnalyzer(s.Game, new FakeIndex("adder.yft", "adder_hi.yft", "adder.ytd")).Analyze(s.ModRoot, "Adder");
        Assert.DoesNotContain(p.Ops, o => o.Kind == OpKind.DlcPack);
        Assert.Equal(OpKind.RpfFile, p.Ops.Single(o => o.SourceRel == "Adder/adder.yft").Kind);

        // without one: the built-in list of the game's cars
        p = new ModAnalyzer(s.Game).Analyze(s.ModRoot, "Adder");
        Assert.DoesNotContain(p.Ops, o => o.Kind == OpKind.DlcPack);
    }

    [Fact]
    public void Full_copy_of_the_games_vehicles_meta_isnt_an_addon()
    {
        using var s = new Sandbox();
        WriteCar(s, "Tweaks", "vmlcar", withMetas: false);
        s.ModFile("Tweaks/vehicles.meta", VehiclesMeta(Enumerable.Range(1, 10).Select(i => ($"car{i}", $"CAR{i}")).Append(("vmlcar", "VMLCAR")).ToArray()));
        var p = new ModAnalyzer(s.Game).Analyze(s.ModRoot, "Tweaks");
        Assert.DoesNotContain(p.Ops, o => o.Kind == OpKind.DlcPack);
    }

    [Fact]
    public void Mistakes_in_the_metas_become_warnings()
    {
        using var s = new Sandbox();
        WriteCar(s, "Car", "vmlcar", withMetas: false);
        File.WriteAllBytes(Path.Combine(s.ModRoot, "Car", "leftover.ytd"), Resource("unused"));
        s.ModFile("Car/vehicles.meta", VehiclesMeta(("vmlcar", "VMLCAR_X"), ("vmlcar2", "VMLCAR2")));
        s.ModFile("Car/handling.meta", HandlingMeta("VMLCAR2"));
        var p = new ModAnalyzer(s.Game).Analyze(s.ModRoot, "Car");
        Assert.Single(p.Ops, o => o.Kind == OpKind.DlcPack);
        Assert.Contains(p.Warnings, w => w.Message.Contains("\"vmlcar2\"") && w.Message.Contains("vmlcar2.yft"));
        Assert.Contains(p.Warnings, w => w.Message.Contains("VMLCAR_X"));
        Assert.Contains(p.Warnings, w => w.Message.Contains("leftover.ytd"));
    }

    [Fact]
    public void Analysing_again_doesnt_pick_up_the_built_pack()
    {
        using var s = new Sandbox();
        WriteCar(s, "Car", "vmlcar");
        var first = new ModAnalyzer(s.Game).Analyze(s.ModRoot, "Car");
        var again = new ModAnalyzer(s.Game).Analyze(s.ModRoot, "Car");
        Assert.Single(again.Ops, o => o.Kind == OpKind.DlcPack);
        Assert.Equal(first.Ops.Count, again.Ops.Count);
        Assert.Equal(first.Ops.Single(o => o.Kind == OpKind.DlcPack).SourceRel, again.Ops.Single(o => o.Kind == OpKind.DlcPack).SourceRel);
    }

    [Fact]
    public void Pack_name_doesnt_clash_with_one_already_in_the_game()
    {
        using var s = new Sandbox();
        Directory.CreateDirectory(Path.Combine(s.GameRoot, @"mods\update\x64\dlcpacks\vmlcar"));
        WriteCar(s, "Car", "vmlcar");
        var p = new ModAnalyzer(s.Game).Analyze(s.ModRoot, "Car");
        Assert.Equal("vmlcar_2", p.Ops.Single(o => o.Kind == OpKind.DlcPack).Target);

        var taken = new HashSet<string> { "averyveryverylongcar", "averyveryverylongc_2" };
        var set = new AddonSet();
        set.Models.Add("AVeryVeryVeryLongCarName!");
        Assert.Equal("averyveryverylongc_3", AddonPackBuilder.PackName(set, taken));
    }

    [Fact]
    public void Same_model_in_another_installed_pack_is_flagged()
    {
        using var s = new Sandbox();
        WriteCar(s, "Car", "vmlcar");
        var first = new ModAnalyzer(s.Game).Analyze(s.ModRoot, "Car");
        new ModInstaller(s.Game, s.Paths, s.Library).Install(first, new InstalledMod { Name = "first" });

        var again = new ModAnalyzer(s.Game).Analyze(s.ModRoot, "Car");
        Assert.Equal("vmlcar_2", again.Ops.Single(o => o.Kind == OpKind.DlcPack).Target);
        Assert.Contains(again.Warnings, w => w.Message.Contains("already in the add-on pack \"vmlcar\""));
    }
}
