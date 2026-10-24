using System.IO.Compression;
using System.Text;
using VModLauncher.Core;
using VModLauncher.Core.Game;
using VModLauncher.Core.Install;
using VModLauncher.Core.Install.Oiv;
using VModLauncher.Core.Library;
using Xunit;

namespace VModLauncher.Tests;

/// <summary>One test per bug found by the independent review (numbers match the review).</summary>
public class ReviewRegressionTests
{
    static InstalledMod Install(Sandbox s, string name)
    {
        var plan = new ModAnalyzer(s.Game).Analyze(s.ModRoot, name);
        return new ModInstaller(s.Game, s.Paths, s.Library).Install(plan, new InstalledMod { Name = name });
    }

    static void FreshMod(Sandbox s)
    {
        if (Directory.Exists(s.ModRoot)) Directory.Delete(s.ModRoot, true);
        Directory.CreateDirectory(s.ModRoot);
    }

    static string DlcListOf(Sandbox s) =>
        DlcList.Decode(new RpfService(s.Game).TryRead(s.Game.ModsUpdateRpf, DlcList.InnerPath)!, out _);

    [Fact] // 1
    public void Shared_identical_library_survives_uninstalling_one_of_its_mods()
    {
        using var s = new Sandbox();
        var shared = Path.Combine(s.GameRoot, @"scripts\NativeUI.ini");
        s.ModFile("A/scripts/NativeUI.ini", "same bytes");
        s.ModFile("A/scripts/A.ini", "a");
        var a = Install(s, "A");
        FreshMod(s);
        s.ModFile("B/scripts/NativeUI.ini", "same bytes");
        var b = Install(s, "B");

        var service = new ModService(s.Paths, s.Library);
        Assert.Empty(service.Uninstall(s.Game, s.Library.Find(a.Id)!));
        Assert.True(File.Exists(shared), "B still needs the shared file");
        Assert.Empty(service.Uninstall(s.Game, s.Library.Find(b.Id)!));
        Assert.False(File.Exists(shared));
    }

    [Fact] // 2
    public void Locked_file_keeps_the_record_and_backup_for_a_retry()
    {
        using var s = new Sandbox();
        var target = Path.Combine(s.GameRoot, "Locked.asi");
        File.WriteAllText(target, "original");
        s.ModFile("M/Locked.asi", "modded");
        var mod = Install(s, "M");
        var service = new ModService(s.Paths, s.Library);

        List<string> problems;
        using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None)) // something holds the file
            problems = service.Uninstall(s.Game, s.Library.Find(mod.Id)!);
        Assert.NotEmpty(problems);
        var kept = s.Library.Find(mod.Id);
        Assert.NotNull(kept);
        Assert.True(kept!.NeedsCleanup);
        Assert.Single(kept.Entries);

        Assert.Empty(service.Uninstall(s.Game, kept)); // retry once the file is free
        Assert.Equal("original", File.ReadAllText(target));
        Assert.Null(s.Library.Find(mod.Id));
    }

    [Fact] // 3
    public void Nothing_installs_or_uninstalls_while_mods_are_off()
    {
        using var s = new Sandbox();
        File.WriteAllText(Path.Combine(s.GameRoot, "dinput8.dll"), "loader");
        s.ModFile("M/M.asi");
        var mod = Install(s, "M");
        GameLauncher.SetModsEnabled(s.Game, false);
        Assert.False(GameLauncher.AreModsEnabled(s.Game));
        Assert.Throws<InstallBlockedException>(() => new ModService(s.Paths, s.Library).Uninstall(s.Game, s.Library.Find(mod.Id)!));
        Assert.Throws<InstallBlockedException>(() => Install(s, "M2"));
        GameLauncher.SetModsEnabled(s.Game, true);
        Assert.True(GameLauncher.AreModsEnabled(s.Game));
        Assert.True(Directory.Exists(s.Game.ModsDir));
    }

    [Fact] // 4
    public void Failed_re_enable_leaves_the_mod_disabled_not_deleted()
    {
        using var s = new Sandbox();
        s.ModFile("M/M.asi", "m");
        s.ModFile("M/M2.asi", "m2");
        var zip = Path.Combine(s.Root, "m.zip");
        ZipFile.CreateFromDirectory(s.ModRoot, zip);
        var service = new ModService(s.Paths, s.Library);
        var mod = service.Install(s.Game, service.Prepare(s.Game, zip, "m"), new InstalledMod { Name = "m" });
        service.Disable(s.Game, mod);

        var blocker = Path.Combine(s.GameRoot, "M2.asi");
        File.WriteAllText(blocker, "player's own");
        using (new FileStream(blocker, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.ThrowsAny<Exception>(() => service.Enable(s.Game, s.Library.Find(mod.Id)!));

        var after = s.Library.Find(mod.Id);
        Assert.NotNull(after);
        Assert.False(after!.Enabled);
        Assert.False(File.Exists(Path.Combine(s.GameRoot, "M.asi")));
    }

    [Fact] // 5
    public void Re_enable_puts_hand_placed_files_back_where_the_player_put_them()
    {
        using var s = new Sandbox();
        Sandbox.MakeDlcRpf(Path.Combine(s.ModRoot, "P", "pk", "dlc.rpf"), "pk");
        s.ModFile("P/Sounds/x.bnk", "sound");
        var zip = Path.Combine(s.Root, "p.zip");
        ZipFile.CreateFromDirectory(s.ModRoot, zip);
        var service = new ModService(s.Paths, s.Library);
        var plan = service.Prepare(s.Game, zip, "p");
        plan.SetManualTarget(plan.Ops.FindIndex(o => o.SourceRel.EndsWith("x.bnk")), @"scripts\Sounds", false);
        var mod = service.Install(s.Game, plan, new InstalledMod { Name = "p" });
        Assert.True(File.Exists(Path.Combine(s.GameRoot, @"scripts\Sounds\x.bnk")));

        service.Disable(s.Game, mod);
        Assert.False(File.Exists(Path.Combine(s.GameRoot, @"scripts\Sounds\x.bnk")));
        service.Enable(s.Game, s.Library.Find(mod.Id)!);
        Assert.True(File.Exists(Path.Combine(s.GameRoot, @"scripts\Sounds\x.bnk")));
    }

    [Fact] // found in the end-to-end run: a dropped folder couldn't be turned back on
    public void Dropped_folder_can_be_turned_off_and_on()
    {
        using var s = new Sandbox();
        var folder = Path.Combine(s.Root, "Fake Mod v1.2");
        Directory.CreateDirectory(Path.Combine(folder, "scripts"));
        File.WriteAllText(Path.Combine(folder, "Fake.asi"), "asi");
        File.WriteAllText(Path.Combine(folder, "scripts", "Fake.ini"), "ini");
        var service = new ModService(s.Paths, s.Library);
        var mod = service.Install(s.Game, service.Prepare(s.Game, folder, "Fake Mod v1.2"), new InstalledMod { Name = "Fake Mod v1.2" });
        Assert.NotNull(mod.ArchivePath);
        Assert.True(File.Exists(mod.ArchivePath));

        Assert.Empty(service.Disable(s.Game, mod));
        Assert.False(File.Exists(Path.Combine(s.GameRoot, "Fake.asi")));
        service.Enable(s.Game, s.Library.Find(mod.Id)!);
        Assert.True(File.Exists(Path.Combine(s.GameRoot, "Fake.asi")));
        Assert.True(File.Exists(Path.Combine(s.GameRoot, @"scripts\Fake.ini")));
    }

    [Fact] // 6
    public void Pack_registered_by_two_mods_stays_until_the_last_one_goes()
    {
        using var s = new Sandbox();
        Sandbox.MakeDlcRpf(Path.Combine(s.ModRoot, "Car", "dlc.rpf"), "car");
        var a = Install(s, "A");
        FreshMod(s);
        Sandbox.MakeDlcRpf(Path.Combine(s.ModRoot, "Car", "dlc.rpf"), "car");
        var b = Install(s, "B");
        var service = new ModService(s.Paths, s.Library);

        service.Uninstall(s.Game, s.Library.Find(a.Id)!);
        Assert.Contains("dlcpacks:/car/", DlcListOf(s));
        service.Uninstall(s.Game, s.Library.Find(b.Id)!);
        Assert.DoesNotContain("dlcpacks:/car/", DlcListOf(s));
        Assert.Equal(Sandbox.OriginalDlcList, DlcListOf(s));
    }

    [Fact] // 7
    public void Oiv_dlclist_entry_is_removed_even_after_other_packs_were_added()
    {
        using var s = new Sandbox();
        ModInstaller.OivRunner ??= (journal, path, log) =>
        {
            using var pkg = OivPackage.Open(path);
            new OivInstaller(journal).Install(pkg, log);
        };
        var oivDir = Path.Combine(s.Root, "oivsrc");
        Directory.CreateDirectory(Path.Combine(oivDir, "content"));
        File.WriteAllText(Path.Combine(oivDir, "assembly.xml"), """
            <?xml version="1.0" encoding="UTF-8"?>
            <package version="2.0" id="{00000000-0000-0000-0000-000000000001}" target="Five">
              <metadata><name>Test Car</name><version><major>1</major><minor>0</minor></version><author><displayName>t</displayName></author><description>t</description></metadata>
              <content>
                <archive path="update\update.rpf" createIfNotExist="False" type="RPF7">
                  <xml path="common\data\dlclist.xml">
                    <add xpath="/SMandatoryPacksData/Paths" append="Last"><Item>dlcpacks:/oivcar/</Item></add>
                  </xml>
                </archive>
              </content>
            </package>
            """);
        var oivFile = Path.Combine(s.ModRoot, "Test Car.oiv");
        ZipFile.CreateFromDirectory(oivDir, oivFile);
        var oivMod = Install(s, "oiv");
        Assert.Contains("dlcpacks:/oivcar/", DlcListOf(s));

        FreshMod(s);
        Sandbox.MakeDlcRpf(Path.Combine(s.ModRoot, "Other", "dlc.rpf"), "other");
        var other = Install(s, "other");

        var service = new ModService(s.Paths, s.Library);
        Assert.Empty(service.Uninstall(s.Game, s.Library.Find(oivMod.Id)!));
        var list = DlcListOf(s);
        Assert.DoesNotContain("dlcpacks:/oivcar/", list);
        Assert.Contains("dlcpacks:/other/", list);
    }

    [Fact] // 9
    public void Generic_wrapper_folders_and_asi_folders_dont_dump_game_files_in_the_root()
    {
        using var s = new Sandbox();
        s.ModFile("Main Files/Replace/adder.yft");
        s.ModFile("Installation/handling.meta", "<CHandlingDataMgr/>");
        s.ModFile("Car/adder.ytd");
        s.ModFile("Car/x.asi");
        var p = new ModAnalyzer(s.Game).Analyze(s.ModRoot, "t");
        foreach (var rel in new[] { "Main Files/Replace/adder.yft", "Installation/handling.meta", "Car/adder.ytd" })
        {
            var op = p.Ops.Single(o => o.SourceRel == rel);
            Assert.False(op.Kind == OpKind.CopyFile, $"{rel} was copied to {op.Target}");
        }
        Assert.Equal("x.asi", p.Ops.Single(o => o.SourceRel == "Car/x.asi").Target);
    }

    [Fact] // 10
    public void Clash_inside_one_option_becomes_its_own_choice()
    {
        using var s = new Sandbox(GameEdition.Enhanced);
        s.ModFile("Mod/Legacy/x.asi");
        s.ModFile("Mod/Enhanced/Version A/x.asi");
        s.ModFile("Mod/Enhanced/Version B/x.asi");
        var p = new ModAnalyzer(s.Game).Analyze(s.ModRoot, "t");
        Assert.Single(p.ActiveOps, o => o.Target == "x.asi");
        Assert.Equal(2, p.Variants.Count);
    }

    [Fact] // 15, 16
    public void Oiv_names_and_multi_package_downloads()
    {
        using var s = new Sandbox();
        s.ModFile("Remove Wanted Stars.oiv");
        var p = new ModAnalyzer(s.Game).Analyze(s.ModRoot, "t");
        Assert.Single(p.ActiveOps);

        FreshMod(s);
        s.ModFile("Pack/Base.oiv");
        s.ModFile("Pack/Extra Cars.oiv");
        s.ModFile("Pack/Uninstall.oiv");
        p = new ModAnalyzer(s.Game).Analyze(s.ModRoot, "t");
        Assert.Equal(2, p.ActiveOps.Count());
        Assert.Empty(p.Variants);

        FreshMod(s);
        s.ModFile("Pack/Lite.oiv");
        s.ModFile("Pack/Full.oiv");
        p = new ModAnalyzer(s.Game).Analyze(s.ModRoot, "t");
        Assert.Single(p.ActiveOps);
        Assert.Single(p.Variants);
    }

    [Fact] // 20
    public void Commented_out_pack_isnt_counted_as_registered()
    {
        var xml = "<SMandatoryPacksData><Paths>\n<!--<Item>dlcpacks:/car/</Item>-->\n</Paths></SMandatoryPacksData>";
        Assert.False(DlcList.Contains(xml, "dlcpacks:/car/"));
        Assert.Contains("<Item>dlcpacks:/car/</Item>\n</Paths>", DlcList.Add(xml, "dlcpacks:/car/"));
        Assert.Empty(DlcList.Items(xml));
    }

    [Fact] // 19
    public void Save_you_played_on_is_kept_and_its_original_is_kept_too()
    {
        using var s = new Sandbox();
        var file = Path.Combine(s.GameRoot, "settings.ini");
        File.WriteAllText(file, "player original");
        s.ModFile("M/M.asi");
        s.ModFile("M/settings.ini", "mod default");
        var mod = Install(s, "M");
        File.WriteAllText(file, "edited after install");

        var service = new ModService(s.Paths, s.Library);
        Assert.Empty(service.Uninstall(s.Game, s.Library.Find(mod.Id)!));
        Assert.Equal("edited after install", File.ReadAllText(file)); // left as the player has it
        var note = Assert.Single(service.LastNotes);
        var keptPath = note[(note.IndexOf("Kept a copy at ", StringComparison.Ordinal) + 15)..];
        Assert.Equal("player original", File.ReadAllText(keptPath));
    }
}
