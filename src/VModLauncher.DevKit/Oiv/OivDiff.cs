using System.Text;

namespace VModLauncher.DevKit.Oiv;

/// <summary>Two packages compared entry by entry (contents, not zip bytes), and their assembly.xml line by line.</summary>
public sealed class OivDiffResult
{
    public string Old { get; init; } = "";
    public string New { get; init; } = "";
    public int Same { get; set; }
    public List<(string Entry, DateTimeOffset Date)> Added { get; } = new();
    public List<string> Removed { get; } = new();
    public List<(string Entry, DateTimeOffset OldDate, DateTimeOffset NewDate)> Changed { get; } = new();
    public List<string> AssemblyDiff { get; } = new();

    public bool Identical => Added.Count == 0 && Removed.Count == 0 && Changed.Count == 0 && AssemblyDiff.Count == 0;

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{System.IO.Path.GetFileName(Old)} -> {System.IO.Path.GetFileName(New)}");
        sb.AppendLine($"  {Same} files the same, {Added.Count} added, {Removed.Count} removed, {Changed.Count} changed");
        foreach (var (e, d) in Added) sb.AppendLine($"  added    {e}   ({d:yyyy-MM-dd HH:mm})");
        foreach (var e in Removed) sb.AppendLine($"  removed  {e}");
        foreach (var (e, od, nd) in Changed)
            sb.AppendLine($"  changed  {e}   ({od:yyyy-MM-dd HH:mm} -> {nd:yyyy-MM-dd HH:mm}{(od == nd ? ", SAME DATE: Script Hook V .NET may keep the cached old copy" : "")})");
        if (AssemblyDiff.Count == 0) sb.AppendLine("  assembly.xml: the same");
        else
        {
            sb.AppendLine("  assembly.xml:");
            foreach (var l in AssemblyDiff) sb.AppendLine("    " + (l.Length > 160 ? l[..160] + "..." : l));
        }
        return sb.ToString();
    }
}

public static class OivDiff
{
    public static OivDiffResult Compare(string oldOiv, string newOiv)
    {
        using var a = OivZip.Open(oldOiv);
        using var b = OivZip.Open(newOiv);
        var result = new OivDiffResult { Old = a.Path, New = b.Path };
        var oa = a.Entries.Where(e => !e.FullName.EndsWith('/')).ToDictionary(e => e.FullName[a.Root.Length..].Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
        var ob = b.Entries.Where(e => !e.FullName.EndsWith('/')).ToDictionary(e => e.FullName[b.Root.Length..].Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
        foreach (var name in ob.Keys.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (name.Equals("assembly.xml", StringComparison.OrdinalIgnoreCase)) continue;
            if (!oa.TryGetValue(name, out var x)) { result.Added.Add((name, ob[name].LastWriteTime)); continue; }
            var y = ob[name];
            // a different CRC or size settles it from the central directory; equal ones are confirmed byte for byte
            if (x.Crc32 == y.Crc32 && x.Length == y.Length && SameContent(x, y)) result.Same++;
            else result.Changed.Add((name, x.LastWriteTime, y.LastWriteTime));
        }
        foreach (var name in oa.Keys.Where(k => !ob.ContainsKey(k) && !k.Equals("assembly.xml", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase))
            result.Removed.Add(name);
        result.AssemblyDiff.AddRange(LineDiff(a.AssemblyText, b.AssemblyText));
        return result;
    }

    static bool SameContent(System.IO.Compression.ZipArchiveEntry x, System.IO.Compression.ZipArchiveEntry y)
    {
        using var sa = x.Open();
        using var sb = y.Open();
        var ba = new byte[1 << 20];
        var bb = new byte[1 << 20];
        while (true)
        {
            int na = Fill(sa, ba), nb = Fill(sb, bb);
            if (na != nb || !ba.AsSpan(0, na).SequenceEqual(bb.AsSpan(0, nb))) return false;
            if (na == 0) return true;
        }
    }

    static int Fill(Stream s, byte[] buf)
    {
        int got = 0, n;
        while (got < buf.Length && (n = s.Read(buf, got, buf.Length - got)) > 0) got += n;
        return got;
    }

    /// <summary>A minimal unified-style diff: "-" lines only in the old text, "+" lines only in the new, in order (LCS).</summary>
    public static List<string> LineDiff(string oldText, string newText)
    {
        var a = oldText.Replace("\r\n", "\n").Split('\n');
        var b = newText.Replace("\r\n", "\n").Split('\n');
        // trim the common head and tail so the LCS table stays small for big assembly.xml files
        int start = 0;
        while (start < a.Length && start < b.Length && a[start] == b[start]) start++;
        int endA = a.Length, endB = b.Length;
        while (endA > start && endB > start && a[endA - 1] == b[endB - 1]) { endA--; endB--; }
        var x = a[start..endA];
        var y = b[start..endB];
        if ((long)x.Length * y.Length > 4_000_000)
        {
            // too different for a line-by-line table: list what's gone and what's new
            var inB = y.ToHashSet();
            var inA = x.ToHashSet();
            return x.Where(l => !inB.Contains(l)).Select(l => "-" + l).Concat(y.Where(l => !inA.Contains(l)).Select(l => "+" + l)).ToList();
        }
        var lcs = new int[x.Length + 1, y.Length + 1];
        for (int i = x.Length - 1; i >= 0; i--)
            for (int j = y.Length - 1; j >= 0; j--)
                lcs[i, j] = x[i] == y[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var outLines = new List<string>();
        int p = 0, q = 0;
        while (p < x.Length || q < y.Length)
        {
            if (p < x.Length && q < y.Length && x[p] == y[q]) { p++; q++; }
            else if (p < x.Length && (q == y.Length || lcs[p + 1, q] >= lcs[p, q + 1])) outLines.Add($"-{start + p + 1}: {x[p++]}");
            else outLines.Add($"+{start + q + 1}: {y[q++]}");
        }
        return outLines;
    }
}
