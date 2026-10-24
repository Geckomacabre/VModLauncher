using System.Diagnostics;
using System.Text.Json;
using VModLauncher.Core.Game;
using VModLauncher.Core.Install;
using VModLauncher.Core.Tools;

namespace VModLauncher.Host;

/// <summary>Pack manager and game file checker (packs.*, gamefiles.*) — see docs/bridge.md, "Wave 3".</summary>
public sealed partial class Bridge
{
    /// <summary>Edited archives the last gamefiles.check showed: the page can only ask to copy those.</summary>
    readonly HashSet<string> packsReportedEdited = new(StringComparer.OrdinalIgnoreCase);

    private partial async Task<(bool Handled, object? Result)> HandlePacksAsync(string cmd, JsonElement args)
    {
        switch (cmd)
        {
            case "packs.list":
                return (true, await PacksListAsync(Game));

            case "packs.setEnabled":
            {
                var item = Str(args, "item");
                var enabled = Bool(args, "enabled", true);
                return (true, await PacksChangeAsync(g => PackManager.SetEnabled(g, App.Paths, library, item, enabled)));
            }

            case "packs.register":
            {
                var name = Str(args, "name");
                return (true, await PacksChangeAsync(g => PackManager.Register(g, App.Paths, library, name)));
            }

            case "packs.fix":
            {
                var fix = Str(args, "fix");
                return (true, await PacksChangeAsync(g => PackManager.Fix(g, App.Paths, library, fix)));
            }

            case "gamefiles.check":
                return (true, await GameFilesCheckAsync(Game));

            case "gamefiles.moveToMods":
            {
                var g = Game;
                var path = Str(args, "path");
                if (!packsReportedEdited.Contains(path)) throw new BridgeException("ERROR", "Check the game files again first.");
                bool copied;
                try { copied = await Busy(() => Task.Run(() => GameFileChecker.MoveToMods(g, App.Paths, library, path))); }
                catch (ArgumentException ex) { throw new BridgeException("ERROR", ex.Message); }
                catch (InvalidOperationException ex) { throw new BridgeException("BLOCKED", ex.Message); }
                Emit("library.changed", new { gameId = g.Id });
                Toast(copied ? "ok" : "info", copied
                    ? $"{path} is copied into the mods folder. Now restore the originals through your store."
                    : $"{path} already has a copy in the mods folder.");
                return (true, await GameFilesCheckAsync(g));
            }

            case "gamefiles.verify":
            {
                var g = Game;
                var (how, url) = GameFileChecker.Verify(g);
                // only the store's own file-check link is ever opened, never something the page names
                if (Bool(args, "open") && url != null && url.StartsWith("steam://validate/", StringComparison.Ordinal))
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return (true, new { how, url });
            }
        }
        return (false, null);
    }

    Task<PackManagerReport> PacksListAsync(GameInstall g) =>
        Task.Run(() =>
        {
            using (RpfService.Session(g)) return PackManager.List(g, library);
        });

    async Task<PackManagerReport> PacksChangeAsync(Action<GameInstall> change)
    {
        var g = Game;
        try { await Busy(() => Task.Run(() => { change(g); return true; })); }
        catch (ArgumentException ex) { throw new BridgeException("ERROR", ex.Message); }
        catch (InvalidOperationException ex) { throw new BridgeException("BLOCKED", ex.Message); }
        Emit("library.changed", new { gameId = g.Id });
        Emit("game.changed", Status(g));
        return await PacksListAsync(g);
    }

    async Task<GameFileReport> GameFilesCheckAsync(GameInstall g)
    {
        var report = await Task.Run(() => GameFileChecker.Check(g));
        packsReportedEdited.Clear();
        foreach (var a in report.Archives.Where(a => a.State == "edited")) packsReportedEdited.Add(a.Path);
        return report;
    }
}
