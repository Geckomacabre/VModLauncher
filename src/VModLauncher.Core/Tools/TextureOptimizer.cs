using System.Text;
using System.Text.Json;
using CodeWalker.GameFiles;
using VModLauncher.Core.Game;
using VModLauncher.Core.Install;
using VModLauncher.Core.Library;

namespace VModLauncher.Core.Tools;

public sealed record TexWorst(string Ytd, string Texture, int Width, int Height, string Format);
public sealed record TexOwner(string ModId, string Name);
public sealed record TexAlready(string ModId, int MaxSize);

public sealed class TexPack
{
    /// <summary>Game-relative path of the pack's dlc.rpf.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public TexOwner? Owner { get; set; }
    public int Textures { get; set; }
    public int Oversized { get; set; }
    /// <summary>Oversized textures that can't be shrunk, and why (each reason once).</summary>
    public int Kept { get; set; }
    public List<string> KeptReasons { get; set; } = new();
    public long Bytes { get; set; }
    public long SavableBytes { get; set; }
    public TexAlready? AlreadyOptimized { get; set; }
    public string? Skipped { get; set; }
    public List<TexWorst> Worst { get; set; } = new();
}

public sealed record TexScanResult(int MaxSize, List<TexPack> Packs, long TotalBytes, long SavableBytes);
public sealed record TexOptimizedPack(string Id, string ModId, long SavedBytes, int Textures);
public sealed record TexOptimizeResult(List<TexOptimizedPack> Optimized, List<string> Problems);

/// <summary>
/// Shrinks oversized textures in add-on packs (4K/8K car liveries are the usual cause of texture loss)
/// by dropping their top mip levels. Only the player's add-on packs are touched, never Rockstar's,
/// and every change is a "tool" record in the library, so Uninstall puts the original textures back.
/// </summary>
public static class TextureOptimizer
{
    public const string RecordPrefix = "Smaller textures: ";
    public static readonly int[] Sizes = { 512, 1024, 2048, 4096 };

    // ---------------------------------------------------------------- which packs

    /// <summary>Active add-on packs: (pack name, game-relative dlc.rpf). Rockstar's own packs are left out.</summary>
    public static List<(string Name, string Rel)> ActivePacks(GameInstall game, RpfService rpf)
    {
        string? xml = null;
        if (game.HasOnigiri)
        {
            xml = DlcList.Decode(File.ReadAllBytes(game.OnigiriDlcList), out _);
        }
        else if (File.Exists(game.ModsUpdateRpf))
        {
            var bytes = rpf.TryRead(game.ModsUpdateRpf, DlcList.InnerPath);
            if (bytes != null) xml = DlcList.Decode(bytes, out _);
        }
        var result = new List<(string, string)>();
        if (xml == null) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in DlcList.Items(xml))
        {
            if (!item.StartsWith("dlcpacks:/", StringComparison.OrdinalIgnoreCase)) continue;
            var name = item.Substring("dlcpacks:/".Length).Trim('/');
            if (name.Length == 0 || name.Contains('/') || !seen.Add(name)) continue;
            // a name Rockstar ships in the game's own update\x64\dlcpacks is theirs, wherever a copy sits
            if (Directory.Exists(game.Resolve(Path.Combine("update", "x64", "dlcpacks", name)))) continue;
            var candidates = game.HasOnigiri
                ? new[] { Path.Combine("onigiri", "dlcpacks", name, "dlc.rpf"), Path.Combine("mods", "update", "x64", "dlcpacks", name, "dlc.rpf") }
                : new[] { Path.Combine("mods", "update", "x64", "dlcpacks", name, "dlc.rpf") };
            var rel = candidates.FirstOrDefault(c => File.Exists(game.Resolve(c)));
            if (rel != null) result.Add((name, rel));
        }
        return result;
    }

    /// <summary>Every legacy-format .ytd in the pack (nested archives included), as "inner/path.ytd".</summary>
    static List<(string Inner, RpfResourceFileEntry Entry, RpfFile Archive)> Ytds(RpfFile top, out int gen9)
    {
        var list = new List<(string, RpfResourceFileEntry, RpfFile)>();
        gen9 = 0;
        var prefix = top.Path.Replace('/', '\\').TrimEnd('\\') + "\\";
        foreach (var archive in Walk(top))
        {
            if (archive.AllEntries == null) continue;
            foreach (var e in archive.AllEntries.OfType<RpfResourceFileEntry>())
            {
                if (!e.NameLower.EndsWith(".ytd")) continue;
                // version 13 = the classic layout; 5 = Enhanced's gen9 layout, which keeps its mips elsewhere
                if (e.Version != 13) { gen9++; continue; }
                var full = e.Path.Replace('/', '\\');
                if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                list.Add((full.Substring(prefix.Length).Replace('\\', '/'), e, archive));
            }
        }
        return list;
    }

    static IEnumerable<RpfFile> Walk(RpfFile f)
    {
        yield return f;
        if (f.Children == null) yield break;
        foreach (var c in f.Children)
            foreach (var x in Walk(c)) yield return x;
    }

    static YtdFile? Load(RpfFile archive, RpfResourceFileEntry entry)
    {
        var data = archive.ExtractFile(entry);
        if (data == null) return null;
        var ytd = new YtdFile();
        ytd.Load(data, entry);
        return ytd;
    }

    static IEnumerable<Texture> TexturesOf(YtdFile ytd) =>
        ytd.TextureDict?.Textures?.data_items?.Where(t => t != null) ?? Enumerable.Empty<Texture>();

    static TexOwner? OwnerOf(ModLibrary library, GameInstall game, string rel)
    {
        lock (library.Gate)
        {
            var m = library.ForGame(game.Id).FirstOrDefault(m => m.Source != "tool"
                && m.Entries.Any(e => e.Type == JournalEntry.File && string.Equals(e.Path, rel, StringComparison.OrdinalIgnoreCase)));
            return m == null ? null : new TexOwner(m.Id, m.Name);
        }
    }

    /// <summary>The library record of an earlier optimization of this pack.</summary>
    public static InstalledMod? RecordFor(ModLibrary library, GameInstall game, string rel)
    {
        lock (library.Gate)
        {
            return library.ForGame(game.Id).FirstOrDefault(m => m.Source == "tool" && m.Name.StartsWith(RecordPrefix, StringComparison.Ordinal)
                && m.Entries.Count > 0 && m.Entries.All(e => e.Type == JournalEntry.RpfEntry && string.Equals(e.Path, rel, StringComparison.OrdinalIgnoreCase)));
        }
    }

    static int MaxSizeOf(InstalledMod m) =>
        int.TryParse(new string((m.Version ?? "").TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 0;

    // ---------------------------------------------------------------- scan

    sealed record CachedScan(string Key, TexPack Pack);

    static string CacheFile(LauncherPaths paths, string physical, int maxSize)
    {
        var fi = new FileInfo(physical);
        // bump the version when TexPack gains fields, so older cached scans are read again
        var key = $"v2|{physical.ToLowerInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{maxSize}";
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        var dir = Path.Combine(paths.Cache, "textures");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, hash + ".json");
    }

    public static TexScanResult Scan(GameInstall game, LauncherPaths paths, ModLibrary library, int maxSize, Action<string, double?>? progress = null)
    {
        if (!Sizes.Contains(maxSize)) throw new ArgumentException("Pick 512, 1024, 2048 or 4096 px.");
        using var session = RpfService.Session(game);
        var rpf = new RpfService(game);
        var packs = ActivePacks(game, rpf);
        var result = new List<TexPack>();

        for (int i = 0; i < packs.Count; i++)
        {
            var (name, rel) = packs[i];
            progress?.Invoke($"Reading {name}", (double)i / Math.Max(1, packs.Count));
            var physical = game.Resolve(rel);
            TexPack pack;
            var cache = CacheFile(paths, physical, maxSize);
            var cached = TryReadCache(cache);
            if (cached != null)
            {
                pack = cached;
            }
            else
            {
                pack = ScanPack(rpf, physical, name, rel, maxSize);
                try { File.WriteAllText(cache, JsonSerializer.Serialize(pack)); } catch { }
            }
            pack.Owner = OwnerOf(library, game, rel);
            var record = RecordFor(library, game, rel);
            pack.AlreadyOptimized = record == null ? null : new TexAlready(record.Id, MaxSizeOf(record));
            result.Add(pack);
            rpf.Forget(physical); // don't keep every pack's tree in memory
        }
        progress?.Invoke("Done", 1);
        result = result.OrderByDescending(p => p.SavableBytes).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new TexScanResult(maxSize, result, result.Sum(p => p.Bytes), result.Sum(p => p.SavableBytes));
    }

    static TexPack? TryReadCache(string file)
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<TexPack>(File.ReadAllText(file)) : null; }
        catch { return null; }
    }

    static TexPack ScanPack(RpfService rpf, string physical, string name, string rel, int maxSize)
    {
        var pack = new TexPack { Id = rel, Name = name };
        RpfFile top;
        try { top = rpf.Open(physical); }
        catch (Exception ex) { pack.Skipped = "The pack couldn't be read: " + ex.Message; return pack; }

        var ytds = Ytds(top, out var gen9);
        // names and sizes only: holding the textures would keep every pixel of the pack in memory (GBs for big packs)
        var all = new List<TexWorst>();
        foreach (var (inner, entry, archive) in ytds)
        {
            YtdFile? ytd;
            try { ytd = Load(archive, entry); }
            catch { continue; }
            if (ytd == null) continue;
            foreach (var t in TexturesOf(ytd))
            {
                var bytes = t.Data?.FullData?.LongLength ?? 0;
                pack.Textures++;
                pack.Bytes += bytes;
                if (Math.Max(t.Width, t.Height) > maxSize)
                {
                    pack.Oversized++;
                    var (drop, reason) = TextureMips.Plan(t, maxSize);
                    pack.SavableBytes += TextureMips.SavedBytes(t, drop);
                    if (drop == 0)
                    {
                        pack.Kept++;
                        if (reason != null && !pack.KeptReasons.Contains(reason)) pack.KeptReasons.Add(reason);
                    }
                }
                all.Add(new TexWorst(inner, t.Name ?? "", t.Width, t.Height, FormatName(t.Format)));
            }
        }
        pack.Worst = all.OrderByDescending(x => (long)x.Width * x.Height).Take(5).ToList();
        if (ytds.Count == 0 && gen9 > 0)
            pack.Skipped = "Enhanced-format (gen9) textures: the launcher can't shrink these yet.";
        else if (ytds.Count == 0)
            pack.Skipped = "No textures in this pack.";
        return pack;
    }

    static string FormatName(TextureFormat f) => f switch
    {
        TextureFormat.D3DFMT_DXT1 => "DXT1",
        TextureFormat.D3DFMT_DXT3 => "DXT3",
        TextureFormat.D3DFMT_DXT5 => "DXT5",
        TextureFormat.D3DFMT_ATI1 => "BC4",
        TextureFormat.D3DFMT_ATI2 => "BC5",
        TextureFormat.D3DFMT_BC7 => "BC7",
        TextureFormat.D3DFMT_A8R8G8B8 => "A8R8G8B8",
        _ => f.ToString().Replace("D3DFMT_", ""),
    };

    // ---------------------------------------------------------------- optimize

    public static TexOptimizeResult Optimize(GameInstall game, LauncherPaths paths, ModLibrary library, IReadOnlyCollection<string> packIds,
        int maxSize, Action<string, double?>? progress = null)
    {
        if (!Sizes.Contains(maxSize)) throw new ArgumentException("Pick 512, 1024, 2048 or 4096 px.");
        using var session = RpfService.Session(game);
        new ModInstaller(game, paths, library).Preflight();

        // only packs the scan would show: the page can't name an arbitrary archive
        var active = ActivePacks(game, new RpfService(game)).ToDictionary(p => p.Rel, p => p.Name, StringComparer.OrdinalIgnoreCase);
        var optimized = new List<TexOptimizedPack>();
        var problems = new List<string>();
        var service = new ModService(paths, library);
        int n = 0;
        foreach (var id in packIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            n++;
            if (!active.TryGetValue(Journal.NormalizeRel(id), out var name)) { problems.Add($"{id}: not an active add-on pack."); continue; }
            var rel = Journal.NormalizeRel(id);
            progress?.Invoke($"Shrinking textures in {name}", (double)(n - 1) / Math.Max(1, packIds.Count));
            try
            {
                // a pack optimized before goes back to its original textures first
                var old = RecordFor(library, game, rel);
                if (old != null)
                {
                    var undo = service.Uninstall(game, old);
                    if (undo.Count > 0) { problems.Add($"{name}: couldn't undo the earlier optimization ({undo[0]})."); continue; }
                }
                var r = OptimizePack(game, paths, library, rel, name, maxSize, t => progress?.Invoke($"Shrinking textures in {name}", null));
                if (r != null) optimized.Add(r);
                else if (old != null) problems.Add($"{name}: back to its original textures (nothing in it is over {maxSize} px).");
                else problems.Add($"{name}: nothing to shrink at {maxSize} px.");
            }
            catch (Exception ex)
            {
                problems.Add($"{name}: {ex.Message}");
            }
        }
        progress?.Invoke("Done", 1);
        return new TexOptimizeResult(optimized, problems);
    }

    static TexOptimizedPack? OptimizePack(GameInstall game, LauncherPaths paths, ModLibrary library, string rel, string name, int maxSize, Action<string>? log)
    {
        var mod = new InstalledMod
        {
            Name = RecordPrefix + name,
            Source = "tool",
            Summary = $"Texture optimization (max {maxSize} px)",
            Version = $"{maxSize} px",
            Author = "V Mod Launcher",
            GameId = game.Id,
            InstalledAt = DateTime.UtcNow,
            Kind = "other",
        };
        library.Upsert(mod);
        var rpf = new RpfService(game);
        var last = DateTime.UtcNow;
        var journal = new Journal(game, rpf, mod, paths.Backups, library, onChange: () =>
        {
            if ((DateTime.UtcNow - last).TotalMilliseconds < 1000) return;
            last = DateTime.UtcNow;
            library.Save();
        });
        bool inMods = rel.StartsWith(@"mods\", StringComparison.OrdinalIgnoreCase);
        var physical = game.Resolve(rel);
        long saved = 0;
        int textures = 0;
        try
        {
            var inners = Ytds(rpf.Open(physical), out _).Select(x => x.Inner).ToList();
            foreach (var inner in inners)
            {
                // re-locate after every write: the archive's tree is updated in place
                var loc = rpf.Locate(rpf.Open(physical), inner, createDirs: false);
                if (loc.Entry is not RpfResourceFileEntry entry) continue;
                var ytd = Load(loc.Archive, entry);
                if (ytd == null) continue;
                long ytdSaved = 0;
                int ytdCount = 0;
                foreach (var t in TexturesOf(ytd))
                {
                    var (drop, _) = TextureMips.Plan(t, maxSize);
                    if (drop <= 0) continue;
                    ytdSaved += TextureMips.Strip(t, drop);
                    ytdCount++;
                }
                if (ytdCount == 0) continue;
                // always the classic (version 13) layout the pack came with, whatever edition is running
                var bytes = ResourceBuilder.Build(ytd.TextureDict, 13, true, false);
                log?.Invoke(inner);
                if (inMods) journal.WriteRpf(rel, inner, bytes);
                else WriteInPlace(journal, mod, library, paths, game, rel, inner, bytes);
                saved += ytdSaved;
                textures += ytdCount;
            }
        }
        catch
        {
            Rollback(journal, mod, library);
            throw;
        }

        if (textures == 0)
        {
            Rollback(journal, mod, library);
            return null;
        }
        library.Upsert(mod);
        return new TexOptimizedPack(rel, mod.Id, saved, textures);
    }

    static void Rollback(Journal journal, InstalledMod mod, ModLibrary library)
    {
        var problems = journal.UndoAll();
        if (problems.Count > 0)
        {
            mod.NeedsCleanup = true;
            library.Upsert(mod);
        }
        else
        {
            library.Remove(mod);
        }
    }

    /// <summary>
    /// NaturalVision's onigiri packs are read straight from onigiri\dlcpacks (not through mods\), so they're
    /// edited where they are. Journaled the same way Journal.WriteRpf does it, so Uninstall restores them.
    /// </summary>
    static void WriteInPlace(Journal journal, InstalledMod mod, ModLibrary library, LauncherPaths paths, GameInstall game, string rel, string inner, byte[] data)
    {
        var previous = journal.Rpf.Write(game.Resolve(rel), inner, data);
        string? backup = null;
        if (previous != null)
        {
            var dir = Path.Combine(paths.Backups, mod.Id);
            Directory.CreateDirectory(dir);
            backup = $"tex-{Guid.NewGuid():N}.bak";
            File.WriteAllBytes(Path.Combine(dir, backup), previous);
        }
        lock (library.Gate)
            mod.Entries.Add(new JournalEntry { Type = JournalEntry.RpfEntry, Path = rel, Inner = inner, Backup = backup, Hash = RpfService.ContentHash(data) });
        library.Save();
    }
}
