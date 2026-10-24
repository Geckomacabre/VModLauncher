using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using VModLauncher.Core.Features;
using VModLauncher.Core.Game;
using VModLauncher.Core.Install;
using Xunit;
using static VModLauncher.Tests.Features.FeatureFixtures;

namespace VModLauncher.Tests.Features;

public class ConflictTests
{
    [Fact]
    public void Shared_files_show_up_on_both_mods_and_in_new_plans()
    {
        using var s = new Sandbox();
        var a = InstallZip(s, "A", d => Write(d, "A/scripts/shared.ini", "a"));
        var b = InstallZip(s, "B", d => Write(d, "B/scripts/shared.ini", "b"));
        var c = Conflicts.For(s.Library, s.Library.Find(a.Id)!);
        Assert.Equal(b.Id, Assert.Single(c).ModId);
        Assert.Contains(@"scripts\shared.ini", c[0].Paths);

        s.ModFile("C/scripts/shared.ini", "c");
        var plan = new ModAnalyzer(s.Game).Analyze(s.ModRoot, "C");
        var warnings = Conflicts.ForPlan(s.Library, s.Game, plan);
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, w => Assert.Contains("come back when you uninstall", w.Message));
    }
}

public class LaunchOptionTests
{
    [Fact]
    public void Legacy_keeps_battleye_in_args_txt_and_options_in_commandline_txt()
    {
        using var s = new Sandbox();
        File.WriteAllText(Path.Combine(s.GameRoot, "args.txt"), "-nobattleye -noBE");
        File.WriteAllText(Path.Combine(s.GameRoot, "commandline.txt"), "-ignoreDifferentVideoCard");

        var info = LaunchOptions.Get(s.Game);
        Assert.Equal("commandline.txt", info.File);
        Assert.Equal(new[] { "-ignoreDifferentVideoCard", "-nobattleye" }, info.Args);
        Assert.Contains(info.Known, k => k.Arg == "-safemode");

        LaunchOptions.Set(s.Game, s.Paths, new[] { "-nobattleye", "-ignoreDifferentVideoCard", "-windowed" });
        Assert.Equal("-nobattleye -noBE", File.ReadAllText(Path.Combine(s.GameRoot, "args.txt")));
        Assert.Equal("-ignoreDifferentVideoCard -windowed", File.ReadAllText(Path.Combine(s.GameRoot, "commandline.txt")));

        LaunchOptions.Set(s.Game, s.Paths, new[] { "-windowed" });
        Assert.Equal("", File.ReadAllText(Path.Combine(s.GameRoot, "args.txt")));
        Assert.Equal("-windowed", File.ReadAllText(Path.Combine(s.GameRoot, "commandline.txt")));
        Assert.NotEmpty(Directory.GetFiles(s.Paths.State, "args-legacy-*.txt")); // backed up first
    }

    [Fact]
    public void Enhanced_uses_args_txt_only()
    {
        using var s = new Sandbox(GameEdition.Enhanced);
        var info = LaunchOptions.Set(s.Game, s.Paths, new[] { "-windowed", "-nobattleye" });
        Assert.Equal("args.txt", info.File);
        Assert.Equal("-windowed -nobattleye -noBE", File.ReadAllText(Path.Combine(s.GameRoot, "args.txt")));
        Assert.False(File.Exists(Path.Combine(s.GameRoot, "commandline.txt")));
        Assert.DoesNotContain(info.Known, k => k.Arg == "-safemode");
    }
}

public class SpawnNameTests
{
    [Fact]
    public void Model_names_come_from_the_pack()
    {
        using var s = new Sandbox();
        var mod = InstallZip(s, "Cars", d =>
        {
            Directory.CreateDirectory(Path.Combine(d, "Cars"));
            MakePack(Path.Combine(d, "Cars", "dlc.rpf"), "mycars",
                ("common/data/levels/gta5/vehicles.meta",
                 "<CVehicleModelInfo__InitDataList><InitDatas><Item><modelName>zentorno2</modelName></Item><Item><modelName>adderx</modelName></Item></InitDatas></CVehicleModelInfo__InitDataList>"));
        });
        var r = SpawnNames.Compute(s.Game, s.Library.Find(mod.Id)!);
        Assert.Equal(new[] { "adderx", "zentorno2" }, r.Names);
        Assert.Equal("vehicle", r.Kind);
        Assert.True(SpawnNames.Ensure(s.Game, mod));
        Assert.False(SpawnNames.Ensure(s.Game, mod)); // cached
    }

    [Fact]
    public void Kind_is_guessed_before_the_packs_are_read()
    {
        Assert.Equal("script", SpawnNames.GuessKind(new VModLauncher.Core.Library.InstalledMod { Summary = "ASI plugin" }));
        var byPath = new VModLauncher.Core.Library.InstalledMod { Summary = "Loose files" };
        byPath.Entries.Add(new VModLauncher.Core.Library.JournalEntry { Type = "file", Path = "NoParachute.asi" });
        Assert.Equal("script", SpawnNames.GuessKind(byPath));
        Assert.Equal("other", SpawnNames.GuessKind(new VModLauncher.Core.Library.InstalledMod { Summary = "Add-on pack" }));
    }
}

public class DiagnosticsTests
{
    [Fact]
    public void Log_lines_that_matter_are_picked_out()
    {
        var shvdn = Diagnostics.Analyze("ScriptHookVDotNet.log", new[]
        {
            "[17:57:15] [INFO] Started script ViSwitch.ViSwitch.",
            "[17:57:16] [ERROR] Failed to instantiate script Foo.Bar because constructor threw an exception: System.NullReferenceException",
            "[17:57:17] [ERROR] Failed to instantiate script Foo.Bar because constructor threw an exception: System.NullReferenceException",
        });
        Assert.Single(shvdn); // duplicates collapse

        var rph = Diagnostics.Analyze("RagePluginHook.log", new[]
        {
            "[10/21/2026 2:22:14 PM.498] LSPD First Response: [WARN] GetOutfitVariation: Failed to find outfit lspd_cop",
            "[10/21/2026 2:22:14 PM.500] LSPD First Response: [TRACE] Failed to parse  as chance",
            "[10/21/2026 2:30:00 PM.000] Plugin \"MyCallouts\" crashed: System.Exception: boom",
        });
        Assert.Equal("Block", Assert.Single(rph).Level);

        var shv = Diagnostics.Analyze("ScriptHookV.log", new[] { "[18:00:00] FATAL: Unknown game version, check http://dev-c.com for updates" });
        Assert.Equal("Block", Assert.Single(shv).Level);
    }

    [Fact]
    public void Script_hook_log_verdict_reads_only_the_last_session()
    {
        using var s = new Sandbox();
        var log = Path.Combine(s.GameRoot, "ScriptHookV.log");
        File.WriteAllText(log,
            "[10:00:00] INIT: Started\n[10:00:00] FATAL: Unknown game version, check http://dev-c.com for updates\n" +
            "[11:00:00] INIT: Started\n[11:00:00] INIT: Success, game version is VER_1_0_3889_0\n[11:00:00] INIT: Registering script 'A.asi'\n");
        Assert.True(GameHealth.LastSessionVerdict(log, out _, out var build));
        Assert.Equal("3889", build);
        File.AppendAllText(log, "[12:00:00] INIT: Started\n[12:00:00] FATAL: Unknown game version, check http://dev-c.com for updates\n");
        Assert.False(GameHealth.LastSessionVerdict(log, out var msg, out _));
        Assert.Contains("Unknown game version", msg);
    }

    [Fact]
    public void Unmanaged_files_and_quarantine_round_trip()
    {
        using var s = new Sandbox();
        File.WriteAllText(Path.Combine(s.GameRoot, "bink2w64.dll"), "vanilla");
        File.WriteAllText(Path.Combine(s.GameRoot, "Stray.asi"), "x");
        Directory.CreateDirectory(Path.Combine(s.GameRoot, "scripts"));
        File.WriteAllText(Path.Combine(s.GameRoot, "scripts", "Loose.dll"), "x");
        File.WriteAllText(Path.Combine(s.GameRoot, "scripts", "Loose.log"), "x");
        InstallZip(s, "Owned", d => Write(d, "O/Owned.asi", "o"));

        var list = Diagnostics.Unmanaged(s.Game, s.Library);
        Assert.Contains(list, u => u.Path == "Stray.asi" && u.Kind == "asi");
        Assert.Contains(list, u => u.Path == @"scripts\Loose.dll" && u.Kind == "script");
        Assert.DoesNotContain(list, u => u.Path == "Owned.asi");
        Assert.DoesNotContain(list, u => u.Path == "bink2w64.dll");
        Assert.DoesNotContain(list, u => u.Path.EndsWith(".log"));

        var q = new Quarantine(s.Paths);
        var item = q.Move(s.Game, "Stray.asi");
        Assert.False(File.Exists(Path.Combine(s.GameRoot, "Stray.asi")));
        Assert.Single(new Quarantine(s.Paths).Items);
        new Quarantine(s.Paths).Restore(s.Game, item.Id);
        Assert.True(File.Exists(Path.Combine(s.GameRoot, "Stray.asi")));
        Assert.Empty(new Quarantine(s.Paths).Items);
    }
}

public class FrameworkTests
{
    sealed class FakeGitHub : HttpMessageHandler
    {
        public readonly List<string> Requests = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            if (url.Contains("/download/"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("zipdata")) });
            var repo = url.Split("/repos/")[1].Split("/releases")[0];
            var tag = repo.Contains("nightly") ? "v3.7.0-nightly.194" : "v2.2";
            var release = $"{{\"tag_name\":\"{tag}\",\"html_url\":\"https://github.com/{repo}/releases/tag/{tag}\",\"assets\":[" +
                          $"{{\"name\":\"symbols.zip\",\"browser_download_url\":\"https://github.com/{repo}/releases/download/{tag}/symbols.zip\"}}," +
                          $"{{\"name\":\"{repo.Split('/')[1]}.zip\",\"browser_download_url\":\"https://github.com/{repo}/releases/download/{tag}/{repo.Split('/')[1]}.zip\"}}]}}";
            var body = url.Contains("per_page") ? "[" + release + "]" : release;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public async Task Lists_per_edition_and_downloads_the_release_zip()
    {
        using var s = new Sandbox();
        var fake = new FakeGitHub();
        var fw = new Frameworks(new HttpClient(fake));
        var list = await fw.ListAsync(s.Game);
        Assert.Equal(4, list.Count);
        var shvdn = list.Single(f => f.Id == "shvdn");
        Assert.Equal("3.7.0.194", shvdn.LatestVersion);
        Assert.True(shvdn.CanInstall);
        Assert.False(list.Single(f => f.Id == "shvdne").CanInstall); // Enhanced-only on a Legacy game
        Assert.Equal("scripthookvdotnet-nightly.zip", shvdn.AssetName); // not the symbols zip
        Assert.Contains(fake.Requests, r => r.Contains("scripthookvdotnet-nightly/releases?per_page=1"));

        long last = 0;
        var path = await fw.DownloadAsync(s.Game, "lemonui", s.Paths.Downloads, (got, _) => last = got);
        Assert.Equal("zipdata", File.ReadAllText(path));
        Assert.Equal(7, last);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fw.DownloadAsync(s.Game, "shvdne", s.Paths.Downloads));
        Assert.Equal("2.2", Frameworks.Clean("v2.2"));
    }
}

public class FavoritesStorageTests
{
    [Fact]
    public void Favorites_toggle_and_persist()
    {
        using var s = new Sandbox();
        var fav = new Favorites(s.Paths.FavoritesFile);
        var item = JsonNode.Parse("{\"url\":\"https://www.lcpdfr.com/downloads/gta5mods/scripts/1-x/\",\"title\":\"X\",\"installed\":{\"id\":\"1\"}}")!.AsObject();
        Assert.True(fav.Toggle(item));
        var again = new Favorites(s.Paths.FavoritesFile);
        Assert.Single(again.All);
        Assert.Null(again.All[0]["installed"]);
        Assert.False(again.Toggle(item));
        Assert.Empty(new Favorites(s.Paths.FavoritesFile).All);
    }

    [Fact]
    public void Cleaning_never_touches_payloads_or_backups()
    {
        using var s = new Sandbox();
        var old = Path.Combine(s.Paths.Downloads, "old.zip");
        File.WriteAllText(old, "x");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-2));
        var fresh = Path.Combine(s.Paths.Downloads, "fresh.zip");
        File.WriteAllText(fresh, "x");
        var payload = Path.Combine(s.Paths.Payloads, "keep.zip");
        File.WriteAllText(payload, "x");
        Storage.Clean(s.Paths, "downloads");
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(payload));
        Assert.Throws<ArgumentException>(() => Storage.Clean(s.Paths, "payloads"));
        Assert.True(Storage.Report(s.Paths, s.Game).Payloads > 0);
    }
}
