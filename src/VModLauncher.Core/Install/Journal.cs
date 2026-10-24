using System.Security.Cryptography;
using VModLauncher.Core.Game;
using VModLauncher.Core.Library;

namespace VModLauncher.Core.Install;

/// <summary>
/// Every change an install makes goes through here: it backs up what was there, records the
/// change on the mod, and can undo all of it (rollback on failure, or uninstall later).
/// Archive edits always land in mods\ copies, never in the game's own archives.
/// </summary>
public sealed class Journal
{
    readonly GameInstall game;
    readonly RpfService rpf;
    readonly InstalledMod mod;
    readonly ModLibrary? library;
    readonly string backupDir;
    readonly Action? onChange;
    int backupCounter;

    public GameInstall Game => game;
    public RpfService Rpf => rpf;
    public InstalledMod Mod => mod;
    public IProgress<string>? Log { get; set; }

    public Journal(GameInstall game, RpfService rpf, InstalledMod mod, string backupsRoot, ModLibrary? library = null, Action? onChange = null)
    {
        this.game = game;
        this.rpf = rpf;
        this.mod = mod;
        this.library = library;
        this.onChange = onChange;
        backupDir = Path.Combine(backupsRoot, mod.Id);
        Directory.CreateDirectory(backupDir);
        backupCounter = Directory.EnumerateFiles(backupDir).Count();
    }

    // ---------------------------------------------------------------- helpers

    public static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    public static string Sha256File(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s));
    }

    public static string NormalizeRel(string rel) => rel.Replace('/', '\\').Trim().TrimStart('\\');

    string NewBackupPath() => Path.Combine(backupDir, $"{Interlocked.Increment(ref backupCounter):D5}.bak");

    string SaveBackup(byte[] data)
    {
        var p = NewBackupPath();
        File.WriteAllBytes(p, data);
        return Path.GetFileName(p);
    }

    string SaveBackupOf(string file)
    {
        var p = NewBackupPath();
        File.Copy(file, p, overwrite: true);
        return Path.GetFileName(p);
    }

    string BackupFullPath(string name) => Path.Combine(backupDir, name);

    /// <summary>Files moved aside during undo because they changed after install (shown to the player).</summary>
    public List<string> Notes { get; } = new();

    object Gate => library?.Gate ?? mod;

    void Record(JournalEntry e)
    {
        lock (Gate) mod.Entries.Add(e);
        onChange?.Invoke();
    }

    string GameFile(string rel)
    {
        var full = game.Resolve(NormalizeRel(rel));
        if (!full.StartsWith(game.Root + "\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to write outside the game folder: " + rel);
        return full;
    }

    void EnsureDirFor(string fullFile)
    {
        var dir = Path.GetDirectoryName(fullFile)!;
        var missing = new Stack<string>();
        for (var d = dir; !Directory.Exists(d) && d.Length > game.Root.Length; d = Path.GetDirectoryName(d)!)
            missing.Push(d);
        while (missing.Count > 0)
        {
            var d = missing.Pop();
            Directory.CreateDirectory(d);
            Record(new JournalEntry { Type = JournalEntry.Dir, Path = game.ToRelative(d) });
        }
    }

    // ---------------------------------------------------------------- loose files

    public void CopyFile(string sourcePath, string gameRel)
    {
        var dest = GameFile(gameRel);
        EnsureDirFor(dest);
        string? backup = File.Exists(dest) ? SaveBackupOf(dest) : null;
        File.Copy(sourcePath, dest, overwrite: true);
        Record(new JournalEntry { Type = JournalEntry.File, Path = game.ToRelative(dest), Backup = backup, Hash = Sha256File(dest) });
        Log?.Report($"{game.ToRelative(dest)}");
    }

    public void WriteFile(string gameRel, byte[] data)
    {
        var dest = GameFile(gameRel);
        EnsureDirFor(dest);
        string? backup = File.Exists(dest) ? SaveBackupOf(dest) : null;
        File.WriteAllBytes(dest, data);
        Record(new JournalEntry { Type = JournalEntry.File, Path = game.ToRelative(dest), Backup = backup, Hash = Sha256(data) });
        Log?.Report($"{game.ToRelative(dest)}");
    }

    public byte[]? ReadGameFile(string gameRel)
    {
        var p = GameFile(gameRel);
        return File.Exists(p) ? File.ReadAllBytes(p) : null;
    }

    public void DeleteFile(string gameRel)
    {
        var dest = GameFile(gameRel);
        if (!File.Exists(dest)) return;
        var backup = SaveBackupOf(dest);
        File.Delete(dest);
        Record(new JournalEntry { Type = JournalEntry.File, Path = game.ToRelative(dest), Backup = backup, Hash = null });
        Log?.Report($"deleted {game.ToRelative(dest)}");
    }

    /// <summary>Copies to a path outside the game (save games). Backs up what it replaces.</summary>
    public void CopyExternal(string sourcePath, string absoluteTarget)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(absoluteTarget)!);
        string? backup = File.Exists(absoluteTarget) ? SaveBackupOf(absoluteTarget) : null;
        File.Copy(sourcePath, absoluteTarget, overwrite: true);
        Record(new JournalEntry { Type = JournalEntry.External, Path = absoluteTarget, Backup = backup, Hash = Sha256File(absoluteTarget) });
        Log?.Report(absoluteTarget);
    }

    // ---------------------------------------------------------------- archives

    /// <summary>Game-relative path of the mods\ copy for an archive path given by a mod ("update\update.rpf").</summary>
    public static string ModsRel(string archiveRel)
    {
        archiveRel = NormalizeRel(archiveRel);
        return archiveRel.StartsWith("mods\\", StringComparison.OrdinalIgnoreCase) ? archiveRel : "mods\\" + archiveRel;
    }

    /// <summary>Physical path of the editable mods\ copy, copying the game's original first if needed.</summary>
    public string ModsArchive(string archiveRel, IProgress<double>? progress = null)
    {
        var rel = NormalizeRel(archiveRel);
        if (rel.StartsWith("mods\\", StringComparison.OrdinalIgnoreCase)) rel = rel.Substring(5);
        var path = rpf.EnsureModsCopy(rel, out var created, progress);
        if (created)
        {
            Record(new JournalEntry { Type = JournalEntry.RpfCopy, Path = game.ToRelative(path) });
            Log?.Report($"copied {rel} into mods");
        }
        return path;
    }

    /// <summary>Creates a brand new (OPEN) archive in mods\, e.g. an OIV that adds a dlc.rpf it builds itself.</summary>
    public void CreateEmptyArchive(string archiveRel)
    {
        var rel = ModsRel(archiveRel);
        var full = GameFile(rel);
        if (File.Exists(full)) return;
        EnsureDirFor(full);
        CodeWalker.GameFiles.RpfFile.CreateNew(Path.GetDirectoryName(full)!, Path.GetFileName(full), CodeWalker.GameFiles.RpfEncryption.OPEN);
        Record(new JournalEntry { Type = JournalEntry.RpfNew, Path = rel });
    }

    /// <summary>Reads from the mods\ copy if there is one, otherwise from the game's original.</summary>
    public byte[]? ReadRpf(string archiveRel, string inner)
    {
        var modsPath = game.Resolve(ModsRel(archiveRel));
        var rel = NormalizeRel(archiveRel);
        if (rel.StartsWith("mods\\", StringComparison.OrdinalIgnoreCase)) rel = rel.Substring(5);
        var path = File.Exists(modsPath) ? modsPath : game.Resolve(rel);
        return File.Exists(path) ? rpf.TryRead(path, inner) : null;
    }

    public void WriteRpf(string archiveRel, string inner, byte[] data, IProgress<double>? copyProgress = null)
    {
        var path = ModsArchive(archiveRel, copyProgress);
        inner = inner.Replace('\\', '/').Trim('/');
        var previous = rpf.Write(path, inner, data);
        Record(new JournalEntry
        {
            Type = JournalEntry.RpfEntry,
            Path = game.ToRelative(path),
            Inner = inner,
            Backup = previous == null ? null : SaveBackup(previous),
            Hash = RpfService.ContentHash(data),
        });
        Log?.Report($"{game.ToRelative(path)} > {inner}");
    }

    public void DeleteRpf(string archiveRel, string inner)
    {
        var path = ModsArchive(archiveRel);
        inner = inner.Replace('\\', '/').Trim('/');
        var previous = rpf.Delete(path, inner);
        if (previous == null) return;
        Record(new JournalEntry { Type = JournalEntry.RpfEntry, Path = game.ToRelative(path), Inner = inner, Backup = SaveBackup(previous), Hash = null });
        Log?.Report($"deleted {game.ToRelative(path)} > {inner}");
    }

    // ---------------------------------------------------------------- dlclist

    /// <summary>
    /// Registers a pack. Always journaled, even when the item is already listed (Added = false), so the
    /// item stays as long as any installed mod still needs it.
    /// </summary>
    public void AddDlcItem(DlcListTarget target, string item)
    {
        bool added;
        // an item this record took off the list goes back where it was (the pack manager switching it on again)
        var after = WhereItWas(target, item);
        if (target.Kind == DlcListTarget.Onigiri)
        {
            var bytes = File.ReadAllBytes(target.Path);
            var xml = DlcList.Decode(bytes, out var bom);
            added = !DlcList.Contains(xml, item);
            if (added) File.WriteAllBytes(target.Path, DlcList.Encode(DlcList.Add(xml, item, after), bom));
        }
        else
        {
            var path = ModsArchive(@"update\update.rpf");
            var bytes = rpf.TryRead(path, DlcList.InnerPath) ?? throw new InvalidDataException("update.rpf has no common/data/dlclist.xml");
            var xml = DlcList.Decode(bytes, out var bom);
            added = !DlcList.Contains(xml, item);
            if (added) rpf.Write(path, DlcList.InnerPath, DlcList.Encode(DlcList.Add(xml, item, after), bom));
        }
        Record(new JournalEntry { Type = JournalEntry.DlcItem, Path = game.ToRelative(target.Path), Item = item, Added = added });
        if (added) Log?.Report($"dlclist + {item}");
    }

    /// <summary>Removes a pack from a dlclist (an OIV's own removal step), journaled so uninstall puts it back.</summary>
    public void RemoveDlcItem(DlcListTarget target, string item)
    {
        string? after;
        if (target.Kind == DlcListTarget.Onigiri)
        {
            var xml = DlcList.Decode(File.ReadAllBytes(target.Path), out var bom);
            if (!DlcList.Contains(xml, item)) return;
            after = DlcList.ItemBefore(xml, item);
            File.WriteAllBytes(target.Path, DlcList.Encode(DlcList.Remove(xml, item), bom));
        }
        else
        {
            var path = ModsArchive(@"update\update.rpf");
            var bytes = rpf.TryRead(path, DlcList.InnerPath);
            if (bytes == null) return;
            var xml = DlcList.Decode(bytes, out var bom);
            if (!DlcList.Contains(xml, item)) return;
            after = DlcList.ItemBefore(xml, item);
            rpf.Write(path, DlcList.InnerPath, DlcList.Encode(DlcList.Remove(xml, item), bom));
        }
        Record(new JournalEntry { Type = JournalEntry.DlcRemoved, Path = game.ToRelative(target.Path), Item = item, After = after });
        Log?.Report($"dlclist - {item}");
    }

    // ---------------------------------------------------------------- undo

    /// <summary>
    /// Undoes every recorded change, newest first (rollback and uninstall). Changes that couldn't be
    /// undone stay on the mod together with their backups, so the player can retry; the returned
    /// list says what failed.
    /// </summary>
    public List<string> UndoAll()
    {
        var problems = new List<string>();
        var failed = new List<JournalEntry>();
        List<JournalEntry> entries;
        lock (Gate) entries = mod.Entries.ToList();
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            var e = entries[i];
            try { Undo(e); }
            catch (Exception ex)
            {
                problems.Add($"{e}: {ex.Message}");
                failed.Insert(0, e);
            }
        }
        lock (Gate)
        {
            mod.Entries.Clear();
            mod.Entries.AddRange(failed);
        }
        onChange?.Invoke();
        if (failed.Count == 0)
        {
            try { Directory.Delete(backupDir, recursive: true); } catch { }
        }
        return problems;
    }

    string KeptDir()
    {
        var dir = Path.Combine(Path.GetDirectoryName(backupDir)!, "_kept", $"{mod.Id}-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Keeps a copy of a file the undo won't put back, and tells the player where it is.</summary>
    void Keep(string sourceFile, string displayName, string why)
    {
        var dest = Path.Combine(KeptDir(), Path.GetFileName(displayName));
        File.Copy(sourceFile, dest, overwrite: true);
        Notes.Add($"{displayName}: {why} Kept a copy at {dest}");
    }

    void Undo(JournalEntry e)
    {
        switch (e.Type)
        {
            case JournalEntry.File:
            case JournalEntry.External:
                UndoFile(e);
                break;
            case JournalEntry.Dir:
                var dir = game.Resolve(e.Path);
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
                break;
            case JournalEntry.DlcItem:
                UndoDlcItem(e);
                break;
            case JournalEntry.DlcRemoved:
            {
                var target = e.Path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                    ? new DlcListTarget(DlcListTarget.Onigiri, game.Resolve(e.Path))
                    : new DlcListTarget(DlcListTarget.ModsUpdateRpf, game.Resolve(e.Path));
                ReAddDlcItem(target, e.Item!, e.After);
                break;
            }
            case JournalEntry.RpfEntry:
                UndoRpfEntry(e);
                break;
            case JournalEntry.RpfCopy:
                // Kept on purpose: other tools (OpenIV, OIV installers) may have edited the copy since.
                break;
            case JournalEntry.RpfNew:
                var created = game.Resolve(e.Path);
                if (library != null && library.AnyOtherUses(mod, o => string.Equals(o.Path, e.Path, StringComparison.OrdinalIgnoreCase))) break;
                if (File.Exists(created)) File.Delete(created);
                break;
        }
    }

    void UndoFile(JournalEntry e)
    {
        var full = e.Type == JournalEntry.External ? e.Path : game.Resolve(e.Path);
        // A mod installed later wrote the same file (even byte-identical, e.g. a shared LemonUI.dll):
        // it keeps it, and inherits our "before" state for its own uninstall.
        if (HandOver(e, o => o.Type == e.Type && string.Equals(o.Path, e.Path, StringComparison.OrdinalIgnoreCase)))
            return;

        bool ours = e.Hash == null ? !File.Exists(full) : File.Exists(full) && Sha256File(full) == e.Hash;
        if (!ours)
        {
            // Changed after install by the player, the game or the mod itself (a save, an edited .ini).
            if (e.Hash == null)
            {
                // we had deleted it and something put a file back: leave it
                return;
            }
            if (e.Backup != null)
            {
                // leave their current file; keep what was there before the mod
                Keep(BackupFullPath(e.Backup), e.Path, "changed after the mod was installed, so it was left as it is.");
            }
            else if (File.Exists(full))
            {
                // an archive the mod brought (a dlc.rpf) changes when tools edit inside it (texture
                // optimizer, pack fixes): it simply goes with the mod
                if (full.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(full);
                    return;
                }
                // the mod's own file, edited since (settings): it goes, but a copy is kept
                Keep(full, e.Path, "the mod's file had your changes in it.");
                File.Delete(full);
            }
            return;
        }
        if (e.Backup != null)
        {
            File.Copy(BackupFullPath(e.Backup), full, overwrite: true);
        }
        else if (File.Exists(full))
        {
            File.Delete(full);
        }
    }

    void UndoRpfEntry(JournalEntry e)
    {
        var archive = game.Resolve(e.Path);
        if (!File.Exists(archive)) return;
        if (HandOver(e, o => o.Type == JournalEntry.RpfEntry && string.Equals(o.Path, e.Path, StringComparison.OrdinalIgnoreCase)
                             && string.Equals(o.Inner, e.Inner, StringComparison.OrdinalIgnoreCase)))
            return;
        var current = rpf.TryRead(archive, e.Inner!);
        bool ours = e.Hash == null ? current == null : current != null && (RpfService.ContentHash(current) == e.Hash || Sha256(current) == e.Hash);
        if (!ours)
        {
            if (e.Backup != null) Keep(BackupFullPath(e.Backup), e.Inner!, $"inside {e.Path} was changed by another tool after the mod was installed, so it was left as it is.");
            return;
        }
        if (e.Backup != null) rpf.Write(archive, e.Inner!, File.ReadAllBytes(BackupFullPath(e.Backup)));
        else rpf.Delete(archive, e.Inner!);
    }

    /// <summary>
    /// A pack stays listed while any other enabled mod still has it in its journal. If we were the one
    /// who added it, the next user becomes responsible for removing it.
    /// </summary>
    void UndoDlcItem(JournalEntry e)
    {
        bool addedByUs = e.Added != false;
        if (library != null)
        {
            (InstalledMod Mod, JournalEntry Entry)? other;
            lock (Gate)
            {
                other = library.Mods
                    .Where(m => m.Id != mod.Id && m.GameId == mod.GameId && m.Enabled)
                    .SelectMany(m => m.Entries.Where(o => o.Type == JournalEntry.DlcItem && string.Equals(o.Item, e.Item, StringComparison.OrdinalIgnoreCase)
                                                          && string.Equals(o.Path, e.Path, StringComparison.OrdinalIgnoreCase)).Select(o => (m, o)))
                    .Select(t => ((InstalledMod, JournalEntry)?)t).FirstOrDefault();
            }
            if (other != null)
            {
                if (addedByUs) { other.Value.Entry.Added = true; library.Save(); }
                return;
            }
        }
        if (addedByUs) RemoveDlcItemEntry(e);
    }

    /// <summary>The item this record's newest removal of <paramref name="item"/> sat after, if it took it off this list.</summary>
    string? WhereItWas(DlcListTarget target, string item)
    {
        var rel = game.ToRelative(target.Path);
        lock (Gate)
            return mod.Entries.LastOrDefault(e => e.Type == JournalEntry.DlcRemoved
                && string.Equals(e.Item, item, StringComparison.OrdinalIgnoreCase)
                && string.Equals(e.Path, rel, StringComparison.OrdinalIgnoreCase))?.After;
    }

    void ReAddDlcItem(DlcListTarget target, string item, string? after)
    {
        // a pack whose files were deleted since isn't put back on the list (it would only show as missing)
        var m = System.Text.RegularExpressions.Regex.Match(item, @"^dlcpacks:/([^/]+)/?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var name = m.Groups[1].Value;
            bool present = new[] { game.OnigiriDlcPacksDir, game.ModsDlcPacksDir, Path.Combine(game.Root, "update", "x64", "dlcpacks") }
                .Any(dir => File.Exists(Path.Combine(dir, name, "dlc.rpf")));
            if (!present)
            {
                Notes.Add($"{item} wasn't put back on the dlclist: its files aren't there any more.");
                return;
            }
        }
        if (target.Kind == DlcListTarget.Onigiri)
        {
            if (!File.Exists(target.Path)) return;
            var xml = DlcList.Decode(File.ReadAllBytes(target.Path), out var bom);
            if (!DlcList.Contains(xml, item)) File.WriteAllBytes(target.Path, DlcList.Encode(DlcList.Add(xml, item, after), bom));
        }
        else
        {
            if (!File.Exists(target.Path)) return;
            var bytes = rpf.TryRead(target.Path, DlcList.InnerPath);
            if (bytes == null) return;
            var xml = DlcList.Decode(bytes, out var bom);
            if (!DlcList.Contains(xml, item)) rpf.Write(target.Path, DlcList.InnerPath, DlcList.Encode(DlcList.Add(xml, item, after), bom));
        }
    }

    /// <summary>Gives our "before" state to the newest later mod that wrote the same thing. True if there was one.</summary>
    bool HandOver(JournalEntry e, Func<JournalEntry, bool> match)
    {
        if (library == null) return false;
        var later = library.LaterOwner(mod, match);
        if (later == null) return false;
        var (laterMod, laterEntry) = later.Value;
        var theirDir = Path.Combine(Path.GetDirectoryName(backupDir)!, laterMod.Id);
        Directory.CreateDirectory(theirDir);
        if (laterEntry.Backup != null) TryDelete(Path.Combine(theirDir, laterEntry.Backup));
        if (e.Backup != null)
        {
            var name = "from-" + mod.Id + "-" + e.Backup;
            File.Copy(BackupFullPath(e.Backup), Path.Combine(theirDir, name), overwrite: true);
            laterEntry.Backup = name;
        }
        else
        {
            laterEntry.Backup = null;
        }
        library.Save();
        return true;
    }

    void RemoveDlcItemEntry(JournalEntry e)
    {
        var listPath = game.Resolve(e.Path);
        if (!File.Exists(listPath)) return;
        if (listPath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            var xml = DlcList.Decode(File.ReadAllBytes(listPath), out var bom);
            File.WriteAllBytes(listPath, DlcList.Encode(DlcList.Remove(xml, e.Item!), bom));
        }
        else
        {
            var bytes = rpf.TryRead(listPath, DlcList.InnerPath);
            if (bytes == null) return;
            var xml = DlcList.Decode(bytes, out var bom);
            if (!DlcList.Contains(xml, e.Item!)) return;
            rpf.Write(listPath, DlcList.InnerPath, DlcList.Encode(DlcList.Remove(xml, e.Item!), bom));
        }
    }

    static void TryDelete(string p) { try { File.Delete(p); } catch { } }
}
