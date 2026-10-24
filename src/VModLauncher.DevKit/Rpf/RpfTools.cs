using System.Security.Cryptography;
using System.Text;
using CodeWalker.GameFiles;
using VModLauncher.Core.Game;
using VModLauncher.Core.Install;

namespace VModLauncher.DevKit.Rpf;

/// <summary>One file inside an archive (nested archives walked through, not listed as files).</summary>
/// <param name="InnerPath">'/'-separated from the top archive, nested archives as path segments ("x64/data/cdimages/scaleform_frontend.rpf/a.ytd").</param>
public sealed record RpfItem(RpfFile Archive, RpfFileEntry Entry, string InnerPath)
{
    public bool IsResource => Entry is RpfResourceFileEntry;
    public long Size => Entry.FileSize > 0 ? Entry.FileSize : Entry is RpfResourceFileEntry r ? r.FileSize : 0;
}

/// <summary>
/// Read-only archive access for the tools: open any .rpf (in a game or not) with the keys from a game's exe,
/// walk it, read, extract and compare entries. Writing into archives is the installer's job (through the journal).
/// </summary>
public sealed class RpfTools
{
    public RpfService Service { get; }
    public GameInstall Keys { get; }

    /// <summary>Archives read with <paramref name="keys"/>'s keys (its <see cref="GameInstall.KeysRoot"/>, else its own exe).</summary>
    public RpfTools(GameInstall keys)
    {
        Keys = keys;
        Service = new RpfService(keys);
    }

    /// <summary>
    /// Archives under <paramref name="gameForKeys"/> (or anywhere, read with its keys). With no game, only OPEN
    /// archives and OPEN nested archives can be read.
    /// </summary>
    public static RpfTools For(string? gameForKeys, GameEdition? edition = null)
    {
        if (gameForKeys != null)
        {
            var g = GameInstall.TryOpen(gameForKeys) ?? throw new DirectoryNotFoundException("Not a GTA V folder (no GTA5.exe / GTA5_Enhanced.exe): " + gameForKeys);
            return new RpfTools(g);
        }
        // a stand-in game with no exe: keys fail to load and encrypted content stays unreadable
        var none = Path.Combine(Path.GetTempPath(), "vml-nokeys");
        Directory.CreateDirectory(none);
        return new RpfTools(new GameInstall { Root = none, Edition = edition ?? GameEdition.Legacy });
    }

    public RpfFile Open(string path) => Service.Open(path);

    public IEnumerable<RpfItem> Walk(string path) => Walk(Open(path));

    public static IEnumerable<RpfItem> Walk(RpfFile top)
    {
        var prefix = top.Path.Length + 1;
        var stack = new Stack<RpfFile>();
        stack.Push(top);
        while (stack.Count > 0)
        {
            var f = stack.Pop();
            foreach (var e in f.AllEntries.OfType<RpfFileEntry>())
                if (!e.Name.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
                    yield return new RpfItem(f, e, e.Path.Length > prefix ? e.Path[prefix..].Replace('\\', '/') : e.Name);
            foreach (var c in (f.Children ?? new()).AsEnumerable().Reverse()) stack.Push(c);
        }
    }

    /// <summary>Names of nested archives that couldn't be opened (encrypted with keys we don't have).</summary>
    public static IEnumerable<string> Unreadable(RpfFile top)
    {
        var prefix = top.Path.Length + 1;
        foreach (var f in AllArchives(top))
            foreach (var e in f.AllEntries.OfType<RpfFileEntry>().Where(e => e.Name.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase)))
                if (!(f.Children ?? new()).Any(c => c.ParentFileEntry == e && c.Root != null))
                    yield return e.Path.Length > prefix ? e.Path[prefix..].Replace('\\', '/') : e.Name;
    }

    static IEnumerable<RpfFile> AllArchives(RpfFile top)
    {
        yield return top;
        foreach (var c in top.Children ?? new())
            foreach (var x in AllArchives(c)) yield return x;
    }

    /// <summary>The file's data as CodeWalker extracts it (resources decompressed).</summary>
    public static byte[] Extract(RpfItem item) =>
        item.Archive.ExtractFile(item.Entry) ?? throw new IOException($"Couldn't read {item.Entry.Path}: {item.Archive.LastError}");

    /// <summary>Raw bytes as a standalone file (resources with their RSC7 header).</summary>
    public static byte[] Raw(RpfItem item) => RpfService.ReadRaw(item.Archive, item.Entry);

    /// <summary>Same data, same file: a resource's page flags plus the hash of its decompressed data (no recompression).</summary>
    public static string ContentHash(RpfItem item)
    {
        var data = Extract(item);
        var flags = item.Entry is RpfResourceFileEntry r ? $"{r.SystemFlags:X8}{r.GraphicsFlags:X8}:" : "";
        return flags + Convert.ToHexString(SHA256.HashData(data));
    }

    public byte[]? Read(string archive, string innerPath) => Service.TryRead(archive, innerPath);

    /// <summary>A text file from an archive (UTF-8 / UTF-16 by BOM; .ymt/.meta resources aren't text).</summary>
    public string? ReadText(string archive, string innerPath)
    {
        var data = Read(archive, innerPath);
        if (data == null) return null;
        if (data.Length >= 4 && BitConverter.ToUInt32(data, 0) == 0x37435352)
            throw new InvalidDataException($"{innerPath} is a binary resource (RSC7), not text: extract it instead");
        return Oiv.OivZip.Decode(data);
    }

    /// <summary>Extracts every file whose inner path matches (all when <paramref name="filter"/> is empty) as standalone files.</summary>
    public List<string> ExtractTo(string archive, string outDir, IReadOnlyList<Glob> filter)
    {
        var written = new List<string>();
        foreach (var item in Walk(archive))
        {
            if (filter.Count > 0 && !Glob.Any(filter, item.InnerPath)) continue;
            var to = Path.GetFullPath(Path.Combine(outDir, item.InnerPath.Replace('/', '\\')));
            if (!to.StartsWith(Path.GetFullPath(outDir), StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.WriteAllBytes(to, Raw(item));
            written.Add(to);
        }
        return written;
    }

    public sealed record DiffResult(int Same, List<string> OnlyFirst, List<string> OnlySecond, List<string> Differ)
    {
        public string ToText() =>
            $"{Same} same, {Differ.Count} different, {OnlyFirst.Count} only in the first, {OnlySecond.Count} only in the second\n" +
            string.Concat(Differ.Select(d => "  differs: " + d + "\n")) +
            string.Concat(OnlyFirst.Select(d => "  only in first: " + d + "\n")) +
            string.Concat(OnlySecond.Select(d => "  only in second: " + d + "\n"));
    }

    /// <summary>Every file in two archives compared by content (nested archives through their files).</summary>
    public DiffResult Diff(string a, string b)
    {
        var x = Walk(a).ToDictionary(i => i.InnerPath, StringComparer.OrdinalIgnoreCase);
        var y = Walk(b).ToDictionary(i => i.InnerPath, StringComparer.OrdinalIgnoreCase);
        int same = 0;
        var differ = new List<string>();
        foreach (var k in x.Keys.Intersect(y.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            if (ContentHash(x[k]) == ContentHash(y[k])) same++; else differ.Add(k);
        return new DiffResult(same,
            x.Keys.Except(y.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList(),
            y.Keys.Except(x.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList(),
            differ);
    }

    public string List(string archive, IReadOnlyList<Glob> filter, int max = 2000)
    {
        var top = Open(archive);
        var sb = new StringBuilder();
        int n = 0, shown = 0;
        foreach (var item in Walk(top))
        {
            if (filter.Count > 0 && !Glob.Any(filter, item.InnerPath)) continue;
            n++;
            if (shown >= max) continue;
            shown++;
            var kind = item.Entry is RpfResourceFileEntry r ? $"resource v{r.Version}" : "file";
            sb.AppendLine($"{item.InnerPath}  ({kind}, {item.Size:N0} bytes)");
        }
        foreach (var u in Unreadable(top)) sb.AppendLine($"{u}  (nested archive, can't be opened: it's encrypted; give a game folder for its keys)");
        sb.Insert(0, $"{archive}: {n} files{(shown < n ? $", first {shown} shown" : "")}\n");
        return sb.ToString();
    }
}
