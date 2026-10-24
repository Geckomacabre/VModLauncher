using System.Text;
using VModLauncher.Core;
using VModLauncher.Core.Features;
using VModLauncher.Core.Install;
using Xunit;
using static VModLauncher.Tests.Features.FeatureFixtures;

namespace VModLauncher.Tests.Features;

public class RepairTests
{
    static string Text(byte[]? b) => b == null ? "(none)" : Encoding.UTF8.GetString(b);

    [Fact]
    public void Repair_after_a_game_update_keeps_every_mod_and_uninstall_still_works()
    {
        using var s = new Sandbox(withModsRpf: false);
        var gameUpdate = Path.Combine(s.GameRoot, "update", "update.rpf");
        Sandbox.MakeUpdateRpf(Path.GetDirectoryName(gameUpdate)!, "update.rpf", DlcListText("dlcpacks:/mpbeach/"));
        var rpf = new RpfService(s.Game);
        rpf.Write(gameUpdate, "common/data/handling.meta", Encoding.UTF8.GetBytes("<CHandlingDataMgr>v1</CHandlingDataMgr>"));
        rpf.Write(gameUpdate, "common/data/gameconfig.xml", Encoding.UTF8.GetBytes("<CGameConfig>v1</CGameConfig>"));
        rpf.Forget(gameUpdate);

        // mod A edits handling.meta inside update.rpf (the launcher copies update.rpf into mods first)
        var a = InstallZip(s, "Handling", d => Write(d, "H/mods/update/update.rpf/common/data/handling.meta", "<CHandlingDataMgr>modded</CHandlingDataMgr>"));
        // mod B is an add-on pack
        var b = InstallZip(s, "Car", d => { Directory.CreateDirectory(Path.Combine(d, "Car")); Sandbox.MakeDlcRpf(Path.Combine(d, "Car", "dlc.rpf"), "packb"); });
        // something installed with OpenIV added its own pack and changed gameconfig without the launcher knowing
        var mods = s.Game.ModsUpdateRpf;
        var list = DlcList.Decode(rpf.TryRead(mods, DlcList.InnerPath)!, out var bom);
        rpf.Write(mods, DlcList.InnerPath, DlcList.Encode(DlcList.Add(list, "dlcpacks:/openivpack/"), bom));
        rpf.Write(mods, "common/data/gameconfig.xml", Encoding.UTF8.GetBytes("<CGameConfig>openiv</CGameConfig>"));
        rpf.Forget(mods);
        Assert.False(GameHealth.Check(s.Game, s.Paths).ModsUpdateStale);

        // Rockstar update: new DLC in the list, new handling.meta, newer file
        File.Delete(gameUpdate);
        Sandbox.MakeUpdateRpf(Path.GetDirectoryName(gameUpdate)!, "update.rpf", DlcListText("dlcpacks:/mpbeach/", "dlcpacks:/mpnew/"));
        rpf.Write(gameUpdate, "common/data/handling.meta", Encoding.UTF8.GetBytes("<CHandlingDataMgr>v2</CHandlingDataMgr>"));
        rpf.Write(gameUpdate, "common/data/gameconfig.xml", Encoding.UTF8.GetBytes("<CGameConfig>v2</CGameConfig>"));
        rpf.Forget(gameUpdate);

        var health = GameHealth.Check(s.Game, s.Paths);
        Assert.True(health.ModsUpdateStale);
        Assert.Contains("mpnew", health.StaleReason);
        Assert.Contains(health.Problems, p => p.Fix == "game.repair");

        var result = GameRepair.Run(s.Game, s.Paths, s.Library);
        Assert.True(result.Rebuilt);
        Assert.Equal(2, result.RestoredItems);         // packb + openivpack
        Assert.Equal(1, result.RestoredEntries);       // handling.meta
        Assert.True(File.Exists(s.Game.Resolve(result.Backup!)));
        Assert.Contains(result.Problems, p => p.Contains("gameconfig.xml"));

        var fresh = new RpfService(s.Game);
        var items = DlcList.Items(DlcList.Decode(fresh.TryRead(mods, DlcList.InnerPath)!, out _));
        Assert.Contains("dlcpacks:/mpnew/", items);
        Assert.Contains("dlcpacks:/packb/", items);
        Assert.Contains("dlcpacks:/openivpack/", items);
        Assert.Equal("<CHandlingDataMgr>modded</CHandlingDataMgr>", Text(fresh.TryRead(mods, "common/data/handling.meta")));
        Assert.Equal("<CGameConfig>v2</CGameConfig>", Text(fresh.TryRead(mods, "common/data/gameconfig.xml")));
        Assert.False(GameHealth.Check(s.Game, s.Paths).ModsUpdateStale);

        // uninstalling the handling mod now restores the NEW game's original, not the old one
        var service = new ModService(s.Paths, s.Library);
        Assert.Empty(service.Uninstall(s.Game, s.Library.Find(a.Id)!));
        Assert.Equal("<CHandlingDataMgr>v2</CHandlingDataMgr>", Text(new RpfService(s.Game).TryRead(mods, "common/data/handling.meta")));
        Assert.Empty(service.Uninstall(s.Game, s.Library.Find(b.Id)!));
        Assert.DoesNotContain("dlcpacks:/packb/", DlcList.Items(DlcList.Decode(new RpfService(s.Game).TryRead(mods, DlcList.InnerPath)!, out _)));
    }

    [Fact]
    public void Newer_game_file_without_new_dlc_is_reported_by_date()
    {
        using var s = new Sandbox(withModsRpf: false);
        var gameUpdate = Path.Combine(s.GameRoot, "update", "update.rpf");
        Sandbox.MakeUpdateRpf(Path.GetDirectoryName(gameUpdate)!, "update.rpf", DlcListText("dlcpacks:/mpbeach/"));
        new RpfService(s.Game).EnsureModsCopy(@"update\update.rpf", out _);
        File.SetCreationTimeUtc(s.Game.ModsUpdateRpf, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(gameUpdate, DateTime.UtcNow.AddDays(-1));
        var h = GameHealth.Check(s.Game, s.Paths);
        Assert.True(h.ModsUpdateStale);

        // after a repair the launcher remembers which game file the copy came from
        GameRepair.Run(s.Game, s.Paths, s.Library);
        Assert.False(GameHealth.Check(s.Game, s.Paths).ModsUpdateStale);
    }

    [Fact]
    public void Nothing_to_repair_without_a_mods_copy()
    {
        using var s = new Sandbox(withModsRpf: false);
        var r = GameRepair.Run(s.Game, s.Paths, s.Library);
        Assert.False(r.Rebuilt);
    }
}
