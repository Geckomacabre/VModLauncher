using System.Text;
using VModLauncher.Core;
using VModLauncher.Core.Install;
using VModLauncher.Core.Library;
using Xunit;

namespace VModLauncher.Tests;

public class InstallTests
{
    static string DlcListOf(Sandbox s)
    {
        var bytes = new RpfService(s.Game).TryRead(s.Game.ModsUpdateRpf, DlcList.InnerPath)!;
        return DlcList.Decode(bytes, out _);
    }

    static InstalledMod Install(Sandbox s, string name = "test")
    {
        var service = new ModService(s.Paths, s.Library);
        var plan = new ModAnalyzer(s.Game).Analyze(s.ModRoot, name);
        Assert.DoesNotContain(plan.Warnings, w => w.Level == WarningLevel.Block);
        return new ModInstaller(s.Game, s.Paths, s.Library).Install(plan, new InstalledMod { Name = name });
    }

    [Fact]
    public void Addon_pack_install_and_uninstall_leave_the_game_as_it_was()
    {
        using var s = new Sandbox();
        Sandbox.MakeDlcRpf(Path.Combine(s.ModRoot, "Car", "dlc.rpf"), "mycar");
        s.ModFile("Car/Car.asi");
        File.WriteAllText(Path.Combine(s.GameRoot, "args.txt"), "-windowed");
        s.ModFile("Car/args.txt", "-nobattleye -windowed");
        var rpfBefore = File.ReadAllBytes(s.Game.ModsUpdateRpf);

        var mod = Install(s);

        Assert.True(File.Exists(Path.Combine(s.GameRoot, @"mods\update\x64\dlcpacks\mycar\dlc.rpf")));
        Assert.True(File.Exists(Path.Combine(s.GameRoot, "Car.asi")));
        Assert.Contains("<Item>dlcpacks:/mycar/</Item>", DlcListOf(s));
        var args = File.ReadAllText(Path.Combine(s.GameRoot, "args.txt"));
        Assert.Contains("-nobattleye", args);
        Assert.Single(args.Split((char[])['\r', '\n', ' '], StringSplitOptions.RemoveEmptyEntries), a => a == "-windowed");

        var problems = new ModService(s.Paths, s.Library).Uninstall(s.Game, mod);

        Assert.Empty(problems);
        Assert.Equal(Sandbox.OriginalDlcList, DlcListOf(s));
        Assert.False(File.Exists(Path.Combine(s.GameRoot, "Car.asi")));
        Assert.False(Directory.Exists(Path.Combine(s.GameRoot, @"mods\update\x64\dlcpacks\mycar")));
        Assert.Equal("-windowed", File.ReadAllText(Path.Combine(s.GameRoot, "args.txt")));
        Assert.Empty(s.Library.Mods);
    }

    [Fact]
    public void First_pack_copies_update_rpf_into_mods_and_onigiri_wins_when_present()
    {
        using var s = new Sandbox(withModsRpf: false);
        Sandbox.MakeUpdateRpf(Path.Combine(s.GameRoot, "update"), "update.rpf", Sandbox.OriginalDlcList);
        Sandbox.MakeDlcRpf(Path.Combine(s.ModRoot, "Pack", "dlc.rpf"), "packa");
        Install(s, "a");
        Assert.True(File.Exists(s.Game.ModsUpdateRpf));
        Assert.Contains("dlcpacks:/packa/", DlcListOf(s));
        // the game's own update.rpf is untouched
        var original = new RpfService(s.Game).TryRead(Path.Combine(s.GameRoot, @"update\update.rpf"), DlcList.InnerPath)!;
        Assert.Equal(Sandbox.OriginalDlcList, DlcList.Decode(original, out _));

        // NaturalVision's onigiri list takes over
        File.WriteAllText(Path.Combine(s.GameRoot, "onigiri.asi"), "x");
        Directory.CreateDirectory(Path.Combine(s.GameRoot, @"onigiri\common\data"));
        File.WriteAllText(s.Game.OnigiriDlcList, Sandbox.OriginalDlcList);
        Directory.Delete(s.ModRoot, true);
        Sandbox.MakeDlcRpf(Path.Combine(s.ModRoot, "Pack", "dlc.rpf"), "packb");
        Install(s, "b");
        Assert.True(File.Exists(Path.Combine(s.GameRoot, @"onigiri\dlcpacks\packb\dlc.rpf")));
        Assert.Contains("dlcpacks:/packb/", File.ReadAllText(s.Game.OnigiriDlcList));
        Assert.DoesNotContain("dlcpacks:/packb/", DlcListOf(s));
    }

    [Fact]
    public void Replace_inside_archive_is_undone_byte_for_byte()
    {
        using var s = new Sandbox();
        var rpf = new RpfService(s.Game);
        rpf.Write(s.Game.ModsUpdateRpf, "common/data/handling.meta", Encoding.UTF8.GetBytes("<CHandlingDataMgr>original</CHandlingDataMgr>"));
        s.ModFile("Fix/mods/update/update.rpf/common/data/handling.meta", "<CHandlingDataMgr>modded</CHandlingDataMgr>");
        s.ModFile("Fix/mods/update/update.rpf/common/data/new.meta", "<CHandlingDataMgr>new</CHandlingDataMgr>");

        var mod = Install(s);
        Assert.Equal("<CHandlingDataMgr>modded</CHandlingDataMgr>", Encoding.UTF8.GetString(rpf.TryRead(s.Game.ModsUpdateRpf, "common/data/handling.meta")!));
        Assert.NotNull(rpf.TryRead(s.Game.ModsUpdateRpf, "common/data/new.meta"));

        new ModService(s.Paths, s.Library).Uninstall(s.Game, mod);
        Assert.Equal("<CHandlingDataMgr>original</CHandlingDataMgr>", Encoding.UTF8.GetString(rpf.TryRead(s.Game.ModsUpdateRpf, "common/data/handling.meta")!));
        Assert.Null(rpf.TryRead(s.Game.ModsUpdateRpf, "common/data/new.meta"));
    }

    /// <summary>A fake but well-formed RSC7 resource: header + deflated body.</summary>
    static byte[] Resource(string body)
    {
        var packed = CodeWalker.GameFiles.ResourceBuilder.Compress(Encoding.UTF8.GetBytes(body));
        var data = new byte[16 + packed.Length];
        BitConverter.GetBytes(0x37435352u).CopyTo(data, 0);   // 'RSC7'
        BitConverter.GetBytes(13u).CopyTo(data, 4);            // ytd version
        // real resources carry their version inside the page flags
        BitConverter.GetBytes(CodeWalker.GameFiles.RpfResourceFileEntry.GetFlagsFromSize(body.Length, 0)).CopyTo(data, 8);
        BitConverter.GetBytes(CodeWalker.GameFiles.RpfResourceFileEntry.GetFlagsFromSize(0, 13)).CopyTo(data, 12);
        packed.CopyTo(data, 16);
        return data;
    }

    [Fact]
    public void Replaced_resources_are_restored_even_though_codewalker_recompresses_them()
    {
        using var s = new Sandbox();
        var rpf = new RpfService(s.Game);
        rpf.Write(s.Game.ModsUpdateRpf, "x64/textures/sign.ytd", Resource(new string('o', 5000)));
        var originalHash = RpfService.ContentHash(rpf.TryRead(s.Game.ModsUpdateRpf, "x64/textures/sign.ytd")!);

        var modded = Path.Combine(s.ModRoot, @"Sign\mods\update\update.rpf\x64\textures\sign.ytd");
        Directory.CreateDirectory(Path.GetDirectoryName(modded)!);
        File.WriteAllBytes(modded, Resource(new string('m', 7000)));
        var mod = Install(s);
        Assert.NotEqual(originalHash, RpfService.ContentHash(rpf.TryRead(s.Game.ModsUpdateRpf, "x64/textures/sign.ytd")!));

        Assert.Empty(new ModService(s.Paths, s.Library).Uninstall(s.Game, mod));
        Assert.Equal(originalHash, RpfService.ContentHash(rpf.TryRead(s.Game.ModsUpdateRpf, "x64/textures/sign.ytd")!));
    }

    [Fact]
    public void Layered_mods_uninstall_in_any_order()
    {
        using var s = new Sandbox();
        var target = Path.Combine(s.GameRoot, @"scripts\shared.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "vanilla");

        s.ModFile("A/scripts/shared.ini", "from A");
        var a = Install(s, "A");
        Directory.Delete(s.ModRoot, true);
        s.ModFile("B/scripts/shared.ini", "from B");
        var b = Install(s, "B");
        Assert.Equal("from B", File.ReadAllText(target));

        var service = new ModService(s.Paths, s.Library);
        service.Uninstall(s.Game, a);            // the lower layer goes first
        Assert.Equal("from B", File.ReadAllText(target));
        service.Uninstall(s.Game, s.Library.Find(b.Id)!);
        Assert.Equal("vanilla", File.ReadAllText(target));
    }

    [Fact]
    public void Failure_part_way_rolls_everything_back()
    {
        using var s = new Sandbox();
        s.ModFile("M/M.asi");
        s.ModFile("M/scripts/a.ini");
        var plan = new ModAnalyzer(s.Game).Analyze(s.ModRoot, "m");
        plan.Ops.Add(new InstallOp { Kind = OpKind.CopyFile, Source = Path.Combine(s.ModRoot, "missing.dll"), SourceRel = "missing.dll", Target = @"scripts\missing.dll" });
        Assert.ThrowsAny<Exception>(() => new ModInstaller(s.Game, s.Paths, s.Library).Install(plan, new InstalledMod { Name = "m" }));
        Assert.False(File.Exists(Path.Combine(s.GameRoot, "M.asi")));
        Assert.False(Directory.Exists(Path.Combine(s.GameRoot, "scripts")));
        Assert.Empty(s.Library.Mods);
    }

    [Fact]
    public void Disable_then_enable_reinstalls_the_same_choices()
    {
        using var s = new Sandbox();
        s.ModFile("M/Legacy/M.asi");
        s.ModFile("M/Enhanced/M.asi");
        var archive = Path.Combine(s.Root, "m.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(s.ModRoot, archive);
        var service = new ModService(s.Paths, s.Library);
        var plan = service.Prepare(s.Game, archive, "m");
        plan.Select(plan.Variants[0].Id, "Enhanced"); // player overrides the Legacy default
        var mod = service.Install(s.Game, plan, new InstalledMod { Name = "m" });
        Assert.True(File.Exists(Path.Combine(s.GameRoot, "M.asi")));
        Assert.NotNull(mod.ArchivePath);

        service.Disable(s.Game, mod);
        Assert.False(File.Exists(Path.Combine(s.GameRoot, "M.asi")));
        Assert.False(s.Library.Find(mod.Id)!.Enabled);

        service.Enable(s.Game, s.Library.Find(mod.Id)!);
        Assert.Equal("Enhanced", s.Library.Find(mod.Id)!.Choices.Values.Single());
        Assert.True(File.Exists(Path.Combine(s.GameRoot, "M.asi")));
    }
}
