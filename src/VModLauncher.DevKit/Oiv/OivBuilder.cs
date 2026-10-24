using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using VModLauncher.DevKit.Resources;

namespace VModLauncher.DevKit.Oiv;

/// <summary>One file in a package: where it comes from, where it sits in content\, and where it installs.</summary>
/// <param name="Scope">"" for a loose file; for a file inside archives, their paths joined with " > ".</param>
public sealed record PackedFile(string Source, string ContentPath, string Target, string Scope);

public sealed class BuildResult
{
    public required string Output { get; init; }
    public required long Size { get; init; }
    public required string Sha256 { get; init; }
    public required IReadOnlyList<PackedFile> Files { get; init; }
    public required string AssemblyXml { get; init; }
    public List<string> Warnings { get; } = new();

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{Output}");
        sb.AppendLine($"  {Size:N0} bytes, {Files.Count} files ({Files.Count(f => f.Scope.Length > 0)} inside archives), sha256 {Sha256}");
        foreach (var w in Warnings) sb.AppendLine("  warning: " + w);
        return sb.ToString();
    }
}

/// <summary>
/// Builds a .oiv from an <see cref="OivProject"/>. The package is a zip laid out the way OpenIV writes them:
/// assembly.xml and icon.png at the top, the files under content\. Every file keeps its real modification time,
/// which the installers write to disk: Script Hook V .NET caches scripts by path and file time
/// (%LOCALAPPDATA%\assembly\dl3), so packages that stamp every file with one fixed date make updates keep
/// running the first version a player installed.
/// </summary>
public static class OivBuilder
{
    /// <summary>Formats that are already compressed (or archives): stored as they are, like OpenIV's own packages.</summary>
    public static readonly string[] DefaultStored = { ".rpf", ".png", ".jpg", ".jpeg", ".dds", ".mp3", ".ogg", ".zip", ".7z", ".rar", ".oiv", ".gz" };

    /// <summary>What a build would package: every file, the zip entries (one per content path) and assembly.xml.</summary>
    public sealed record BuildPlan(List<PackedFile> Files, List<PackedFile> Entries, string AssemblyXml, List<string> Warnings);

    public static BuildResult Build(OivProject project, string? output = null, IProgress<string>? log = null)
    {
        var (files, unique, xml, warnings) = Plan(project);

        var outPath = Path.GetFullPath(output ?? project.ResolvePath(OutputPattern(project, xml)));
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        var stored = (project.Store ?? DefaultStored.ToList()).Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant()).ToHashSet();
        var icon = project.Icon != null ? project.ResolvePath(project.Icon) : null;
        if (icon != null && !File.Exists(icon)) throw new FileNotFoundException("icon not found", icon);

        var tmp = outPath + ".part";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                Write(zip, "assembly.xml", new UTF8Encoding(false).GetBytes(xml), DateTime.Now, compress: true);
                if (icon != null) Write(zip, "icon.png", icon, compress: false);
                int n = 0;
                foreach (var pf in unique)
                {
                    Write(zip, "content/" + pf.ContentPath, pf.Source, compress: !stored.Contains(Path.GetExtension(pf.Source).ToLowerInvariant()));
                    if (++n % 250 == 0) log?.Report($"{n}/{unique.Count} files");
                }
            }
            File.Move(tmp, outPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }

        var result = new BuildResult
        {
            Output = outPath,
            Size = new FileInfo(outPath).Length,
            Sha256 = Sha256File(outPath),
            Files = files,
            AssemblyXml = xml,
        };
        result.Warnings.AddRange(warnings);
        return result;
    }

    /// <summary>Collects and checks everything without writing a package (also what verify compares against).</summary>
    public static BuildPlan Plan(OivProject project)
    {
        var files = new List<PackedFile>();
        var warnings = new List<string>();
        var content = new StringBuilder();

        foreach (var f in project.Files)
            foreach (var pf in Collect(project, f, scope: "", contentPrefix: null, warnings))
            {
                files.Add(pf);
                content.Append($"    <add source=\"{OivSteps.X(Win(pf.ContentPath))}\">{OivSteps.X(pf.Target)}</add>\n");
            }
        foreach (var a in project.Archives)
            WriteArchive(project, a, "    ", parentScope: "", files, content, warnings, top: true);

        var raise = project.Raise?.ToList() ?? new();
        var packs = project.RegisterPacks ?? new();
        content.Append(OivSteps.Registration(raise, packs, project.ScratchTag ?? OivSteps.DefaultScratch, "    "));
        foreach (var step in project.Steps ?? new())
            content.Append(Indent(project.ResolveText(step)!, "    "));

        var unique = CheckCollisions(files);
        CheckResourceVersions(project, unique);

        var xml = project.AssemblyTemplate != null ? FromTemplate(project, content.ToString()) : Generate(project, content.ToString());
        try { XDocument.Parse(xml); }
        catch (Exception ex) { throw new InvalidDataException("The generated assembly.xml isn't valid XML: " + ex.Message); }
        return new BuildPlan(files, unique, xml, warnings);
    }

    /// <summary>The output file a build of this project writes (its "output" pattern, filled in).</summary>
    public static string OutputPath(OivProject project, BuildPlan plan) => project.ResolvePath(OutputPattern(project, plan.AssemblyXml));

    // ---------------------------------------------------------------- files

    static IEnumerable<PackedFile> Collect(OivProject project, OivFiles f, string scope, string? contentPrefix, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(f.From)) throw new InvalidDataException("A files entry has no \"from\"");
        var src = project.ResolvePath(f.From);
        var to = Clean(project.Expand(f.To ?? ""));
        var contentGiven = f.Content != null ? Clean(project.Expand(f.Content)) : null;
        var excludes = (f.Exclude ?? new()).Select(g => new Glob(project.Expand(g))).ToList();

        if (File.Exists(src))
        {
            var name = Path.GetFileName(src);
            var target = to.Length == 0 || (f.To ?? "").EndsWith('/') || (f.To ?? "").EndsWith('\\') ? Join(to, name) : to;
            var contentPath = contentGiven != null
                ? (f.Content!.EndsWith('/') || f.Content.EndsWith('\\') || contentGiven.Length == 0 ? Join(contentGiven, name) : contentGiven)
                : Join(contentPrefix ?? "", target);
            yield return new PackedFile(src, contentPath, Win(target), scope);
            yield break;
        }
        if (!Directory.Exists(src))
        {
            if (f.Optional) { warnings.Add($"skipped {f.From}: not found (optional)"); yield break; }
            throw new FileNotFoundException($"\"from\": {f.From} doesn't exist", src);
        }

        int count = 0;
        foreach (var rel in Walk(src))
        {
            if (Glob.Any(excludes, rel)) continue;
            count++;
            var target = Join(to, rel);
            var contentPath = Join(contentGiven ?? Join(contentPrefix ?? "", to), rel);
            yield return new PackedFile(Path.Combine(src, rel.Replace('/', '\\')), contentPath, Win(target), scope);
        }
        if (count == 0) warnings.Add($"{f.From}: no files (after the excludes)");
    }

    /// <summary>Files under a folder, '/'-separated: each folder's files in ordinal order, then its subfolders, depth first.</summary>
    public static IEnumerable<string> Walk(string root)
    {
        IEnumerable<string> Inner(string dir, string prefix)
        {
            foreach (var file in Directory.GetFiles(dir).Select(Path.GetFileName).Order(StringComparer.Ordinal))
                yield return prefix + file;
            foreach (var sub in Directory.GetDirectories(dir).Select(Path.GetFileName).Order(StringComparer.Ordinal))
                foreach (var r in Inner(Path.Combine(dir, sub!), prefix + sub + "/"))
                    yield return r;
        }
        return Inner(root, "");
    }

    static void WriteArchive(OivProject project, OivArchive a, string indent, string parentScope, List<PackedFile> files, StringBuilder content, List<string> warnings, bool top)
    {
        var path = Clean(project.Expand(a.Path));
        if (path.Length == 0) throw new InvalidDataException("An archive has no \"path\"");
        if (!path.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase)) warnings.Add($"archive {path} doesn't end in .rpf");
        var scope = parentScope.Length == 0 ? Win(path) : parentScope + " > " + Win(path);
        var prefix = "archives/" + scope.Replace(" > ", "/").Replace('\\', '/').Replace(".rpf", "_rpf", StringComparison.OrdinalIgnoreCase);

        content.Append($"{indent}<archive path=\"{OivSteps.X(Win(path))}\" createIfNotExist=\"{(a.Create ? "True" : "False")}\" type=\"{OivSteps.X(a.Type)}\">\n");
        foreach (var f in a.Files)
            foreach (var pf in Collect(project, f, scope, prefix, warnings))
            {
                files.Add(pf);
                content.Append($"{indent}  <add source=\"{OivSteps.X(Win(pf.ContentPath))}\">{OivSteps.X(pf.Target)}</add>\n");
            }
        foreach (var child in a.Archives)
            WriteArchive(project, child, indent + "  ", scope, files, content, warnings, top: false);
        foreach (var step in a.Steps ?? new())
            content.Append(Indent(project.ResolveText(step)!, indent + "  "));
        content.Append($"{indent}</archive>\n");
    }

    /// <summary>
    /// One zip entry per content path. Two sources for one content path must be the same bytes; two files that
    /// install to the same place in the same archive are an error (the second would silently win).
    /// </summary>
    static List<PackedFile> CheckCollisions(List<PackedFile> files)
    {
        var byContent = new Dictionary<string, PackedFile>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<PackedFile>();
        foreach (var f in files)
        {
            if (byContent.TryGetValue(f.ContentPath, out var seen))
            {
                if (!string.Equals(Path.GetFullPath(seen.Source), Path.GetFullPath(f.Source), StringComparison.OrdinalIgnoreCase) && !SameBytes(seen.Source, f.Source))
                    throw new InvalidDataException($"content\\{Win(f.ContentPath)} would hold two different files: {seen.Source} and {f.Source}");
                continue;
            }
            byContent[f.ContentPath] = f;
            unique.Add(f);
        }
        var clash = files.GroupBy(f => (f.Scope.ToLowerInvariant(), f.Target.ToLowerInvariant()))
            .FirstOrDefault(g => g.Select(x => x.ContentPath.ToLowerInvariant()).Distinct().Count() > 1);
        if (clash != null)
            throw new InvalidDataException($"Two files install to {(clash.Key.Item1.Length > 0 ? clash.First().Scope + " > " : "")}{clash.First().Target}: " +
                                           string.Join(", ", clash.Select(x => x.Source).Distinct()));
        return unique;
    }

    static void CheckResourceVersions(OivProject project, List<PackedFile> files)
    {
        var want = project.EffectiveResourceVersions();
        if (want.Count == 0) return;
        var wrong = new List<string>();
        foreach (var f in files)
        {
            if (!want.TryGetValue(Path.GetExtension(f.Source).ToLowerInvariant(), out var version)) continue;
            var info = ResourceInfo.Read(f.Source);
            if (!info.IsResource) wrong.Add($"{f.Source}: not a resource (no RSC7 header)");
            else if (info.Version != version) wrong.Add($"{f.Source}: resource version {info.Version}, {project.EditionName} needs {version}");
        }
        if (wrong.Count > 0)
            throw new InvalidDataException("Resources in the wrong format (they'd crash the game):\n  " + string.Join("\n  ", wrong.Take(20)) +
                                           (wrong.Count > 20 ? $"\n  ... and {wrong.Count - 20} more" : ""));
    }

    // ---------------------------------------------------------------- assembly.xml

    static string Generate(OivProject p, string content)
    {
        var name = p.Name ?? throw new InvalidDataException("\"name\" is required (or use an assemblyTemplate)");
        var version = p.Version ?? throw new InvalidDataException("\"version\" is required (or use an assemblyTemplate)");
        var (major, minor) = MajorMinor(version);
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        sb.Append($"<package version=\"2.1\" id=\"{OivSteps.X(p.Id ?? StableId(name))}\" target=\"Five\">\n");
        sb.Append("  <metadata>\n");
        sb.Append($"    <name>{OivSteps.X(p.Expand(name))}</name>\n");
        sb.Append($"    <version><major>{major}</major><minor>{minor}</minor><tag>{OivSteps.X(p.Expand(p.Tag ?? version))}</tag></version>\n");
        if (p.GameVersionElement && p.EditionName != "Any") sb.Append($"    <gameversion>{p.EditionName}</gameversion>\n");
        sb.Append("    <author><displayName>").Append(OivSteps.X(p.Expand(p.Author ?? "Unknown"))).Append("</displayName>");
        if (p.AuthorLink != null) sb.Append("<actionLink>").Append(OivSteps.X(p.Expand(p.AuthorLink))).Append("</actionLink>");
        sb.Append("</author>\n");
        sb.Append($"    <description>{Cdata(p.ResolveText(p.Description) ?? "")}</description>\n");
        if (p.LargeDescription != null) sb.Append($"    <largeDescription>{Cdata(p.ResolveText(p.LargeDescription)!)}</largeDescription>\n");
        if (p.Licence != null) sb.Append($"    <licence>{Cdata(p.ResolveText(p.Licence)!)}</licence>\n");
        sb.Append("  </metadata>\n");
        if (p.Colors != null)
        {
            sb.Append("  <colors>\n");
            if (p.Colors.HeaderBackground != null)
                sb.Append($"    <headerBackground useBlackTextColor=\"{(p.Colors.HeaderBlackText ? "true" : "false")}\">{OivSteps.X(p.Colors.HeaderBackground)}</headerBackground>\n");
            if (p.Colors.IconBackground != null) sb.Append($"    <iconBackground>{OivSteps.X(p.Colors.IconBackground)}</iconBackground>\n");
            sb.Append("  </colors>\n");
        }
        sb.Append("  <content>\n").Append(content).Append("  </content>\n");
        sb.Append("</package>\n");
        return sb.ToString();
    }

    const string Marker = "<!-- vml:content -->";

    static string FromTemplate(OivProject p, string content)
    {
        var path = p.ResolvePath(p.AssemblyTemplate!);
        var text = File.ReadAllText(path).Replace("\r\n", "\n");
        var at = text.IndexOf(Marker, StringComparison.Ordinal);
        if (at >= 0)
        {
            var lineStart = text.LastIndexOf('\n', at) + 1;
            var lineEnd = text.IndexOf('\n', at);
            lineEnd = lineEnd < 0 ? text.Length : lineEnd + 1;
            return text[..lineStart] + content + text[lineEnd..];
        }
        var close = text.LastIndexOf("</content>", StringComparison.OrdinalIgnoreCase);
        if (close < 0) throw new InvalidDataException($"{path} has no <content> element");
        var closeLine = text.LastIndexOf('\n', close) + 1;
        return text[..closeLine] + content + text[closeLine..];
    }

    static string OutputPattern(OivProject p, string xml)
    {
        var pattern = p.Output ?? "build/{name}-{version}.oiv";
        var doc = XDocument.Parse(xml);
        var meta = doc.Root?.Element("metadata");
        var name = p.Name ?? meta?.Element("name")?.Value.Trim() ?? "package";
        var tag = p.Tag ?? meta?.Element("version")?.Element("tag")?.Value.Trim() ?? "";
        var version = p.Version;
        if (version == null)
        {
            var inTag = Regex.Match(tag, @"\d+\.\d+(\.\d+)*").Value;
            version = inTag.Length > 0
                ? inTag
                : $"{meta?.Element("version")?.Element("major")?.Value.Trim()}.{meta?.Element("version")?.Element("minor")?.Value.Trim()}";
        }
        return pattern
            .Replace("{name}", FileSafe(p.Expand(name)), StringComparison.OrdinalIgnoreCase)
            .Replace("{version}", FileSafe(version), StringComparison.OrdinalIgnoreCase)
            .Replace("{edition}", p.EditionName, StringComparison.OrdinalIgnoreCase)
            .Replace("{tag}", FileSafe(tag), StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- helpers

    static (int, int) MajorMinor(string version)
    {
        var m = Regex.Match(version.Trim(), @"^(\d+)\.(\d+)");
        if (!m.Success) throw new InvalidDataException($"version \"{version}\" should start with major.minor (\"1.2.0\")");
        return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
    }

    /// <summary>The same id for the same package name, so a new version installs as an update of the old one.</summary>
    public static string StableId(string name)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("vmod-oiv:" + name.Trim().ToLowerInvariant()));
        return new Guid(hash).ToString("B");
    }

    static string Cdata(string text) => "<![CDATA[" + text.Replace("]]>", "]]]]><![CDATA[>") + "]]>";

    static string Indent(string xml, string indent)
    {
        var lines = xml.Replace("\r\n", "\n").Trim('\n').Split('\n');
        var common = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        return string.Concat(lines.Select(l => (l.Trim().Length == 0 ? "" : indent + l[Math.Min(common, l.Length)..]) + "\n"));
    }

    static void Write(ZipArchive zip, string name, byte[] data, DateTime time, bool compress)
    {
        var e = zip.CreateEntry(name, compress ? CompressionLevel.Optimal : CompressionLevel.NoCompression);
        e.LastWriteTime = ZipTime(time);
        using var s = e.Open();
        s.Write(data);
    }

    static void Write(ZipArchive zip, string name, string source, bool compress)
    {
        var e = zip.CreateEntry(name, compress ? CompressionLevel.Optimal : CompressionLevel.NoCompression);
        e.LastWriteTime = ZipTime(File.GetLastWriteTime(source));
        using var s = e.Open();
        using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        src.CopyTo(s, 1 << 20);
    }

    /// <summary>Zip dates start in 1980.</summary>
    static DateTimeOffset ZipTime(DateTime t) => t.Year < 1980 ? new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Local) : t;

    static bool SameBytes(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        return fa.Length == fb.Length && Sha256File(a) == Sha256File(b);
    }

    public static string Sha256File(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    static string Clean(string p) => p.Replace('\\', '/').Trim().Trim('/');

    static string Join(string a, string b) => a.Length == 0 ? b : b.Length == 0 ? a : a.TrimEnd('/') + "/" + b.TrimStart('/');

    static string Win(string p) => p.Replace('/', '\\');

    static string FileSafe(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        return new string(s.Trim().Select(c => bad.Contains(c) ? '-' : c == ' ' ? '-' : c).ToArray());
    }
}
