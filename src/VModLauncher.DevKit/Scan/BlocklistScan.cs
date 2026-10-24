using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace VModLauncher.DevKit.Scan;

/// <summary>
/// Makes sure no file from a folder you must never publish (private tools, someone else's files, game files)
/// ends up in a release or a repo: every file in the targets, and every entry inside zips and .oiv packages, is
/// compared by size and CRC32 against the folder's files. Byte-identical copies are found whatever they're named.
/// </summary>
public sealed class BlocklistScanResult
{
    public int Indexed { get; set; }
    public List<string> Checked { get; } = new();
    public List<(string Found, string Matches)> Matches { get; } = new();
    public bool Clean => Matches.Count == 0;

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"blocklist: {Indexed} files indexed");
        foreach (var c in Checked) sb.AppendLine("  checked " + c);
        foreach (var (f, m) in Matches) sb.AppendLine($"  MATCH {f}  ==  {m}");
        sb.AppendLine(Clean ? "CLEAN" : $"{Matches.Count} MATCHES");
        return sb.ToString();
    }
}

public static class BlocklistScan
{
    /// <param name="targets">Files, folders, .zip/.oiv packages, or "git:&lt;repo folder&gt;" for the files git tracks there.</param>
    public static BlocklistScanResult Run(string blocklistFolder, IEnumerable<string> targets, IProgress<string>? log = null)
    {
        var result = new BlocklistScanResult();
        var bySize = new Dictionary<long, List<string>>();
        foreach (var f in Directory.EnumerateFiles(blocklistFolder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
        {
            long size;
            try { size = new FileInfo(f).Length; } catch { continue; }
            if (size == 0) continue;
            if (!bySize.TryGetValue(size, out var l)) bySize[size] = l = new();
            l.Add(f);
            result.Indexed++;
        }
        log?.Report($"{result.Indexed} files indexed");
        var crcs = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        uint Crc(string path)
        {
            if (crcs.TryGetValue(path, out var c)) return c;
            using var s = File.OpenRead(path);
            return crcs[path] = Crc32Of(s);
        }
        string? Match(long size, Func<uint> crc)
        {
            if (size == 0 || !bySize.TryGetValue(size, out var candidates)) return null;
            var want = crc();
            return candidates.FirstOrDefault(c => Crc(c) == want);
        }

        foreach (var target in targets)
        {
            if (target.StartsWith("git:", StringComparison.OrdinalIgnoreCase))
            {
                var repo = Path.GetFullPath(target[4..]);
                var files = GitFiles(repo);
                foreach (var rel in files)
                {
                    var full = Path.Combine(repo, rel);
                    if (!File.Exists(full)) continue;
                    var m = Match(new FileInfo(full).Length, () => Crc(full));
                    if (m != null) result.Matches.Add((full, m));
                }
                result.Checked.Add($"{repo}: {files.Count} files git tracks");
                continue;
            }
            var path = Path.GetFullPath(target);
            if (Directory.Exists(path))
            {
                int n = 0;
                foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    n++;
                    var m = Match(new FileInfo(f).Length, () => Crc(f));
                    if (m != null) result.Matches.Add((f, m));
                    if (IsZip(f)) ScanZip(f, Match, result);
                }
                result.Checked.Add($"{path}: {n} files");
            }
            else if (File.Exists(path))
            {
                var m = Match(new FileInfo(path).Length, () => Crc(path));
                if (m != null) result.Matches.Add((path, m));
                if (IsZip(path)) ScanZip(path, Match, result);
                result.Checked.Add(path);
            }
            else throw new FileNotFoundException("Nothing to scan at " + target);
        }
        return result;
    }

    static bool IsZip(string f) => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".oiv", StringComparison.OrdinalIgnoreCase);

    static void ScanZip(string zipPath, Func<long, Func<uint>, string?> match, BlocklistScanResult result)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var e in zip.Entries.Where(e => !e.FullName.EndsWith('/')))
        {
            // the zip's own CRC32 is the content's, so nothing has to be read
            var m = match(e.Length, () => e.Crc32);
            if (m != null) result.Matches.Add(($"{zipPath} > {e.FullName}", m));
        }
    }

    static List<string> GitFiles(string repo)
    {
        var psi = new ProcessStartInfo("git", "ls-files -z")
        {
            WorkingDirectory = repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("git didn't start");
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git ls-files failed in {repo}: {p.StandardError.ReadToEnd()}");
        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(r => r.Replace('/', '\\')).ToList();
    }

    static readonly uint[] Table = Enumerable.Range(0, 256).Select(i =>
    {
        uint c = (uint)i;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static uint Crc32Of(Stream s)
    {
        uint crc = 0xFFFFFFFFu;
        var buf = new byte[1 << 20];
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0)
            for (int i = 0; i < n; i++) crc = Table[(crc ^ buf[i]) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }
}
