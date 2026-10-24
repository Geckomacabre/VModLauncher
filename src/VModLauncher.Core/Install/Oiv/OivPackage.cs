using System.Xml.Linq;
using VModLauncher.Core.Game;

namespace VModLauncher.Core.Install.Oiv;

/// <summary>
/// An OpenIV package (.oiv): a zip holding assembly.xml, an optional icon.png and a content\ folder.
/// Reads package formats 1.x and 2.0 / 2.1 / 2.2 as OpenIV writes them.
/// </summary>
public sealed class OivPackage : IDisposable
{
    /// <summary>The .oiv file or folder that was opened.</summary>
    public string SourcePath { get; private init; } = "";
    /// <summary>Folder holding assembly.xml.</summary>
    public string PackageRoot { get; private init; } = "";
    /// <summary>Folder that add steps' source paths are relative to (PackageRoot\content).</summary>
    public string ContentRoot { get; private init; } = "";
    /// <summary>The package element's version attribute ("2.1").</summary>
    public string FormatVersion { get; private init; } = "";
    public string? Id { get; private init; }
    /// <summary>The package element's target attribute ("Five" for GTA V).</summary>
    public string? Target { get; private init; }

    public string Name { get; private init; } = "";
    public int Major { get; private init; }
    public int Minor { get; private init; }
    public string? Tag { get; private init; }
    /// <summary>"2.6 LEGACY 2.6.0"-style text: major.minor plus the tag.</summary>
    public string VersionText => Tag is { Length: > 0 } ? $"{Major}.{Minor} {Tag}" : $"{Major}.{Minor}";
    public string Author { get; private init; } = "";
    public string? AuthorLink { get; private init; }
    public string Description { get; private init; } = "";
    public string? LargeDescription { get; private init; }
    public string? Licence { get; private init; }
    public byte[]? Icon { get; private init; }
    /// <summary>Edition the package says it's for (from target/name/tag), null when it doesn't say.</summary>
    public GameEdition? TargetEdition { get; private init; }

    /// <summary>The raw &lt;content&gt; element (namespace-free, original casing).</summary>
    public XElement Content { get; private init; } = new("content");

    /// <summary>True when Open extracted the .oiv into a folder that Dispose deletes.</summary>
    public bool OwnsExtraction { get; private init; }

    public static OivPackage Open(string oivFileOrExtractedFolder, string? extractTo = null)
    {
        var path = Path.GetFullPath(oivFileOrExtractedFolder);
        string root;
        bool owns = false;
        if (Directory.Exists(path))
        {
            root = FindAssemblyFolder(path) ?? throw new InvalidDataException("No assembly.xml in " + path);
        }
        else if (File.Exists(path))
        {
            var dest = extractTo ?? Path.Combine(Path.GetTempPath(), "vml-oiv-" + Guid.NewGuid().ToString("N")[..10]);
            owns = extractTo == null;
            ArchiveExtractor.Extract(path, dest);
            root = FindAssemblyFolder(dest) ?? throw new InvalidDataException("No assembly.xml in " + path);
        }
        else
        {
            throw new FileNotFoundException("OIV package not found", path);
        }

        var assemblyFile = Directory.EnumerateFiles(root)
            .First(f => Path.GetFileName(f).Equals("assembly.xml", StringComparison.OrdinalIgnoreCase));
        var doc = LoadLenient(assemblyFile);
        var pkg = doc.Root ?? throw new InvalidDataException("assembly.xml is empty");
        if (!pkg.Name.LocalName.Equals("package", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("assembly.xml root is <" + pkg.Name.LocalName + ">, not <package>");

        var meta = Child(pkg, "metadata");
        var version = Child(meta, "version");
        var author = Child(meta, "author");

        var name = Text(Child(meta, "name"));
        var tag = NullIfEmpty(Text(Child(version, "tag")));
        var target = Attr(pkg, "target");

        var contentDir = Directory.EnumerateDirectories(root)
            .FirstOrDefault(d => Path.GetFileName(d).Equals("content", StringComparison.OrdinalIgnoreCase))
            ?? Path.Combine(root, "content");

        byte[]? icon = null;
        var iconFile = Directory.EnumerateFiles(root)
            .FirstOrDefault(f => Path.GetFileName(f).Equals("icon.png", StringComparison.OrdinalIgnoreCase));
        if (iconFile != null) icon = File.ReadAllBytes(iconFile);

        return new OivPackage
        {
            SourcePath = path,
            PackageRoot = root,
            ContentRoot = contentDir,
            FormatVersion = Attr(pkg, "version") ?? "",
            Id = Attr(pkg, "id"),
            Target = target,
            Name = name.Length > 0 ? name : Path.GetFileNameWithoutExtension(path),
            Major = ParseInt(Text(Child(version, "major"))),
            Minor = ParseInt(Text(Child(version, "minor"))),
            Tag = tag,
            Author = Text(Child(author, "displayName")) is { Length: > 0 } dn ? dn : Text(author),
            AuthorLink = NullIfEmpty(Text(Child(author, "actionLink"))) ?? NullIfEmpty(Text(Child(author, "web"))),
            Description = Text(Child(meta, "description")),
            LargeDescription = NullIfEmpty(Text(Child(meta, "largeDescription"))),
            Licence = NullIfEmpty(Text(Child(meta, "licence")) is { Length: > 0 } l ? l : Text(Child(meta, "license"))),
            Icon = icon,
            TargetEdition = GuessEdition(target, name, tag, Path.GetFileName(path)),
            Content = Child(pkg, "content") ?? new XElement("content"),
            OwnsExtraction = owns,
        };
    }

    /// <summary>Name, version, author and target edition from assembly.xml, without unpacking the package.</summary>
    public sealed record Info(string Name, string VersionText, string Author, string Description, GameEdition? TargetEdition);

    public static Info? Peek(string oivFileOrFolder)
    {
        try
        {
            XDocument doc;
            if (Directory.Exists(oivFileOrFolder))
            {
                var root = FindAssemblyFolder(oivFileOrFolder);
                if (root == null) return null;
                doc = LoadLenient(Directory.EnumerateFiles(root).First(f => Path.GetFileName(f).Equals("assembly.xml", StringComparison.OrdinalIgnoreCase)));
            }
            else
            {
                using var zip = System.IO.Compression.ZipFile.OpenRead(oivFileOrFolder);
                var entry = zip.Entries
                    .Where(e => e.Name.Equals("assembly.xml", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(e => e.FullName.Length).FirstOrDefault();
                if (entry == null) return null;
                using var s = entry.Open();
                doc = XDocument.Load(s);
            }
            var pkg = doc.Root;
            if (pkg == null) return null;
            var meta = Child(pkg, "metadata");
            var version = Child(meta, "version");
            var author = Child(meta, "author");
            var name = Text(Child(meta, "name"));
            var tag = NullIfEmpty(Text(Child(version, "tag")));
            var major = ParseInt(Text(Child(version, "major")));
            var minor = ParseInt(Text(Child(version, "minor")));
            return new Info(
                name.Length > 0 ? name : Path.GetFileNameWithoutExtension(oivFileOrFolder),
                tag is { Length: > 0 } ? $"{major}.{minor} {tag}" : $"{major}.{minor}",
                Text(Child(author, "displayName")) is { Length: > 0 } dn ? dn : Text(author),
                Text(Child(meta, "description")),
                GuessEdition(Attr(pkg, "target"), name, tag, Path.GetFileName(oivFileOrFolder)));
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (!OwnsExtraction) return;
        try
        {
            var dir = PackageRoot;
            // PackageRoot may be a subfolder of the temp extraction; delete from the vml-oiv-* folder
            while (dir.Length > 3 && !Path.GetFileName(dir).StartsWith("vml-oiv-", StringComparison.Ordinal))
                dir = Path.GetDirectoryName(dir)!;
            if (Path.GetFileName(dir).StartsWith("vml-oiv-", StringComparison.Ordinal)) Directory.Delete(dir, true);
        }
        catch { }
    }

    /// <summary>True when the folder (or one wrapper folder inside it) holds an OIV's assembly.xml.</summary>
    public static bool LooksLikePackageFolder(string folder) => FindAssemblyFolder(folder) != null;

    // ---------------------------------------------------------------- helpers

    static string? FindAssemblyFolder(string dir)
    {
        bool Has(string d) => Directory.EnumerateFiles(d)
            .Any(f => Path.GetFileName(f).Equals("assembly.xml", StringComparison.OrdinalIgnoreCase));
        if (Has(dir)) return dir;
        // tolerate one wrapper folder (someone zipped the package folder itself)
        var subs = Directory.GetDirectories(dir);
        if (subs.Length == 1 && Has(subs[0])) return subs[0];
        return null;
    }

    /// <summary>Loads XML ignoring namespaces and tolerating a BOM or leading junk before the declaration.</summary>
    internal static XDocument LoadLenient(string file)
    {
        var text = OivText.Decode(File.ReadAllBytes(file), out _, out _);
        var start = text.IndexOf('<');
        if (start > 0) text = text.Substring(start);
        var doc = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
        foreach (var e in doc.Descendants().ToList())
        {
            e.Name = e.Name.LocalName;
            foreach (var a in e.Attributes().Where(a => a.IsNamespaceDeclaration).ToList()) a.Remove();
        }
        return doc;
    }

    internal static XElement? Child(XElement? parent, string name) =>
        parent?.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));

    internal static string? Attr(XElement? e, string name) =>
        e?.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    static string Text(XElement? e) => e == null ? "" : e.Value.Trim();

    static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    static int ParseInt(string s) => int.TryParse(s, out var v) ? v : 0;

    static GameEdition? GuessEdition(params string?[] hints)
    {
        foreach (var h in hints)
        {
            if (string.IsNullOrEmpty(h)) continue;
            if (h.Contains("enhanced", StringComparison.OrdinalIgnoreCase) || h.Contains("gen9", StringComparison.OrdinalIgnoreCase))
                return GameEdition.Enhanced;
            if (h.Contains("legacy", StringComparison.OrdinalIgnoreCase)) return GameEdition.Legacy;
        }
        return null;
    }

    public override string ToString() => $"{Name} {VersionText} by {Author} (format {FormatVersion})";
}
