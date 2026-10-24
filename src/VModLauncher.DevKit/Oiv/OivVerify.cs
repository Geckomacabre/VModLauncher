using System.Security.Cryptography;
using System.Text;

namespace VModLauncher.DevKit.Oiv;

/// <summary>
/// A built package checked against where its files came from: every file in content\ byte-identical to its
/// source. The sources come from the project the package was built from, or from "prefix=folder" mappings
/// (content\scripts\... ↔ C:\mod\scripts\...). Also lists the sha256 of every file, for release notes.
/// </summary>
public sealed class VerifyResult
{
    public string Oiv { get; init; } = "";
    public int Identical { get; set; }
    public List<string> Differ { get; } = new();
    public List<string> NoSource { get; } = new();
    /// <summary>Files the project packs that the package doesn't have.</summary>
    public List<string> MissingFromPackage { get; } = new();
    public List<string> Hashes { get; } = new();

    public bool Ok => Differ.Count == 0 && NoSource.Count == 0 && MissingFromPackage.Count == 0;

    public string ToText(bool withHashes = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{System.IO.Path.GetFileName(Oiv)}: {Identical} files identical to their source, {Differ.Count} differ, " +
                      $"{NoSource.Count} without a source, {MissingFromPackage.Count} missing from the package");
        foreach (var d in Differ.Take(50)) sb.AppendLine("  DIFFERS  " + d);
        foreach (var d in NoSource.Take(50)) sb.AppendLine("  NO SOURCE  " + d);
        foreach (var d in MissingFromPackage.Take(50)) sb.AppendLine("  MISSING  " + d);
        sb.AppendLine(Ok ? "  OK" : "  NOT OK");
        if (withHashes) foreach (var h in Hashes) sb.AppendLine(h);
        return sb.ToString();
    }
}

public static class OivVerify
{
    /// <summary>Checks a package against the project it was built from.</summary>
    public static VerifyResult AgainstProject(string oivPath, OivProject project, string? label = null)
    {
        var plan = OivBuilder.Plan(project);
        var sources = plan.Entries.ToDictionary(f => f.ContentPath.Replace('\\', '/'), f => f.Source, StringComparer.OrdinalIgnoreCase);
        return Run(oivPath, rel => sources.TryGetValue(rel, out var s) ? s : null, label, sources.Keys);
    }

    /// <summary>Checks a package against "prefix=folder" mappings; the longest matching prefix wins, "" is content\ itself.</summary>
    public static VerifyResult AgainstFolders(string oivPath, IReadOnlyDictionary<string, string> map, string? label = null)
    {
        var maps = map.Select(kv => (Prefix: kv.Key.Replace('\\', '/').Trim('/'), Folder: Path.GetFullPath(kv.Value)))
            .OrderByDescending(m => m.Prefix.Length).ToList();
        string? Source(string rel)
        {
            foreach (var (prefix, folder) in maps)
            {
                if (prefix.Length == 0) return Path.Combine(folder, rel.Replace('/', '\\'));
                if (rel.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                    return Path.Combine(folder, rel[(prefix.Length + 1)..].Replace('/', '\\'));
            }
            return null;
        }
        return Run(oivPath, Source, label, expected: null);
    }

    static VerifyResult Run(string oivPath, Func<string, string?> sourceOf, string? label, IEnumerable<string>? expected)
    {
        using var oiv = OivZip.Open(oivPath);
        var result = new VerifyResult { Oiv = oiv.Path };
        label ??= System.IO.Path.GetFileNameWithoutExtension(oiv.Path);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in oiv.ContentEntries.OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            var rel = oiv.ContentPathOf(e);
            seen.Add(rel);
            string hash;
            bool same;
            var src = sourceOf(rel);
            using (var s = e.Open())
            {
                if (src == null || !File.Exists(src))
                {
                    hash = Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
                    result.NoSource.Add(rel + (src != null ? "  (expected " + src + ")" : ""));
                    result.Hashes.Add($"{hash} {label}: {rel}");
                    continue;
                }
                (same, hash) = CompareStream(s, src);
            }
            result.Hashes.Add($"{hash} {label}: {rel}");
            if (same) result.Identical++;
            else result.Differ.Add(rel + "  vs " + src);
        }
        if (expected != null)
            result.MissingFromPackage.AddRange(expected.Where(x => !seen.Contains(x)));
        return result;
    }

    /// <summary>Compares a zip entry with a file while hashing the entry.</summary>
    static (bool Same, string Hash) CompareStream(Stream entry, string file)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        var a = new byte[1 << 20];
        var b = new byte[1 << 20];
        bool same = true;
        while (true)
        {
            int na = Fill(entry, a);
            sha.AppendData(a, 0, na);
            if (same)
            {
                int nb = Fill(f, b);
                if (na != nb || !a.AsSpan(0, na).SequenceEqual(b.AsSpan(0, nb))) same = false;
            }
            if (na == 0) break;
        }
        if (same && f.Position != f.Length) same = false;
        return (same, Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant());
    }

    static int Fill(Stream s, byte[] buf)
    {
        int got = 0, n;
        while (got < buf.Length && (n = s.Read(buf, got, buf.Length - got)) > 0) got += n;
        return got;
    }
}
