using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using VModLauncher.Core;
using VModLauncher.Core.Features;
using VModLauncher.Core.Game;
using VModLauncher.Core.Install;
using VModLauncher.Core.Library;

namespace VModLauncher.Host;

/// <summary>Answers the launcher page's requests (see docs/bridge.md) and pushes events back to it.</summary>
public sealed partial class Bridge : IDisposable
{
    sealed class PlanEntry
    {
        public required string Id;
        public required InstallPlan Plan;
        public required DownloadRequest Meta;
        public required GameInstall Game;
    }

    sealed class BridgeException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    readonly MainWindow window;
    readonly CoreWebView2 ui;
    readonly Settings settings;
    readonly ModLibrary library;
    readonly ModService service;
    readonly Dictionary<string, SiteSession> sites = new();
    readonly Dictionary<string, PlanEntry> plans = new();
    readonly Dictionary<string, JsonElement> details = new(StringComparer.OrdinalIgnoreCase);
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, GameIndex?> indexes = new();
    List<GameInstall> games = new();
    GameInstall? selected;

    public Bridge(MainWindow window, CoreWebView2 ui)
    {
        this.window = window;
        this.ui = ui;
        settings = Settings.Load(App.Paths.SettingsFile);
        library = new ModLibrary(App.Paths);
        service = new ModService(App.Paths, library);
        ui.WebMessageReceived += OnMessage;
        App.Art.Ready += set => Emit("art.ready", new { hero = set.Hero, panels = set.Panels, status = set.Status });
        RefreshGames();
        _ = WarmUpSitesAsync();
    }

    /// <summary>
    /// Opens both hidden site tabs a moment after start, so LCPDFR's browser check (5-45 s the
    /// first time) has finished before the player clicks its tab.
    /// </summary>
    async Task WarmUpSitesAsync()
    {
        await Task.Delay(1500);
        var first = settings.DefaultSite == "lcpdfr" ? "lcpdfr" : "gta5mods";
        foreach (var site in new[] { first, first == "lcpdfr" ? "gta5mods" : "lcpdfr" })
        {
            try { await Site(site).CallAsync("categories", null); }
            catch (Exception ex) { App.Log("site", $"{site} warm-up: {ex.Message}"); }
        }
    }

    /// <summary>Wave-2 commands, implemented in Bridge.Features.cs.</summary>
    private partial Task<(bool Handled, object? Result)> HandleFeatureAsync(string cmd, JsonElement args);

    // ---------------------------------------------------------------- plumbing

    async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? id = null;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("cmd", out var cmdEl)) return;
            id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var cmd = cmdEl.GetString() ?? "";
            var args = root.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object ? a.Clone() : default;

            object? result;
            if (cmd == "mod.importDropped")
            {
                var paths = e.AdditionalObjects?.OfType<CoreWebView2File>().Select(f => f.Path).ToList() ?? new();
                foreach (var p in paths) _ = PrepareLocalAsync(p);
                result = paths.Count > 0;
            }
            else
            {
                result = await HandleAsync(cmd, args);
            }
            Reply(id, true, result, null, null);
        }
        catch (BridgeException ex)
        {
            Reply(id, false, null, ex.Message, ex.Code);
        }
        catch (InstallBlockedException ex)
        {
            Reply(id, false, null, ex.Message, "BLOCKED");
        }
        catch (SiteChallengeException ex)
        {
            App.Log("site", "browser check didn't clear by itself: " + ex.Url);
            Reply(id, false, null, ex.Message, "CHALLENGE");
        }
        catch (Exception ex)
        {
            App.Log("bridge", ex.ToString());
            Reply(id, false, null, ex.Message, "ERROR");
        }
    }

    void Reply(string? id, bool ok, object? result, string? error, string? code)
    {
        if (id == null) return;
        var payload = ok
            ? JsonSerializer.Serialize(new { id, ok, result }, Json)
            : JsonSerializer.Serialize(new { id, ok, error, code }, Json);
        window.Dispatcher.InvokeAsync(() =>
        {
            try { ui.PostWebMessageAsJson(payload); } catch { } // the page may be gone during shutdown
        });
    }

    // ---------------------------------------------------------------- busy (installs must finish before the window closes)

    int busy;

    /// <summary>True while something is changing the game; closing then would leave it half-modded.</summary>
    public bool IsBusy => Volatile.Read(ref busy) > 0;

    async Task<T> Busy<T>(Func<Task<T>> work)
    {
        Interlocked.Increment(ref busy);
        try { return await work(); }
        finally { Interlocked.Decrement(ref busy); }
    }

    void Emit(string name, object? data)
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?> { ["event"] = name, ["data"] = data }, Json);
        window.Dispatcher.InvokeAsync(() =>
        {
            try { ui.PostWebMessageAsJson(payload); } catch { }
        });
    }

    void Toast(string kind, string text) => Emit("toast", new { kind, text });

    static string Str(JsonElement args, string name, string? fallback = null) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : fallback ?? "";

    static string? StrOrNull(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static int Int(JsonElement args, string name, int fallback = 0) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : fallback;

    static bool Bool(JsonElement args, string name, bool fallback = false) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && (v.ValueKind is JsonValueKind.True or JsonValueKind.False) ? v.GetBoolean() : fallback;

    GameInstall Game => selected ?? throw new BridgeException("BLOCKED",
        $"No GTA V{(App.LockedEdition is { } e ? " " + e : "")} install found. Add your game folder in Settings.");

    // ---------------------------------------------------------------- commands

    async Task<object?> HandleAsync(string cmd, JsonElement args)
    {
        switch (cmd)
        {
            case "app.init":
                return new
                {
                    version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0",
                    games = games.Select(GameDto).ToList(),
                    selectedGameId = selected?.Id,
                    lockedEdition = App.LockedEdition?.ToString(),
                    productName = App.ProductName,
                    settings = SettingsDto(),
                };

            case "art.list":
            {
                // real installs only (read-only), whatever edition this build manages: Legacy art decodes everywhere
                var set = App.Art.Ensure(GameLocator.FindAll(settings.ExtraGameRoots));
                return new { hero = set.Hero, panels = set.Panels, status = set.Status };
            }

            case "games.select":
            {
                var id = Str(args, "id");
                selected = games.FirstOrDefault(g => g.Id == id) ?? throw new BridgeException("ERROR", "Unknown game");
                settings.SelectedGameId = selected.Id;
                settings.Save(App.Paths.SettingsFile);
                return Status(selected);
            }

            case "games.refresh":
                RefreshGames();
                return games.Select(GameDto).ToList();

            case "games.addFolder":
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Pick your GTA V folder (the one with GTA5.exe or GTA5_Enhanced.exe)" };
                if (dlg.ShowDialog(window) != true) return games.Select(GameDto).ToList();
                var g = GameInstall.TryOpen(dlg.FolderName)
                        ?? throw new BridgeException("ERROR", "That folder has no GTA5.exe or GTA5_Enhanced.exe.");
                if (App.LockedEdition is { } locked && g.Edition != locked)
                    throw new BridgeException("ERROR", $"That's GTA V {g.Edition}. This launcher is the {locked} one: use V Mod Launcher {g.Edition} for that game.");
                if (!settings.ExtraGameRoots.Contains(g.Root, StringComparer.OrdinalIgnoreCase)) settings.ExtraGameRoots.Add(g.Root);
                settings.SelectedGameId = g.Id;
                settings.Save(App.Paths.SettingsFile);
                RefreshGames();
                return games.Select(GameDto).ToList();
            }

            case "game.status":
                return Status(Game);

            case "game.play":
            {
                var g = Game;
                if (GameLauncher.IsGameRunning()) throw new BridgeException("BLOCKED", "GTA V is already running.");
                if (Str(args, "mode") == "lspdfr")
                {
                    if (!GameLauncher.CanPlayLspdfr(g)) throw new BridgeException("BLOCKED", "RAGE Plugin Hook isn't installed. Install LSPDFR first.");
                    if (!GameLauncher.AreModsEnabled(g)) GameLauncher.SetModsEnabled(g, true);
                    GameLauncher.PlayLspdfr(g);
                }
                else
                {
                    GameLauncher.Play(g);
                }
                _ = Task.Delay(8000).ContinueWith(_ => Emit("game.changed", Status(g)));
                return true;
            }

            case "game.setModsEnabled":
            {
                var g = Game;
                if (GameLauncher.IsGameRunning(g)) throw new BridgeException("BLOCKED", "Close GTA V first.");
                try { GameLauncher.SetModsEnabled(g, Bool(args, "enabled", true)); }
                catch (InvalidOperationException ex) { throw new BridgeException("BLOCKED", ex.Message); }
                return Status(g);
            }

            case "game.fixAccess":
            {
                var g = Game;
                var ok = await FolderAccess.GrantAsync(g.Root);
                Toast(ok && g.IsWritable() ? "ok" : "warn",
                    ok && g.IsWritable() ? "The launcher can now write to your game folder." : "Windows didn't grant access. Try again and choose Yes on the prompt.");
                return Status(g);
            }

            case "game.openFolder":
            {
                var dir = Path.Combine(Game.Root, Str(args, "sub"));
                if (!Directory.Exists(dir)) dir = Game.Root;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
                return true;
            }

            case "site.categories":
            {
                var s = Site(Str(args, "site"));
                var cats = await s.CallAsync("categories", null);
                var sorts = await s.CallAsync("sorts", null);
                return new { categories = cats, sorts };
            }

            case "site.list":
            {
                var r = await Site(Str(args, "site")).CallAsync("list", new
                {
                    category = StrOrNull(args, "category"),
                    sort = StrOrNull(args, "sort"),
                    page = Int(args, "page", 1),
                });
                return AnnotateInstalled(r);
            }

            case "site.search":
            {
                var query = Str(args, "query").Trim();
                if (query.Length == 0) throw new BridgeException("ERROR", "Type something to search for.");
                var r = await Site(Str(args, "site")).CallAsync("search", new { query, page = Int(args, "page", 1) });
                return AnnotateInstalled(r);
            }

            case "site.detail":
            {
                var url = Str(args, "url");
                var d = await Site(Str(args, "site")).CallAsync("detail", url);
                details[Norm(url)] = d;
                var node = JsonNode.Parse(d.GetRawText())!.AsObject();
                node["installed"] = InstalledInfo(url);
                return node;
            }

            case "site.open":
            {
                var url = Str(args, "url");
                if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) throw new BridgeException("ERROR", "Only https pages can be opened.");
                OpenSiteWindow(url, new DownloadRequest { Site = SiteFromUrl(url) },
                    "This is the real site. Sign in or pass its check here if it asks; anything you download in this window is installed by the launcher.");
                return true;
            }

            case "mod.download":
                return await StartDownloadAsync(args);

            case "mod.importFile":
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Pick a mod to install",
                    Filter = "Mods (*.zip;*.rar;*.7z;*.oiv;*.asi;*.dll;*.rpf)|*.zip;*.rar;*.7z;*.oiv;*.asi;*.dll;*.rpf|All files (*.*)|*.*",
                    Multiselect = true,
                };
                if (dlg.ShowDialog(window) != true) return false;
                foreach (var f in dlg.FileNames) _ = PrepareLocalAsync(f);
                return true;
            }

            case "plan.get":
                return PlanDto(PlanOf(args));

            case "plan.select":
            {
                var p = PlanOf(args);
                p.Plan.Select(Str(args, "groupId"), Str(args, "optionId"));
                return PlanDto(p);
            }

            case "plan.toggleOp":
            {
                var p = PlanOf(args);
                var i = Int(args, "index", -1);
                if (i < 0 || i >= p.Plan.Ops.Count || p.Plan.Ops[i].Kind == OpKind.Skip) throw new BridgeException("ERROR", "That file can't be switched on.");
                p.Plan.Ops[i].Enabled = Bool(args, "enabled", true);
                return PlanDto(p);
            }

            case "plan.setTarget":
            {
                var p = PlanOf(args);
                try { p.Plan.SetManualTarget(Int(args, "index", -1), Str(args, "folder"), Bool(args, "wholeFolder")); }
                catch (ArgumentException ex) { throw new BridgeException("ERROR", ex.Message); }
                return PlanDto(p);
            }

            case "plan.readme":
            {
                var p = PlanOf(args);
                var rel = Str(args, "rel");
                var full = Path.GetFullPath(Path.Combine(p.Plan.StagingDir, rel.Replace('/', '\\')));
                if (!full.StartsWith(p.Plan.StagingDir, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                    throw new BridgeException("ERROR", "Readme not found");
                return new { rel, text = ReadText(full) };
            }

            case "plan.locate":
            {
                var p = PlanOf(args);
                var g = p.Game;
                var progress = new Progress<string>(t => Emit("install.progress", new { planId = p.Id, stage = "Scanning your game's archives", fraction = (double?)null, detail = t }));
                var index = await Task.Run(() => GameIndex.Build(g, App.Paths.Cache, progress));
                indexes[g.Id] = index;
                var old = p.Plan;
                var fresh = await Task.Run(() =>
                {
                    using (RpfService.Session(g)) return new ModAnalyzer(g, index).Analyze(old.StagingDir, old.ModName, old.ArchivePath);
                });
                // keep what the player already decided: variant choices, switched-off files, hand placements
                foreach (var v in old.Variants)
                {
                    var nv = fresh.Variants.FirstOrDefault(x => x.Id == v.Id);
                    if (nv != null && nv.Options.Any(o => o.Id == v.Selected)) nv.Selected = v.Selected;
                }
                foreach (var o in old.Ops)
                {
                    var n = fresh.Ops.FirstOrDefault(x => x.SourceRel == o.SourceRel);
                    if (n == null) continue;
                    if (o.Reason == "Placed by you") { n.Kind = o.Kind; n.Target = o.Target; n.Enabled = true; n.Variant = null; n.Reason = o.Reason; }
                    else if (o.Kind != OpKind.Skip && !o.Enabled && n.Kind != OpKind.Skip) n.Enabled = false;
                }
                fresh.Warnings.AddRange(old.Warnings.Where(w => w.Message.StartsWith("The mod's page marks it") || w.Message.StartsWith("Downloaded from")));
                p.Plan = fresh;
                return PlanDto(p);
            }

            case "plan.install":
                return await InstallAsync(PlanOf(args));

            case "plan.cancel":
            {
                if (plans.Remove(Str(args, "planId"), out var p)) service.DeleteStaging(p.Plan);
                return true;
            }

            case "library.list":
                if (selected == null) return new List<object>();
                _ = RefreshSpawnNamesAsync(selected); // fills spawn names in the background, then library.changed
                lock (library.Gate) return library.ForGame(selected.Id).Select(ModDto).ToList<object>();

            case "library.setEnabled":
            {
                var mod = ModOf(args);
                var enable = Bool(args, "enabled", true);
                var g = Game;
                var problems = await Busy(() => Task.Run(() =>
                {
                    if (enable && !mod.Enabled) { service.Enable(g, mod, IndexFor(g)); return new List<string>(); }
                    if (!enable && mod.Enabled) return service.Disable(g, mod);
                    return new List<string>();
                }));
                Emit("library.changed", new { gameId = g.Id });
                Emit("game.changed", Status(g));
                ReportUndo(mod.Name, problems, enable ? "turned on" : "turned off");
                lock (library.Gate) return ModDto(library.Find(mod.Id) ?? mod);
            }

            case "library.uninstall":
            {
                var mod = ModOf(args);
                var g = Game;
                var problems = await Busy(() => Task.Run(() => service.Uninstall(g, mod)));
                Emit("library.changed", new { gameId = g.Id });
                Emit("game.changed", Status(g));
                ReportUndo(mod.Name, problems, "removed");
                return new { problems };
            }

            case "library.checkUpdates":
            {
                // one detail fetch per site mod (the extractors space requests a second apart)
                var results = new List<object>();
                foreach (var m in selected == null ? new List<InstalledMod>() : library.ForGame(selected.Id).Where(m => m.PageUrl != null && m.Source != "local").ToList())
                {
                    try
                    {
                        var d = await Site(m.Source).CallAsync("detail", m.PageUrl);
                        details[Norm(m.PageUrl!)] = d;
                        var latest = d.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                        bool newer = latest != null && m.Version != null && !string.Equals(latest.Trim(), m.Version.Trim(), StringComparison.OrdinalIgnoreCase);
                        results.Add(new { id = m.Id, latestVersion = latest, hasUpdate = newer });
                    }
                    catch (Exception ex)
                    {
                        results.Add(new { id = m.Id, latestVersion = (string?)null, hasUpdate = false, error = ex.Message });
                    }
                }
                return results;
            }

            case "library.files":
            {
                var mod = ModOf(args);
                return new { entries = mod.Entries.Select(e => new { type = e.Type, path = e.Path, inner = e.Inner, item = e.Item }) };
            }

            case "settings.set":
            {
                if (args.ValueKind == JsonValueKind.Object)
                {
                    if (args.TryGetProperty("keepDownloads", out var kd)) settings.KeepDownloads = kd.GetBoolean();
                    if (args.TryGetProperty("confirmUninstall", out var cu)) settings.ConfirmUninstall = cu.GetBoolean();
                    if (args.TryGetProperty("defaultSite", out var ds) && ds.GetString() is "gta5mods" or "lcpdfr") settings.DefaultSite = ds.GetString()!;
                    if (args.TryGetProperty("accent", out var ac) && ac.ValueKind == JsonValueKind.String) settings.Accent = ac.GetString()!;
                }
                settings.Save(App.Paths.SettingsFile);
                return SettingsDto();
            }

            case "shell.openUrl":
                Web.OpenExternal(Str(args, "url"));
                return true;

            default:
            {
                // wave-2 features (profiles, repair, diagnostics, ...) live in Bridge.Features.cs
                var (handled, featureResult) = await HandleFeatureAsync(cmd, args);
                if (handled) return featureResult;
                throw new BridgeException("ERROR", "Unknown command " + cmd);
            }
        }
    }

    // ---------------------------------------------------------------- games

    void RefreshGames()
    {
        // VML_SANDBOX=1 (testing): only the folders listed in settings, never the real installs
        games = Environment.GetEnvironmentVariable("VML_SANDBOX") == "1"
            ? settings.ExtraGameRoots.Select(r => GameInstall.TryOpen(r)).OfType<GameInstall>().ToList()
            : GameLocator.FindAll(settings.ExtraGameRoots);
        if (App.LockedEdition is { } locked) games = games.Where(g => g.Edition == locked).ToList();
        selected = games.FirstOrDefault(g => g.Id == settings.SelectedGameId) ?? games.FirstOrDefault();
    }

    GameIndex? IndexFor(GameInstall g)
    {
        if (!indexes.TryGetValue(g.Id, out var index))
        {
            index = GameIndex.TryLoad(g, App.Paths.Cache);
            indexes[g.Id] = index;
        }
        return index;
    }

    static object GameDto(GameInstall g) => new { id = g.Id, name = g.Name, edition = g.Edition.ToString(), store = g.Store, root = g.Root };

    object Status(GameInstall g) => new
    {
        id = g.Id,
        name = g.Name,
        edition = g.Edition.ToString(),
        store = g.Store,
        root = g.Root,
        running = GameLauncher.IsGameRunning(g),
        writable = g.IsWritable(),
        modsEnabled = GameLauncher.AreModsEnabled(g),
        canLspdfr = GameLauncher.CanPlayLspdfr(g),
        hasOnigiri = g.HasOnigiri,
        prereqs = Prerequisites.Check(g).Select(PrereqDto).ToList(),
        installedCount = library.ForGame(g.Id).Count(m => m.Enabled),
    };

    static object PrereqDto(PrereqStatus p) => new { id = p.Id, name = p.Name, state = p.State.ToString(), detail = p.Detail, getUrl = p.GetUrl };

    object SettingsDto() => new
    {
        keepDownloads = settings.KeepDownloads,
        confirmUninstall = settings.ConfirmUninstall,
        defaultSite = settings.DefaultSite,
        accent = settings.Accent,
    };

    // ---------------------------------------------------------------- sites

    SiteSession Site(string site)
    {
        if (site is not ("gta5mods" or "lcpdfr")) throw new BridgeException("ERROR", "Unknown site " + site);
        if (!sites.TryGetValue(site, out var s))
        {
            s = new SiteSession(site, new WindowInteropHelper(window).Handle);
            sites[site] = s;
        }
        return s;
    }

    static string SiteFromUrl(string url) =>
        url.Contains("lcpdfr.com", StringComparison.OrdinalIgnoreCase) ? "lcpdfr"
        : url.Contains("gta5-mods.com", StringComparison.OrdinalIgnoreCase) ? "gta5mods" : "local";

    static string Norm(string url) => url.Trim().TrimEnd('/').ToLowerInvariant();

    JsonNode? InstalledInfo(string url)
    {
        if (selected == null || string.IsNullOrEmpty(url)) return null;
        var m = library.ForGame(selected.Id).FirstOrDefault(x => x.PageUrl != null && Norm(x.PageUrl) == Norm(url));
        return m == null ? null : JsonSerializer.SerializeToNode(new { id = m.Id, version = m.Version, enabled = m.Enabled }, Json);
    }

    JsonNode AnnotateInstalled(JsonElement listResult)
    {
        var node = JsonNode.Parse(listResult.GetRawText())!;
        if (node["items"] is JsonArray items)
            foreach (var item in items.OfType<JsonObject>())
                item["installed"] = InstalledInfo(item["url"]?.GetValue<string>() ?? "");
        return node;
    }

    void OpenSiteWindow(string url, DownloadRequest request, string status)
    {
        var w = new SiteBrowserWindow(url, request, status) { Owner = window };
        w.DownloadProgress += (req, file, received, total, state) =>
            Emit("download.progress", new { key = req.Key ?? file, title = req.Title, received, total, state, file, error = (string?)null });
        w.DownloadFailed += (req, reason) =>
            Emit("download.progress", new { key = req.Key ?? req.Title, title = req.Title, received = 0, total = (long?)null, state = "failed", file = (string?)null, error = reason });
        w.DownloadFinished += (req, path) => _ = PrepareAsync(path, req);
        w.Closed += (_, _) =>
        {
            // the player may have signed in or passed a check: let the hidden tabs pick that up
            foreach (var s in sites.Values) s.Reset();
            if (request.Key != null && !w.ReceivedDownload)
                Emit("download.progress", new { key = request.Key, title = request.Title, received = 0, total = (long?)null, state = "cancelled", file = (string?)null, error = (string?)null });
        };
        w.Show();
    }

    async Task<object> StartDownloadAsync(JsonElement args)
    {
        var site = Str(args, "site");
        var url = Str(args, "url");
        var title = Str(args, "title", "Mod");
        if (!details.TryGetValue(Norm(url), out var detail))
        {
            detail = await Site(site).CallAsync("detail", url);
            details[Norm(url)] = detail;
        }
        var page = detail.TryGetProperty("downloadPageUrl", out var dp) && dp.ValueKind == JsonValueKind.String ? dp.GetString() : null;
        if (string.IsNullOrEmpty(page))
            throw new BridgeException("BLOCKED", $"{title} has no download on its page (the author may have pulled it). Open the page to check.");

        var request = new DownloadRequest
        {
            Key = url,
            Title = title,
            Site = site,
            Version = StrOrNull(args, "version") ?? (detail.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null),
            Author = StrOrNull(args, "author"),
            Image = StrOrNull(args, "image"),
        };
        var status = site == "lcpdfr"
            // signed out, LCPDFR's button just reloads the page without saying why
            ? "LCPDFR only lets members download. If the top of the page says \"Existing user? Sign In\", sign in there first (your password goes to LCPDFR, not the launcher). Then press \"Download this file\": the launcher picks the file up and installs it."
            : "GTA5-Mods' download page: the file starts after its countdown, or press its Download button. The launcher picks the file up and installs it.";
        Emit("download.progress", new { key = url, title, received = 0, total = (long?)null, state = "waiting", file = (string?)null, error = (string?)null });
        OpenSiteWindow(page!, request, status);
        return true;
    }

    // ---------------------------------------------------------------- plans

    Task PrepareLocalAsync(string path) =>
        // a dropped folder keeps its whole name ("Mod v1.2" isn't a file with a ".2" extension)
        PrepareAsync(path, new DownloadRequest { Key = null, Title = Directory.Exists(path) ? Path.GetFileName(path.TrimEnd('\\')) : Path.GetFileNameWithoutExtension(path), Site = "local" });

    async Task PrepareAsync(string path, DownloadRequest meta)
    {
        var key = meta.Key ?? path;
        try
        {
            var g = Game;
            var plan = await Task.Run(() => service.Prepare(g, path, meta.Title, IndexFor(g)));
            EditionWarning(plan, meta, g);
            if (meta.SourceHost != null)
                plan.Warnings.Add(new PlanWarning(WarningLevel.Warn, $"Downloaded from {meta.SourceHost}, not from the mod site. Make sure this is the file you meant to get."));
            var entry = new PlanEntry { Id = Guid.NewGuid().ToString("N")[..10], Plan = plan, Meta = meta, Game = g };
            await window.Dispatcher.InvokeAsync(() => plans[entry.Id] = entry);
            Emit("plan.ready", PlanDto(entry));
        }
        catch (Exception ex)
        {
            App.Log("prepare", ex.ToString());
            Emit("plan.failed", new { key, title = meta.Title, error = ex.Message });
        }
    }

    /// <summary>The mod's page says which game versions it's for (LCPDFR tags, GTA5-Mods "[Enhanced]" etc.).</summary>
    void EditionWarning(InstallPlan plan, DownloadRequest meta, GameInstall g)
    {
        if (meta.Key == null || !details.TryGetValue(Norm(meta.Key), out var d)) return;
        if (!d.TryGetProperty("editions", out var eds) || eds.ValueKind != JsonValueKind.Array) return;
        var editions = eds.EnumerateArray().Select(e => e.GetString() ?? "").Where(e => e.Length > 0).ToList();
        if (editions.Count == 0) return;
        var mine = g.Edition.ToString().ToLowerInvariant();
        if (editions.Contains(mine, StringComparer.OrdinalIgnoreCase)) return;
        var theirs = string.Join(" and ", editions.Select(e => char.ToUpper(e[0]) + e[1..]));
        plan.Warnings.Add(new PlanWarning(WarningLevel.Warn,
            $"The mod's page marks it for GTA V {theirs}, not {g.Edition}. It may not load (or may crash) on your game."));
    }

    PlanEntry PlanOf(JsonElement args) =>
        plans.TryGetValue(Str(args, "planId"), out var p) ? p : throw new BridgeException("ERROR", "That install was already finished or cancelled.");

    object PlanDto(PlanEntry e)
    {
        var plan = e.Plan;
        var prereqs = Prerequisites.Check(e.Game).ToDictionary(p => p.Id);
        return new
        {
            planId = e.Id,
            key = e.Meta.Key ?? plan.ArchivePath,
            modName = plan.ModName,
            title = e.Meta.Key == null && plan.PackageName != null ? plan.PackageName : e.Meta.Title,
            image = e.Meta.Image,
            version = e.Meta.Version ?? plan.PackageVersion,
            author = e.Meta.Author ?? plan.PackageAuthor,
            site = e.Meta.Site,
            url = e.Meta.Key,
            summary = plan.Summary,
            ops = plan.Ops.Select((o, i) => new
            {
                index = i,
                kind = o.Kind.ToString(),
                sourceRel = o.SourceRel,
                target = o.Target,
                reason = o.Reason,
                enabled = o.Enabled,
                active = o.Enabled && o.Kind != OpKind.Skip && plan.IsVariantSelected(o.Variant),
                variant = o.Variant,
            }).ToList(),
            variants = plan.Variants.Select(v => new
            {
                id = v.Id,
                title = v.Title,
                selected = v.Selected,
                options = v.Options.Select(o => new { id = o.Id, label = o.Label, recommended = o.Recommended }),
            }).ToList(),
            // computed per call so the warnings follow the selected variants
            warnings = plan.Warnings.Concat(Conflicts.ForPlan(library, e.Game, plan, e.Meta.Key))
                .Select(w => new { level = w.Level.ToString(), message = w.Message }).ToList(),
            requires = plan.Requires.Where(prereqs.ContainsKey).Select(r => PrereqDto(prereqs[r])).ToList(),
            readmes = plan.Readmes,
            needsScan = plan.Ops.Any(o => o.Kind == OpKind.Skip && o.Reason.StartsWith("Game file meant to replace")),
            activeCount = plan.ActiveOps.Count(),
            skippedCount = plan.Ops.Count(o => o.Kind == OpKind.Skip),
            gameName = e.Game.Name,
        };
    }

    async Task<object> InstallAsync(PlanEntry e)
    {
        var g = e.Game;
        var meta = e.Meta;
        var progress = new Progress<InstallProgress>(p => Emit("install.progress", new { planId = e.Id, stage = p.Stage, fraction = p.Fraction, detail = p.Detail }));

        // A mod from the same page is an update when its version changed or it's the same download again.
        // A second, different file from the same page (LCPDFR multi-file, an optional extra) installs alongside.
        var previous = meta.Key == null ? null
            : library.ForGame(g.Id).FirstOrDefault(m => m.PageUrl != null && Norm(m.PageUrl) == Norm(meta.Key)
                && ((meta.Version != null && m.Version != null && !string.Equals(meta.Version.Trim(), m.Version.Trim(), StringComparison.OrdinalIgnoreCase))
                    || DownloadStem(m.ArchivePath) == DownloadStem(e.Plan.ArchivePath)));

        var installed = await Busy(() => Task.Run(() =>
        {
            new ModInstaller(g, App.Paths, library).Preflight(e.Plan);
            // disable (not remove) the old version first, so it can come back if the new one fails
            bool wasEnabled = previous?.Enabled == true;
            if (previous != null && wasEnabled)
            {
                var disableProblems = service.Disable(g, previous);
                if (disableProblems.Count > 0)
                    throw new InstallBlockedException($"Couldn't take the old version of {previous.Name} out ({disableProblems[0]}). Close whatever is using its files and try again.");
            }
            InstalledMod result;
            try
            {
                result = service.Install(g, e.Plan, new InstalledMod
                {
                    Name = meta.Key == null && e.Plan.PackageName != null ? e.Plan.PackageName : meta.Title,
                    Source = meta.Site is "gta5mods" or "lcpdfr" ? meta.Site : "local",
                    PageUrl = meta.Key,
                    Version = meta.Version ?? e.Plan.PackageVersion,
                    Author = meta.Author ?? e.Plan.PackageAuthor,
                    ImageUrl = meta.Image,
                }, progress);
            }
            catch
            {
                if (previous != null && wasEnabled)
                {
                    try { service.Enable(g, previous, IndexFor(g)); } catch (Exception ex) { App.Log("update", "couldn't restore the old version: " + ex.Message); }
                }
                throw;
            }
            if (previous != null) service.Uninstall(g, library.Find(previous.Id) ?? previous); // drops the old record and its kept download
            return result;
        }));
        foreach (var note in service.LastNotes.Take(2)) Toast("info", note);

        plans.Remove(e.Id);
        if (!settings.KeepDownloads && installed.ArchivePath != null)
        {
            try { File.Delete(installed.ArchivePath); installed.ArchivePath = null; library.Upsert(installed); } catch { }
        }
        Emit("library.changed", new { gameId = g.Id });
        Emit("game.changed", Status(g));
        // the plan dialog shows its own success screen, so no toast here
        _ = RefreshSpawnNamesAsync(g);
        return ModDto(installed);
    }

    /// <summary>"mymod" for "20261022-183000-MyMod_v1.2.zip": no launcher timestamp, extension or version numbers.</summary>
    static string DownloadStem(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        name = System.Text.RegularExpressions.Regex.Replace(name, @"^\d{8}-\d{6}-", "");
        name = System.Text.RegularExpressions.Regex.Replace(name, @"[\s_.\-]*v?\d+(\.\d+)*[a-z]?\b", "");
        return System.Text.RegularExpressions.Regex.Replace(name, "[^a-z]", "");
    }

    // ---------------------------------------------------------------- library

    void ReportUndo(string name, List<string> problems, string verb)
    {
        if (problems.Count > 0)
        {
            App.Log("undo", $"{name}: {string.Join(" | ", problems)}");
            Toast("warn", $"{name}: {problems.Count} change(s) couldn't be undone (a file in use?). It stays in Library: close whatever uses it and press Retry.");
            return;
        }
        Toast("ok", $"{name} {verb}.");
        foreach (var note in service.LastNotes.Take(3))
        {
            App.Log("undo", note);
            Toast("info", note);
        }
    }

    InstalledMod ModOf(JsonElement args) =>
        library.Find(Str(args, "id")) ?? throw new BridgeException("ERROR", "That mod isn't in the library any more.");

    object ModDto(InstalledMod m) => new
    {
        id = m.Id,
        name = m.Name,
        source = m.Source,
        pageUrl = m.PageUrl,
        version = m.Version,
        author = m.Author,
        imageUrl = m.ImageUrl,
        summary = m.Summary,
        installedAt = m.InstalledAt.ToString("o"),
        enabled = m.Enabled,
        changeCount = m.Entries.Count,
        canReEnable = m.ArchivePath != null && File.Exists(m.ArchivePath),
        needsCleanup = m.NeedsCleanup,
        spawnNames = m.SpawnNames ?? new List<string>(),
        kind = m.Kind ?? SpawnNames.GuessKind(m), // until the background pass reads its packs
        conflicts = Conflicts.For(library, m).Select(c => new { modId = c.ModId, modName = c.ModName, paths = c.Paths }),
    };

    static string ReadText(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".pdf" or ".doc" or ".docx" or ".rtf" or ".odt")
            return $"({Path.GetFileName(path)} can't be shown here. It's in the mod's download.)";
        var bytes = File.ReadAllBytes(path);
        var len = Math.Min(bytes.Length, 200_000);
        return System.Text.Encoding.UTF8.GetString(bytes, 0, len).Replace("\0", "");
    }

    public void Dispose()
    {
        foreach (var s in sites.Values) s.Dispose();
        foreach (var p in plans.Values) service.DeleteStaging(p.Plan);
    }
}
