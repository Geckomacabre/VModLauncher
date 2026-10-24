using System.IO.Compression;
using System.Text;
using CodeWalker.GameFiles;
using VModLauncher.Core.Game;

namespace VModLauncher.Core.Install;

/// <summary>
/// Where every vanilla file lives inside the game's archives, so loose "replace" files
/// (adder.yft, adder.ytd, ...) can go into the copy the game actually loads.
/// Built once per game version (about a minute) and cached gzipped in the launcher's data.
/// </summary>
public sealed class GameIndex : IGameIndex
{
    readonly Dictionary<string, List<(string Location, int Rank)>> byName = new(StringComparer.OrdinalIgnoreCase);
    readonly GameInstall game;
    RpfService? rpf;
    public int Count { get; private set; }

    GameIndex(GameInstall game) => this.game = game;

    /// <summary>Reads the game's own copy (never a mods\ copy) at "archive|inner".</summary>
    public byte[]? Read(string location)
    {
        var (archive, inner) = ModAnalyzer.SplitRpfTarget(location);
        rpf ??= new RpfService(game);
        var path = game.Resolve(archive);
        return File.Exists(path) ? rpf.TryRead(path, inner) : null;
    }

    public IReadOnlyList<string> Find(string fileName) =>
        byName.TryGetValue(fileName, out var list)
            ? list.OrderByDescending(l => l.Rank).Select(l => l.Location).ToList()
            : Array.Empty<string>();

    // ---------------------------------------------------------------- cache

    static string CacheFile(GameInstall game, string cacheDir)
    {
        // a game update changes the exe and update.rpf, which makes a new cache file
        long Stamp(string f) => File.Exists(f) ? new FileInfo(f).Length ^ new FileInfo(f).LastWriteTimeUtc.Ticks : 0;
        var key = $"{game.Id}|{Stamp(game.ExePath)}|{Stamp(game.GameUpdateRpf)}";
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        return Path.Combine(cacheDir, $"index-{game.Edition.ToString().ToLowerInvariant()}-{hash}.txt.gz");
    }

    public static GameIndex? TryLoad(GameInstall game, string cacheDir)
    {
        var file = CacheFile(game, cacheDir);
        if (!File.Exists(file)) return null;
        try
        {
            var index = new GameIndex(game);
            using var gz = new GZipStream(File.OpenRead(file), CompressionMode.Decompress);
            using var reader = new StreamReader(gz, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var parts = line.Split('\t');
                if (parts.Length == 3) index.Add(parts[0], parts[1], int.Parse(parts[2]));
            }
            return index;
        }
        catch { return null; }
    }

    public static GameIndex Build(GameInstall game, string cacheDir, IProgress<string>? progress = null)
    {
        using var session = RpfService.Session(game);
        var rpf = new RpfService(game);
        rpf.EnsureKeys();
        var order = DlcOrder(game, rpf);
        var index = new GameIndex(game);

        var archives = Directory.EnumerateFiles(game.Root, "*.rpf", SearchOption.AllDirectories)
            .Select(game.ToRelative)
            // only the game's own archives: mods\ copies and NVE's onigiri packs aren't vanilla
            .Where(r => !r.StartsWith("mods", StringComparison.OrdinalIgnoreCase) && !r.StartsWith("onigiri\\", StringComparison.OrdinalIgnoreCase)) // mods and a parked mods.vml-off aren't vanilla
            .ToList();

        int done = 0;
        foreach (var rel in archives)
        {
            progress?.Report($"Scanning {rel} ({++done}/{archives.Count})");
            RpfFile top;
            try { top = rpf.Open(game.Resolve(rel)); }
            catch { continue; }
            int rank = Rank(rel, order);
            foreach (var archive in Walk(top))
            {
                foreach (var e in archive.AllEntries.OfType<RpfFileEntry>())
                {
                    var full = e.Path.Replace('/', '\\');
                    // e.Path starts with the archive's own relative path
                    if (!full.StartsWith(rel + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                    index.Add(e.Name, rel + "|" + full.Substring(rel.Length + 1).Replace('\\', '/'), rank);
                }
            }
            rpf.Forget(game.Resolve(rel)); // don't keep every archive tree in memory
        }

        Directory.CreateDirectory(cacheDir);
        var file = CacheFile(game, cacheDir);
        using (var gz = new GZipStream(File.Create(file + ".tmp"), CompressionLevel.Optimal))
        using (var w = new StreamWriter(gz, Encoding.UTF8))
        {
            foreach (var (name, list) in index.byName)
                foreach (var (loc, r) in list)
                    w.WriteLine($"{name}\t{loc}\t{r}");
        }
        File.Move(file + ".tmp", file, overwrite: true);
        progress?.Report($"Indexed {index.Count:N0} game files");
        return index;
    }

    void Add(string name, string location, int rank)
    {
        if (!byName.TryGetValue(name, out var list)) byName[name] = list = new();
        list.Add((location, rank));
        Count++;
    }

    static IEnumerable<RpfFile> Walk(RpfFile f)
    {
        yield return f;
        if (f.Children == null) yield break;
        foreach (var c in f.Children)
            foreach (var x in Walk(c)) yield return x;
    }

    /// <summary>
    /// Which copy the game uses: later DLC packs override earlier ones (patchday packs come last),
    /// DLC overrides update.rpf, and update.rpf overrides the base x64*.rpf archives.
    /// </summary>
    static int Rank(string rel, Dictionary<string, int> dlcOrder)
    {
        var lower = rel.ToLowerInvariant();
        var m = System.Text.RegularExpressions.Regex.Match(lower, @"dlcpacks\\([^\\]+)\\dlc\.rpf$");
        if (m.Success)
            return 2000 + (dlcOrder.TryGetValue(m.Groups[1].Value, out var i) ? i : 0);
        if (lower.StartsWith("update\\")) return 1000;
        return 0;
    }

    static Dictionary<string, int> DlcOrder(GameInstall game, RpfService rpf)
    {
        var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var bytes = rpf.TryRead(game.GameUpdateRpf, DlcList.InnerPath);
            if (bytes == null) return order;
            int i = 0;
            foreach (var item in DlcList.Items(DlcList.Decode(bytes, out _)))
            {
                var name = item.TrimEnd('/').Split('/').Last();
                order[name] = ++i;
            }
        }
        catch { }
        return order;
    }
}
