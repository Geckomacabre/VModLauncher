using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using VModLauncher.Core.Features;
using VModLauncher.Core.Game;
using VModLauncher.Core.Library;

namespace VModLauncher.Host;

/// <summary>Wave-2 commands (see docs/bridge.md, "Wave 2"). Returns (false, null) for commands it doesn't know.</summary>
public sealed partial class Bridge
{
    ProfileStore? profileStore;
    ProfileStore Profiles => profileStore ??= new ProfileStore(App.Paths.ProfilesFile);

    Favorites? favoriteStore;
    Favorites FavoriteStore => favoriteStore ??= new Favorites(App.Paths.FavoritesFile);

    Frameworks? frameworkSource;
    Frameworks FrameworkSource => frameworkSource ??= new Frameworks(new HttpClient { Timeout = TimeSpan.FromMinutes(10) });

    Quarantine? quarantineStore;
    Quarantine QuarantineStore => quarantineStore ??= new Quarantine(App.Paths);

    /// <summary>Only paths the last diag.report showed may be opened or moved (the page can't name arbitrary files).</summary>
    readonly HashSet<string> reportedFiles = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> reportedUnmanaged = new(StringComparer.OrdinalIgnoreCase);
    int spawnRefreshRunning;
    GameInstall? spawnRefreshNext;

    private partial async Task<(bool Handled, object? Result)> HandleFeatureAsync(string cmd, JsonElement args)
    {
        switch (cmd)
        {
            // ------------------------------------------------------------ profiles
            case "profiles.list":
            {
                var g = Game;
                return (true, Profiles.ForGame(g.Id).Select(ProfileDto).ToList());
            }
            case "profiles.save":
            {
                var g = Game;
                var p = Profiles.SaveCurrent(library, g, Str(args, "name"), StrOrNull(args, "id"));
                return (true, ProfileDto(p));
            }
            case "profiles.apply":
            {
                var g = Game;
                var p = Profiles.Find(Str(args, "id")) ?? throw new BridgeException("ERROR", "That profile doesn't exist any more.");
                if (GameLauncher.IsGameRunning(g)) throw new BridgeException("BLOCKED", $"{g.Name} is running. Close the game first.");
                var result = await Busy(() => Task.Run(() => ProfileStore.Apply(p, service, g, (text, fraction) =>
                    Emit("install.progress", new { planId = "profile", stage = $"Switching to {p.Name}", fraction, detail = text }))));
                Emit("library.changed", new { gameId = g.Id });
                Emit("game.changed", Status(g));
                Toast(result.Problems.Count == 0 ? "ok" : "warn",
                    result.Problems.Count == 0 ? $"Switched to {p.Name}." : $"Switched to {p.Name} with {result.Problems.Count} problem(s).");
                return (true, new { enabled = result.Enabled, disabled = result.Disabled, problems = result.Problems, missing = result.Missing });
            }
            case "profiles.delete":
                Profiles.Delete(Str(args, "id"));
                return (true, true);
            case "profiles.export":
            {
                var g = Game;
                var p = Profiles.Find(Str(args, "id")) ?? throw new BridgeException("ERROR", "That profile doesn't exist any more.");
                return (true, new { code = ProfileStore.Export(p, g.Edition) });
            }
            case "profiles.import":
            {
                var g = Game;
                try
                {
                    var (profile, missing, edition) = Profiles.Import(Str(args, "code"), library, g);
                    if (edition != null && !edition.Equals(g.Edition.ToString(), StringComparison.OrdinalIgnoreCase))
                        Toast("warn", $"This profile was made for GTA V {edition}; you're on {g.Edition}. Some mods may not work.");
                    return (true, new { profile = ProfileDto(profile), missing });
                }
                catch (FormatException ex) { throw new BridgeException("ERROR", ex.Message); }
            }

            // ------------------------------------------------------------ health / repair
            case "game.health":
            {
                var g = Game;
                return (true, await Task.Run(() => GameHealth.Check(g, App.Paths)));
            }
            case "game.repair":
            {
                var g = Game;
                var r = await Busy(() => Task.Run(() => GameRepair.Run(g, App.Paths, library, (text, fraction) =>
                    Emit("install.progress", new { planId = "repair", stage = "Rebuilding your mods copy of update.rpf", fraction, detail = text }))));
                var health = await Task.Run(() => GameHealth.Check(g, App.Paths));
                Emit("library.changed", new { gameId = g.Id });
                Emit("game.changed", Status(g));
                return (true, new { health, rebuilt = r.Rebuilt, restoredItems = r.RestoredItems, restoredEntries = r.RestoredEntries, backup = r.Backup, problems = r.Problems });
            }

            // ------------------------------------------------------------ diagnostics
            case "diag.report":
            {
                var g = Game;
                var report = await Task.Run(() => Diagnostics.Build(g, library));
                reportedFiles.Clear();
                foreach (var l in report.Logs.Where(l => l.Exists)) reportedFiles.Add(l.Path);
                foreach (var c in report.Crashes) reportedFiles.Add(c.Path);
                reportedUnmanaged.Clear();
                foreach (var u in report.Unmanaged) reportedUnmanaged.Add(u.Path);
                return (true, report);
            }
            case "diag.openLog":
            {
                var path = Str(args, "path");
                if (!reportedFiles.Contains(path) || !File.Exists(path)) throw new BridgeException("ERROR", "Refresh Diagnostics first.");
                if (path.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                else
                    Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
                return (true, true);
            }
            case "diag.quarantine":
            {
                var g = Game;
                var rel = Str(args, "path");
                if (!reportedUnmanaged.Contains(rel)) throw new BridgeException("ERROR", "Refresh Diagnostics first.");
                if (GameLauncher.IsGameRunning(g)) throw new BridgeException("BLOCKED", $"{g.Name} is running. Close the game first: loaded files can't be moved.");
                try
                {
                    var item = QuarantineStore.Move(g, rel);
                    reportedUnmanaged.Remove(rel);
                    Emit("game.changed", Status(g));
                    return (true, new { quarantined = item });
                }
                catch (IOException ex) { throw new BridgeException("ERROR", ex.Message); }
            }
            case "diag.quarantineList":
            {
                var g = Game;
                return (true, QuarantineStore.Items.Where(i => i.GameId == g.Id).OrderByDescending(i => i.MovedAt).ToList());
            }
            case "diag.restore":
            {
                var g = Game;
                if (GameLauncher.IsGameRunning(g)) throw new BridgeException("BLOCKED", $"{g.Name} is running. Close the game first.");
                try { QuarantineStore.Restore(g, Str(args, "id")); }
                catch (Exception ex) when (ex is IOException or ArgumentException) { throw new BridgeException("ERROR", ex.Message); }
                Emit("game.changed", Status(g));
                return (true, true);
            }

            // ------------------------------------------------------------ launch options
            case "game.launchOptions":
                return (true, LaunchOptions.Get(Game));
            case "game.setLaunchOptions":
            {
                var g = Game;
                var list = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array
                    ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                    : new List<string>();
                try { return (true, LaunchOptions.Set(g, App.Paths, list)); }
                catch (UnauthorizedAccessException) { throw new BridgeException("BLOCKED", "Windows won't let the launcher write to the game folder. Use \"Fix folder access\" on Home."); }
            }

            // ------------------------------------------------------------ frameworks
            case "frameworks.list":
            {
                var g = Game;
                var list = await FrameworkSource.ListAsync(g);
                return (true, list.Select(f => new { id = f.Id, name = f.Name, installedVersion = f.InstalledVersion, latestVersion = f.LatestVersion, releaseUrl = f.ReleaseUrl, forEdition = f.ForEdition, canInstall = f.CanInstall }).ToList());
            }
            case "frameworks.install":
            {
                var g = Game;
                var id = Str(args, "id");
                var info = (await FrameworkSource.ListAsync(g)).FirstOrDefault(f => f.Id == id)
                           ?? throw new BridgeException("ERROR", "Unknown framework " + id);
                if (!info.CanInstall) throw new BridgeException("BLOCKED", info.ForEdition is "Legacy" or "Enhanced" && info.ForEdition != g.Edition.ToString()
                    ? $"{info.Name} is for GTA V {info.ForEdition}." : $"No download found for {info.Name}. Open {info.ReleaseUrl}.");
                _ = InstallFrameworkAsync(g, info);
                return (true, true);
            }

            // ------------------------------------------------------------ favourites / storage
            case "favorites.list":
                return (true, FavoriteStore.All.Select(o =>
                {
                    var copy = (JsonObject)o.DeepClone();
                    copy["installed"] = InstalledInfo(copy["url"]?.GetValue<string>() ?? "");
                    return copy;
                }).ToList());
            case "favorites.toggle":
            {
                if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
                    throw new BridgeException("ERROR", "No mod to favourite.");
                try { return (true, new { favorite = FavoriteStore.Toggle(JsonNode.Parse(item.GetRawText())!.AsObject()) }); }
                catch (ArgumentException ex) { throw new BridgeException("ERROR", ex.Message); }
            }
            case "storage.report":
            {
                var g = selected;
                return (true, await Task.Run(() => Storage.Report(App.Paths, g)));
            }
            case "storage.clean":
            {
                var what = Str(args, "what");
                var g = selected;
                try { await Task.Run(() => Storage.Clean(App.Paths, what)); }
                catch (ArgumentException ex) { throw new BridgeException("ERROR", ex.Message); }
                return (true, await Task.Run(() => Storage.Report(App.Paths, g)));
            }
        }
        // wave 3 tools, each in its own file (Bridge.Textures.cs, Bridge.PackBuilder.cs, Bridge.Packs.cs)
        var tool = await HandleTexturesAsync(cmd, args);
        if (tool.Handled) return tool;
        tool = await HandlePackBuilderAsync(cmd, args);
        if (tool.Handled) return tool;
        return await HandlePacksAsync(cmd, args);
    }

    private partial Task<(bool Handled, object? Result)> HandleTexturesAsync(string cmd, JsonElement args);
    private partial Task<(bool Handled, object? Result)> HandlePackBuilderAsync(string cmd, JsonElement args);
    private partial Task<(bool Handled, object? Result)> HandlePacksAsync(string cmd, JsonElement args);

    object ProfileDto(Profile p) => new
    {
        id = p.Id,
        name = p.Name,
        gameId = p.GameId,
        createdAt = p.CreatedAt.ToString("o"),
        updatedAt = p.UpdatedAt.ToString("o"),
        mods = p.Mods.Select(m => new { id = m.Id, name = m.Name, source = m.Source, pageUrl = m.PageUrl, version = m.Version }),
        active = ProfileStore.IsActive(p, library),
    };

    async Task InstallFrameworkAsync(GameInstall g, FrameworkInfo info)
    {
        var key = info.Id;
        try
        {
            Emit("download.progress", new { key, title = info.Name, received = 0L, total = (long?)null, state = "downloading", file = info.AssetName, error = (string?)null });
            var path = await FrameworkSource.DownloadAsync(g, info.Id, App.Paths.Downloads, (received, total) =>
                Emit("download.progress", new { key, title = info.Name, received, total, state = "downloading", file = info.AssetName, error = (string?)null }));
            Emit("download.progress", new { key, title = info.Name, received = new FileInfo(path).Length, total = (long?)new FileInfo(path).Length, state = "done", file = info.AssetName, error = (string?)null });
            await window.Dispatcher.InvokeAsync(() => PrepareAsync(path, new DownloadRequest
            {
                Key = info.ReleasesPage,
                Title = info.Name,
                Site = "local",
                Version = info.LatestVersion,
                Author = info.Repo.Split('/')[0],
            })).Task.Unwrap();
        }
        catch (Exception ex)
        {
            App.Log("frameworks", ex.ToString());
            Emit("download.progress", new { key, title = info.Name, received = 0L, total = (long?)null, state = "failed", file = info.AssetName, error = ex.Message });
            Toast("error", $"Couldn't download {info.Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Fills spawn names / kind for library mods that don't have them yet (reads their dlc packs in the
    /// background, one archive session at a time) and tells the page when anything changed.
    /// </summary>
    internal Task RefreshSpawnNamesAsync(GameInstall g)
    {
        // asked while a pass runs (an install during the start-up pass): one more pass afterwards, or the new mod waits for the next start
        if (Interlocked.Exchange(ref spawnRefreshRunning, 1) == 1)
        {
            Interlocked.Exchange(ref spawnRefreshNext, g);
            return Task.CompletedTask;
        }
        return Task.Run(() =>
        {
            try
            {
                bool changed = false;
                foreach (var m in library.ForGame(g.Id).Where(m => m.Enabled && (m.SpawnNames == null || m.Kind == null)).ToList())
                {
                    try { changed |= SpawnNames.Ensure(g, m); }
                    catch (Exception ex) { App.Log("spawn", $"{m.Name}: {ex.Message}"); }
                }
                if (changed)
                {
                    library.Save();
                    Emit("library.changed", new { gameId = g.Id });
                }
            }
            finally
            {
                Interlocked.Exchange(ref spawnRefreshRunning, 0);
                var next = Interlocked.Exchange(ref spawnRefreshNext, null);
                if (next != null) _ = RefreshSpawnNamesAsync(next);
            }
        });
    }

    /// <summary>Library extras for the page: spawn names, kind and file conflicts with other enabled mods.</summary>
    internal object LibraryExtras(InstalledMod m) => new
    {
        spawnNames = m.SpawnNames ?? new List<string>(),
        kind = m.Kind ?? SpawnNames.GuessKind(m), // until the background pass reads its packs
        conflicts = Conflicts.For(library, m).Select(c => new { modId = c.ModId, modName = c.ModName, paths = c.Paths }),
    };
}
