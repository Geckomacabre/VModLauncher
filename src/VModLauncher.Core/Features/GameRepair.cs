using VModLauncher.Core.Game;
using VModLauncher.Core.Install;
using VModLauncher.Core.Library;

namespace VModLauncher.Core.Features;

public sealed record RepairResult(bool Rebuilt, int RestoredItems, int RestoredEntries, string? Backup, List<string> Problems);

/// <summary>
/// After a Rockstar update the game's update\update.rpf changes, but the copy in mods\ is still the
/// old one, so the game loads stale data and usually crashes. This rebuilds the copy from the new
/// game file and carries over what mods added: dlclist packs (also ones OpenIV installed) and every
/// archive change the launcher's own mods made. The old copy is kept next to it as a backup.
/// </summary>
public static class GameRepair
{
    /// <summary>Data files mods often edit; differences we can't attribute are reported, not copied.</summary>
    static readonly string[] WatchedData =
    {
        "common/data/gameconfig.xml", "common/data/handling.meta", "common/data/carcols.meta",
        "common/data/carvariations.meta", "common/data/levels/gta5/vehicles.meta", "common/data/visualsettings.dat",
        "common/data/ai/weapons.meta", "common/data/popgroups.ymt", "common/data/timecycle/timecycle_mods_1.xml",
    };

    public static RepairResult Run(GameInstall game, LauncherPaths paths, ModLibrary library, Action<string, double?>? progress = null)
    {
        var modsPath = game.ModsUpdateRpf;
        var problems = new List<string>();
        if (!File.Exists(modsPath))
            return new RepairResult(false, 0, 0, null, new List<string> { "There's no mods copy of update.rpf yet, so there's nothing to rebuild." });
        if (!File.Exists(game.GameUpdateRpf))
            return new RepairResult(false, 0, 0, null, new List<string> { "The game has no update\\update.rpf (is the install complete?)." });
        if (GameLauncher.IsGameRunning(game))
            throw new InstallBlockedException($"{game.Name} is running. Close the game first.");

        // one edition's keys at a time across the whole launcher; also loads this game's keys
        using var session = RpfService.Session(game);
        var rpf = new RpfService(game);

        var backupPath = $"{modsPath}.vml-old-{DateTime.Now:yyyyMMdd-HHmm}";
        if (File.Exists(backupPath)) backupPath += DateTime.Now.ToString("ss");
        rpf.Forget(modsPath);
        File.Move(modsPath, backupPath);

        int restoredItems = 0, restoredEntries = 0;
        try
        {
            progress?.Invoke("Copying the game's new update.rpf into mods", 0);
            rpf.EnsureModsCopy(@"update\update.rpf", out _, new InlineProgress<double>(f => progress?.Invoke("Copying the game's new update.rpf into mods", f * 0.7)));

            // 1. dlclist: every pack the old copy listed that the new one doesn't (ours, OpenIV's, anyone's)
            progress?.Invoke("Putting your add-on packs back on the list", 0.75);
            var oldList = rpf.TryRead(backupPath, DlcList.InnerPath);
            var newList = rpf.TryRead(modsPath, DlcList.InnerPath);
            if (oldList != null && newList != null)
            {
                var oldItems = DlcList.Items(DlcList.Decode(oldList, out _));
                var xml = DlcList.Decode(newList, out var bom);
                foreach (var item in oldItems.Where(i => !DlcList.Contains(xml, i)))
                {
                    xml = DlcList.Add(xml, item);
                    restoredItems++;
                }
                if (restoredItems > 0) rpf.Write(modsPath, DlcList.InnerPath, DlcList.Encode(xml, bom));
            }

            // 2. the launcher's mods: copy each changed entry's current content across, layer by layer
            progress?.Invoke("Re-applying your mods' changes", 0.85);
            var rel = game.ToRelative(modsPath);
            List<IGrouping<string, (InstalledMod Mod, JournalEntry Entry)>> touched;
            lock (library.Gate)
                touched = library.ForGame(game.Id).Where(m => m.Enabled)
                    .SelectMany(m => m.Entries
                        .Where(e => e.Type == JournalEntry.RpfEntry && e.Inner != null && string.Equals(e.Path, rel, StringComparison.OrdinalIgnoreCase))
                        .Select(e => (Mod: m, Entry: e)))
                    .GroupBy(t => t.Entry.Inner!.Replace('\\', '/').Trim('/'), StringComparer.OrdinalIgnoreCase)
                    .ToList();

            var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in touched)
            {
                var inner = group.Key;
                owned.Add(inner);
                var layers = group.OrderBy(t => t.Mod.InstalledAt).ToList();
                var top = layers[^1];
                try
                {
                    var freshOriginal = rpf.TryRead(modsPath, inner);
                    if (top.Entry.Hash == null)
                    {
                        // the top mod deleted this file
                        if (freshOriginal != null) rpf.Delete(modsPath, inner);
                    }
                    else
                    {
                        var current = rpf.TryRead(backupPath, inner);
                        if (current == null) { problems.Add($"{top.Mod.Name}: {inner} wasn't in the old copy any more, so it couldn't be carried over."); continue; }
                        rpf.Write(modsPath, inner, current);
                    }
                    // the bottom layer's "before" is now the new game's original, not the old one
                    ReplaceBackup(paths, layers[0].Mod, layers[0].Entry, freshOriginal);
                    restoredEntries += layers.Count;
                }
                catch (Exception ex)
                {
                    problems.Add($"{top.Mod.Name}: {inner}: {ex.Message}");
                }
            }
            library.Save();

            // 3. data files that differ but no launcher mod owns: say so instead of guessing
            foreach (var path in WatchedData.Where(p => !owned.Contains(p)))
            {
                try
                {
                    var before = rpf.TryRead(backupPath, path);
                    var after = rpf.TryRead(modsPath, path);
                    if (before != null && after != null && RpfService.ContentHash(before) != RpfService.ContentHash(after))
                        problems.Add($"{Path.GetFileName(path)} in your old copy differs from the new game's (changed by a mod installed outside the launcher, or by the update itself). It wasn't carried over; reinstall that mod if you need it.");
                }
                catch { }
            }

            var state = GameState.Load(paths, game);
            var gi = new FileInfo(game.GameUpdateRpf);
            state.UpdateRpfLength = gi.Length;
            state.UpdateRpfWrittenUtc = gi.LastWriteTimeUtc;
            state.RepairedAtUtc = DateTime.UtcNow;
            state.Save(paths, game);
            progress?.Invoke("Done", 1);
        }
        catch
        {
            // put the old copy back exactly as it was
            rpf.Forget(modsPath);
            try { if (File.Exists(modsPath)) File.Delete(modsPath); } catch { }
            File.Move(backupPath, modsPath);
            throw;
        }

        problems.Insert(0, $"Your previous copy is kept as {Path.GetFileName(backupPath)} in mods\\update (delete it once the game runs fine).");
        return new RepairResult(true, restoredItems, restoredEntries, game.ToRelative(backupPath), problems);
    }

    static void ReplaceBackup(LauncherPaths paths, InstalledMod mod, JournalEntry entry, byte[]? freshOriginal)
    {
        var dir = Path.Combine(paths.Backups, mod.Id);
        Directory.CreateDirectory(dir);
        if (entry.Backup != null)
        {
            try { File.Delete(Path.Combine(dir, entry.Backup)); } catch { }
        }
        if (freshOriginal == null)
        {
            entry.Backup = null;
            return;
        }
        var name = $"repair-{Guid.NewGuid():N}.bak";
        File.WriteAllBytes(Path.Combine(dir, name), freshOriginal);
        entry.Backup = name;
    }
}
