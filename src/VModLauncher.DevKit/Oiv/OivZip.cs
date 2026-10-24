using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace VModLauncher.DevKit.Oiv;

/// <summary>Reads a .oiv straight from its zip, without unpacking it: assembly.xml and the entry list.</summary>
public sealed class OivZip : IDisposable
{
    readonly ZipArchive zip;
    public string Path { get; }
    public XDocument Assembly { get; }
    public string AssemblyText { get; }
    /// <summary>Zip folder that holds assembly.xml ("" normally; one wrapper folder is tolerated).</summary>
    public string Root { get; }

    OivZip(string path, ZipArchive zip, string root, string text)
    {
        Path = path;
        this.zip = zip;
        Root = root;
        AssemblyText = text;
        Assembly = ParseLenient(text);
    }

    public static OivZip Open(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var zip = ZipFile.OpenRead(full);
        try
        {
            var asm = zip.Entries
                .Where(e => e.Name.Equals("assembly.xml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName.Length)
                .FirstOrDefault() ?? throw new InvalidDataException($"{path} has no assembly.xml: not an OIV package");
            var root = asm.FullName[..^asm.Name.Length];
            using var s = asm.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return new OivZip(full, zip, root, Decode(ms.ToArray()));
        }
        catch
        {
            zip.Dispose();
            throw;
        }
    }

    public XElement Package => Assembly.Root!;
    public XElement? Metadata => Child(Package, "metadata");
    public XElement Content => Child(Package, "content") ?? new XElement("content");

    /// <summary>Entries under content\, by their path inside content ('/'-separated, as stored).</summary>
    public IEnumerable<ZipArchiveEntry> ContentEntries =>
        zip.Entries.Where(e => !e.FullName.EndsWith('/') && Norm(e.FullName).StartsWith(Norm(Root + "content/"), StringComparison.OrdinalIgnoreCase));

    public IReadOnlyCollection<ZipArchiveEntry> Entries => zip.Entries;

    public string ContentPathOf(ZipArchiveEntry e) => Norm(e.FullName)[Norm(Root + "content/").Length..];

    public ZipArchiveEntry? FindContent(string sourceAttr)
    {
        var want = Norm(Root + "content/" + sourceAttr.Replace('\\', '/').Trim().TrimStart('/'));
        return zip.Entries.FirstOrDefault(e => Norm(e.FullName).Equals(want, StringComparison.OrdinalIgnoreCase));
    }

    public byte[] Read(ZipArchiveEntry e)
    {
        using var s = e.Open();
        using var ms = new MemoryStream((int)Math.Min(e.Length, int.MaxValue));
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>The first bytes of an entry (resource headers), without reading the whole file.</summary>
    public byte[] Head(ZipArchiveEntry e, int count)
    {
        using var s = e.Open();
        var buf = new byte[count];
        int got = 0, n;
        while (got < count && (n = s.Read(buf, got, count - got)) > 0) got += n;
        return got == count ? buf : buf[..got];
    }

    public void Dispose() => zip.Dispose();

    // ---------------------------------------------------------------- metadata

    public string Name => Text(Child(Metadata, "name"));
    public string Tag => Text(Child(Child(Metadata, "version"), "tag"));
    public string VersionText
    {
        get
        {
            var v = Child(Metadata, "version");
            var mm = $"{Text(Child(v, "major"))}.{Text(Child(v, "minor"))}";
            return Tag.Length > 0 ? $"{mm} {Tag}" : mm;
        }
    }
    public string Author
    {
        get
        {
            var a = Child(Metadata, "author");
            return Text(Child(a, "displayName")) is { Length: > 0 } d ? d : Text(a);
        }
    }
    /// <summary>&lt;gameversion&gt; (Legacy / Enhanced), which CodeWalker's OIV Package Installer reads; null when absent.</summary>
    public string? GameVersion => Child(Metadata, "gameversion") is { } g ? Text(g) : null;
    public string? FormatVersion => Attr(Package, "version");
    public string? Id => Attr(Package, "id");
    public string? Target => Attr(Package, "target");

    // ---------------------------------------------------------------- xml helpers

    internal static XDocument ParseLenient(string text)
    {
        var start = text.IndexOf('<');
        if (start > 0) text = text[start..];
        var doc = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
        foreach (var e in doc.Descendants().ToList())
        {
            e.Name = e.Name.LocalName;
            foreach (var a in e.Attributes().Where(a => a.IsNamespaceDeclaration).ToList()) a.Remove();
        }
        if (doc.Root == null || !doc.Root.Name.LocalName.Equals("package", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("assembly.xml's root isn't <package>");
        return doc;
    }

    /// <summary>UTF-8 (with or without BOM) or UTF-16 by BOM.</summary>
    internal static string Decode(byte[] data)
    {
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) return Encoding.Unicode.GetString(data, 2, data.Length - 2);
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) return Encoding.UTF8.GetString(data, 3, data.Length - 3);
        return Encoding.UTF8.GetString(data);
    }

    public static XElement? Child(XElement? parent, string name) =>
        parent?.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static string? Attr(XElement? e, string name) =>
        e?.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    public static string Text(XElement? e) => e == null ? "" : e.Value.Trim();

    static string Norm(string p) => p.Replace('\\', '/');
}
