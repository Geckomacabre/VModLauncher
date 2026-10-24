using VModLauncher.Core.Game;
using VModLauncher.Core.Install;
using Xunit;

namespace VModLauncher.Tests;

public class AnalyzerTests
{
    static InstallPlan Analyze(Sandbox s, GameEdition edition = GameEdition.Legacy) =>
        new ModAnalyzer(s.Game).Analyze(s.ModRoot, "test");

    static InstallOp Op(InstallPlan p, string sourceRel) =>
        p.Ops.Single(o => o.SourceRel.Equals(sourceRel, StringComparison.OrdinalIgnoreCase));

    static string RealDll(string rel)
    {
        var p = Path.Combine(Sandbox.RealLegacy, rel);
        return File.Exists(p) ? p : "";
    }

    [Fact]
    public void Addon_pack_is_registered_under_its_setup2_name()
    {
        using var s = new Sandbox();
        Sandbox.MakeDlcRpf(Path.Combine(s.ModRoot, "Lambo Aventador [Add-On]", "dlc.rpf"), "lamboav");
        s.ModFile("Lambo Aventador [Add-On]/readme.txt");
        s.ModFile("screenshot.jpg");
        var p = Analyze(s);
        var op = Op(p, "Lambo Aventador [Add-On]/dlc.rpf");
        Assert.Equal(OpKind.DlcPack, op.Kind);
        Assert.Equal("lamboav", op.Target);
        Assert.Equal(OpKind.Skip, Op(p, "screenshot.jpg").Kind);
        Assert.Contains(VModLauncher.Core.Game.Prerequisites.ModsFolder, p.Requires);
        Assert.Equal("Add-on DLC pack", p.Summary);
    }

    [Fact]
    public void Archive_that_mirrors_the_game_folder_maps_one_to_one()
    {
        using var s = new Sandbox();
        s.ModFile("MyMod v2/Grand Theft Auto V/scripts/MyMod.ini");
        s.ModFile("MyMod v2/Grand Theft Auto V/scripts/MyMod/data.json");
        s.ModFile("MyMod v2/Grand Theft Auto V/MyMod.asi");
        s.ModFile("MyMod v2/readme.txt");
        var p = Analyze(s);
        Assert.Equal(@"scripts\MyMod.ini", Op(p, "MyMod v2/Grand Theft Auto V/scripts/MyMod.ini").Target);
        Assert.Equal(@"scripts\MyMod\data.json", Op(p, "MyMod v2/Grand Theft Auto V/scripts/MyMod/data.json").Target);
        Assert.Equal("MyMod.asi", Op(p, "MyMod v2/Grand Theft Auto V/MyMod.asi").Target);
        Assert.Equal(OpKind.Skip, Op(p, "MyMod v2/readme.txt").Kind);
    }

    [Fact]
    public void Asi_with_config_goes_to_root_and_screenshots_stay_out()
    {
        using var s = new Sandbox();
        s.ModFile("Trainer/Trainer.asi");
        s.ModFile("Trainer/Trainer.ini");
        s.ModFile("Trainer/screens/1.png");
        s.ModFile("Trainer/screens/2.png");
        var p = Analyze(s);
        Assert.Equal("Trainer.asi", Op(p, "Trainer/Trainer.asi").Target);
        Assert.Equal("Trainer.ini", Op(p, "Trainer/Trainer.ini").Target);
        Assert.Equal(OpKind.Skip, Op(p, "Trainer/screens/1.png").Kind);
        Assert.Contains(VModLauncher.Core.Game.Prerequisites.ScriptHookV, p.Requires);
    }

    [Fact]
    public void Shvdn_script_found_by_its_references_takes_its_config_along()
    {
        var lemon = RealDll(@"scripts\LemonUI.SHVDN3.dll");
        if (lemon.Length == 0) return; // needs the real Legacy game for a real SHVDN assembly
        using var s = new Sandbox();
        s.ModCopy("Cool Script 1.0/CoolScript.dll", lemon);
        s.ModFile("Cool Script 1.0/CoolScript.ini");
        s.ModFile("Cool Script 1.0/CoolScript/sounds.xml", "<Sounds/>");
        var p = Analyze(s);
        Assert.Equal(@"scripts\CoolScript.dll", Op(p, "Cool Script 1.0/CoolScript.dll").Target);
        Assert.Equal(@"scripts\CoolScript.ini", Op(p, "Cool Script 1.0/CoolScript.ini").Target);
        Assert.Equal(@"scripts\CoolScript\sounds.xml", Op(p, "Cool Script 1.0/CoolScript/sounds.xml").Target);
        Assert.Contains(VModLauncher.Core.Game.Prerequisites.Shvdn, p.Requires);
    }

    [Fact]
    public void Rph_plugin_found_by_its_plugin_attribute()
    {
        var lspdfr = RealDll(@"plugins\LSPD First Response.dll");
        if (lspdfr.Length == 0) return;
        Assert.Equal(DllKind.RphPlugin, PeInspector.Inspect(lspdfr));
        Assert.Equal(DllKind.Native, PeInspector.Inspect(RealDll("ScriptHookV.dll")));
        Assert.Equal(DllKind.Shvdn, PeInspector.Inspect(RealDll(@"scripts\LemonUI.SHVDN3.dll")));
    }

    [Fact]
    public void Edition_folders_become_a_choice_that_follows_the_game()
    {
        using var s = new Sandbox(GameEdition.Enhanced);
        s.ModFile("Mod/Legacy/Mod.asi");
        s.ModFile("Mod/Enhanced/Mod.asi");
        var p = Analyze(s);
        var g = Assert.Single(p.Variants);
        Assert.Equal("Enhanced", g.Selected);
        var active = p.ActiveOps.ToList();
        Assert.Single(active);
        Assert.Equal("Mod/Enhanced/Mod.asi", active[0].SourceRel);
    }

    [Fact]
    public void Folders_that_write_the_same_files_become_a_choice()
    {
        using var s = new Sandbox();
        s.ModFile("Pack/Common/scripts/shared.dll.config");
        s.ModFile("Pack/Options/Blue Lights/ELS/pack_default/car.xml", "<vcfroot/>");
        s.ModFile("Pack/Options/Red Lights/ELS/pack_default/car.xml", "<vcfroot/>");
        var p = Analyze(s);
        var g = Assert.Single(p.Variants);
        Assert.Equal(2, g.Options.Count);
        Assert.Single(p.ActiveOps, o => o.Target == @"ELS\pack_default\car.xml");
        p.Select(g.Id, "Red Lights");
        Assert.Equal("Pack/Options/Red Lights/ELS/pack_default/car.xml", p.ActiveOps.Single(o => o.Target == @"ELS\pack_default\car.xml").SourceRel);
    }

    [Fact]
    public void Els_and_menyoo_xml_are_recognised_by_root_element()
    {
        using var s = new Sandbox();
        s.ModFile("Police Car/ELS/police3.xml", "<?xml version=\"1.0\"?><vcfroot Description=\"x\"/>");
        s.ModFile("Maps/Prison.xml", "<SpoonerPlacements><Placement/></SpoonerPlacements>");
        s.ModFile("dlclist.xml", "<SMandatoryPacksData><Paths/></SMandatoryPacksData>");
        var p = Analyze(s);
        // ELS only reads ELS\pack_default\, even when the archive's ELS folder skips that level
        Assert.Equal(@"ELS\pack_default\police3.xml", Op(p, "Police Car/ELS/police3.xml").Target);
        Assert.Equal(@"menyooStuff\Spooner\Prison.xml", Op(p, "Maps/Prison.xml").Target);
        Assert.Equal(OpKind.Skip, Op(p, "dlclist.xml").Kind);
    }

    [Fact]
    public void Loose_els_xml_goes_to_pack_default()
    {
        using var s = new Sandbox();
        s.ModFile("police3.xml", "<vcfroot/>");
        Sandbox.MakeDlcRpf(Path.Combine(s.ModRoot, "police3", "dlc.rpf"), "police3");
        var p = Analyze(s);
        Assert.Equal(@"ELS\pack_default\police3.xml", Op(p, "police3.xml").Target);
        Assert.Equal("Add-on vehicle (ELS)", p.Summary);
    }

    [Fact]
    public void Folders_named_like_archives_give_the_path_inside_the_game()
    {
        using var s = new Sandbox();
        s.ModFile("Replace/x64e.rpf/levels/gta5/vehicles.rpf/adder.yft");
        s.ModFile("Replace/mods/update/update.rpf/common/data/handling.meta", "<CHandlingDataMgr/>");
        var p = Analyze(s);
        Assert.Equal("x64e.rpf|levels/gta5/vehicles.rpf/adder.yft", Op(p, "Replace/x64e.rpf/levels/gta5/vehicles.rpf/adder.yft").Target);
        Assert.Equal(@"update\update.rpf|common/data/handling.meta", Op(p, "Replace/mods/update/update.rpf/common/data/handling.meta").Target);
        Assert.Equal("Replace mod", p.Summary);
    }

    [Fact]
    public void Shipped_args_txt_is_merged_not_skipped()
    {
        using var s = new Sandbox();
        s.ModFile("bin/ScriptHookV.dll");
        s.ModFile("bin/dinput8.dll");
        s.ModFile("bin/args.txt", "-nobattleye");
        var p = Analyze(s);
        Assert.Equal("args.txt", Op(p, "bin/args.txt").Target);
        Assert.Equal("Script Hook V", p.Summary);
    }

    [Fact]
    public void FiveM_resources_are_refused_with_a_reason()
    {
        using var s = new Sandbox();
        s.ModFile("res/fxmanifest.lua");
        s.ModFile("res/stream/thing.ydr");
        var p = Analyze(s);
        Assert.Contains(p.Warnings, w => w.Level == WarningLevel.Block && w.Message.Contains("FiveM"));
        Assert.Empty(p.ActiveOps);
    }

    [Fact]
    public void Optional_folder_is_off_by_default()
    {
        using var s = new Sandbox();
        s.ModFile("Mod/Mod.asi");
        s.ModFile("Mod/Optional/No Blur/Mod.ini");
        var p = Analyze(s);
        var opt = Op(p, "Mod/Optional/No Blur/Mod.ini");
        Assert.False(opt.Enabled);
        Assert.True(Op(p, "Mod/Mod.asi").Enabled);
    }

    [Fact]
    public void Bundled_second_asi_loader_is_left_out_when_script_hook_v_is_there()
    {
        using var s = new Sandbox();
        File.WriteAllText(Path.Combine(s.GameRoot, "dinput8.dll"), "shv loader");
        s.ModFile("OpenRPF/OpenRPF.asi");
        s.ModFile("OpenRPF/dsound.dll");
        var p = Analyze(s);
        Assert.True(Op(p, "OpenRPF/OpenRPF.asi").Enabled);
        Assert.False(Op(p, "OpenRPF/dsound.dll").Enabled);
        Assert.DoesNotContain(VModLauncher.Core.Game.Prerequisites.ModsFolder, p.Requires);
    }

    [Fact]
    public void Player_can_place_unrecognised_files_by_hand()
    {
        using var s = new Sandbox();
        Sandbox.MakeDlcRpf(Path.Combine(s.ModRoot, "Pack", "dlc", "dlc.rpf"), "pack"); // no main file the sounds could follow
        s.ModFile("Pack/Sounds/a.bnk");
        s.ModFile("Pack/Sounds/sub/b.bnk");
        var p = Analyze(s);
        var i = p.Ops.FindIndex(o => o.SourceRel == "Pack/Sounds/a.bnk");
        Assert.Equal(OpKind.Skip, p.Ops[i].Kind);

        p.SetManualTarget(i, @"scripts\MySounds", wholeFolder: true);
        Assert.Equal(@"scripts\MySounds\a.bnk", Op(p, "Pack/Sounds/a.bnk").Target);
        Assert.Equal(@"scripts\MySounds\sub\b.bnk", Op(p, "Pack/Sounds/sub/b.bnk").Target);
        Assert.All(p.Ops.Where(o => o.SourceRel.StartsWith("Pack/Sounds")), o => Assert.True(o.Enabled && o.Kind == OpKind.CopyFile));

        Assert.DoesNotContain(p.Warnings, w => w.Level == WarningLevel.Block);
        Assert.Throws<ArgumentException>(() => p.SetManualTarget(i, @"..\..\Windows", false));
        Assert.Throws<ArgumentException>(() => p.SetManualTarget(i, @"C:\Windows", false));
    }

    [Fact]
    public void Oiv_wins_and_uninstallers_are_ignored()
    {
        using var s = new Sandbox();
        s.ModFile("Pack/install.oiv");
        s.ModFile("Pack/uninstall.oiv");
        s.ModFile("Pack/readme.txt");
        var p = Analyze(s);
        Assert.Equal(OpKind.Oiv, Op(p, "Pack/install.oiv").Kind);
        Assert.Equal(OpKind.Skip, Op(p, "Pack/uninstall.oiv").Kind);
        Assert.Single(p.ActiveOps);
    }
}
