using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using VModLauncher.DevKit.Resources;

namespace VModLauncher.DevKit.Oiv;

public enum LintLevel { Info, Warning, Error }

public sealed record Lint(LintLevel Level, string Message)
{
    public override string ToString() => $"{Level.ToString().ToLowerInvariant()}: {Message}";
}

/// <summary>What a package holds and does, plus the mistakes that break installs or updates.</summary>
public sealed class OivReport
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public string VersionText { get; init; } = "";
    public string Author { get; init; } = "";
    public string? GameVersion { get; init; }
    public string? Edition { get; init; }
    public string? FormatVersion { get; init; }
    public string? Id { get; init; }
    public long Size { get; init; }
    public int ContentFiles { get; init; }
    public int LooseAdds { get; init; }
    public int Deletes { get; init; }
    public List<string> Archives { get; } = new();
    public List<string> XmlEdits { get; } = new();
    public List<string> RegisteredPacks { get; } = new();
    public List<string> RaisedValues { get; } = new();
    public List<Lint> Lints { get; } = new();

    public bool HasErrors => Lints.Any(l => l.Level == LintLevel.Error);

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{Name} {VersionText} by {Author}");
        sb.AppendLine($"  {Path}");
        sb.AppendLine($"  {Size:N0} bytes, {ContentFiles} files in content\\, format {FormatVersion ?? "?"}, id {Id ?? "(none)"}");
        sb.AppendLine($"  edition: {Edition ?? "not said"}; <gameversion>: {GameVersion ?? "(none)"}");
        sb.AppendLine($"  steps: {LooseAdds} loose files, {Deletes} deletes, {Archives.Count} archives, {XmlEdits.Count} xml/text edits");
        foreach (var a in Archives) sb.AppendLine("    archive " + a);
        foreach (var x in XmlEdits) sb.AppendLine("    edit " + x);
        if (RegisteredPacks.Count > 0) sb.AppendLine("  registers: " + string.Join(", ", RegisteredPacks));
        if (RaisedValues.Count > 0) sb.AppendLine("  raises: " + string.Join(", ", RaisedValues));
        if (Lints.Count == 0) sb.AppendLine("  no problems found");
        foreach (var l in Lints.OrderByDescending(l => l.Level)) sb.AppendLine("  " + l);
        return sb.ToString();
    }
}

public static class OivInspector
{
    public static OivReport Inspect(string oivPath)
    {
        using var oiv = OivZip.Open(oivPath);
        var content = oiv.Content;
        var contentEntries = oiv.ContentEntries.ToList();
        var edition = GuessEdition(oiv);
        var report = new OivReport
        {
            Path = oiv.Path,
            Name = oiv.Name,
            VersionText = oiv.VersionText,
            Author = oiv.Author,
            GameVersion = oiv.GameVersion,
            Edition = edition,
            FormatVersion = oiv.FormatVersion,
            Id = oiv.Id,
            Size = new FileInfo(oiv.Path).Length,
            ContentFiles = contentEntries.Count,
            LooseAdds = content.Elements().Count(e => Is(e, "add")),
            Deletes = content.Descendants().Count(e => Is(e, "delete")),
        };
        var lints = report.Lints;

        // ---- every <add>: its source is in the package, and no two install to the same place
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Adds(XElement parent, string scope)
        {
            foreach (var add in parent.Elements().Where(e => Is(e, "add")))
            {
                var src = OivZip.Attr(add, "source");
                if (string.IsNullOrWhiteSpace(src)) { lints.Add(new(LintLevel.Error, $"<add> without source in {Scope(scope)}")); continue; }
                var entry = oiv.FindContent(src);
                if (entry == null) lints.Add(new(LintLevel.Error, $"missing from the package: content\\{src.Trim()} (added to {Scope(scope)})"));
                else referenced.Add(oiv.ContentPathOf(entry));
                var target = add.Value.Trim().Length > 0 ? add.Value.Trim() : src.Trim();
                var key = scope + "|" + target.Replace('/', '\\').TrimStart('\\');
                if (targets.TryGetValue(key, out var other) && !other.Equals(src.Trim(), StringComparison.OrdinalIgnoreCase))
                    lints.Add(new(LintLevel.Warning, $"two files install to {Scope(scope)} {target}: {other} and {src.Trim()} (the later one wins)"));
                targets[key] = src.Trim();
            }
            foreach (var arc in parent.Elements().Where(e => Is(e, "archive")))
            {
                var path = OivZip.Attr(arc, "path") ?? "?";
                var inner = scope.Length == 0 ? path : scope + " > " + path;
                var create = OivZip.Attr(arc, "createIfNotExist");
                report.Archives.Add($"{inner} ({arc.Elements().Count(e => Is(e, "add"))} files{(string.Equals(create, "true", StringComparison.OrdinalIgnoreCase) ? ", created if missing" : "")})");
                Adds(arc, inner);
            }
            foreach (var edit in parent.Elements().Where(e => Is(e, "xml") || Is(e, "text")))
            {
                var path = OivZip.Attr(edit, "path") ?? "?";
                report.XmlEdits.Add($"{Scope(scope)} > {path}: {string.Join(", ", edit.Elements().GroupBy(s => s.Name.LocalName).Select(g => $"{g.Count()} {g.Key}"))}");
                ReadRegistration(edit, path, report);
            }
        }
        Adds(content, "");

        var unreferenced = contentEntries.Select(oiv.ContentPathOf).Where(p => !referenced.Contains(p)).ToList();
        if (unreferenced.Count > 0)
            lints.Add(new(LintLevel.Warning, $"{unreferenced.Count} files in content\\ that no step installs (dead weight), e.g. {string.Join(", ", unreferenced.Take(3))}"));

        // ---- the installer has to know the game, or it asks (and a wrong answer installs into the other edition)
        if (report.GameVersion == null)
            lints.Add(new(LintLevel.Info,
                "no <gameversion> in the metadata: CodeWalker's OIV Package Installer asks for the game folder on every install " +
                "(its --game is ignored without --force), so a player with both editions can pick the wrong one"));
        else if (edition != null && !report.GameVersion.Equals(edition, StringComparison.OrdinalIgnoreCase))
            lints.Add(new(LintLevel.Warning, $"<gameversion> says {report.GameVersion} but the name/tag say {edition}"));

        // ---- resources in the other edition's format crash the game
        var wantYtd = (report.GameVersion ?? edition) switch
        {
            var e when string.Equals(e, "Legacy", StringComparison.OrdinalIgnoreCase) => 13,
            var e when string.Equals(e, "Enhanced", StringComparison.OrdinalIgnoreCase) => 5,
            _ => 0,
        };
        var ytds = contentEntries.Where(e => e.Name.EndsWith(".ytd", StringComparison.OrdinalIgnoreCase)).ToList();
        if (ytds.Count > 0)
        {
            var versions = ytds.Select(e => (e, info: ResourceInfo.FromHeader(e.FullName, oiv.Head(e, 16), e.Length))).ToList();
            var byVersion = versions.GroupBy(v => v.info.IsResource ? v.info.Version : -1).ToDictionary(g => g.Key, g => g.Count());
            if (wantYtd != 0)
            {
                var wrong = versions.Where(v => !v.info.IsResource || v.info.Version != wantYtd).ToList();
                if (wrong.Count > 0)
                    lints.Add(new(LintLevel.Error, $"{wrong.Count} .ytd files aren't {report.GameVersion ?? edition}'s format (version {wantYtd}): " +
                                                   string.Join(", ", wrong.Take(3).Select(w => $"{w.e.Name} v{w.info.Version}"))));
            }
            else if (byVersion.Count > 1)
                lints.Add(new(LintLevel.Warning, "texture dictionaries in both formats: " + string.Join(", ", byVersion.Select(kv => $"{kv.Value}x v{kv.Key}"))));
        }

        // ---- one date for every file: Script Hook V .NET's shadow cache keeps running the old scripts after an update
        var dlls = contentEntries.Where(e => e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToList();
        if (contentEntries.Count >= 5 && contentEntries.Select(e => e.LastWriteTime).Distinct().Count() == 1)
            lints.Add(new(LintLevel.Warning,
                $"every file has the same date ({contentEntries[0].LastWriteTime:yyyy-MM-dd HH:mm}): Script Hook V .NET caches scripts by path and date " +
                @"(%LOCALAPPDATA%\assembly\dl3), so players who update keep running the first version they installed. Keep real file dates."));
        else if (dlls.Count >= 2 && dlls.Select(e => e.LastWriteTime).Distinct().Count() == 1 && dlls[0].LastWriteTime.Year <= 1980)
            lints.Add(new(LintLevel.Warning, "the script DLLs carry a placeholder date: updates may keep running cached old scripts"));

        // ---- dlclist.xml: a plain add lists the pack again on every reinstall
        foreach (var edit in content.Descendants().Where(e => Is(e, "xml") && (OivZip.Attr(e, "path") ?? "").EndsWith("dlclist.xml", StringComparison.OrdinalIgnoreCase)))
            foreach (var add in edit.Elements().Where(e => Is(e, "add")))
            {
                var xpath = OivZip.Attr(add, "xpath") ?? "";
                if (add.Elements().Any(i => Is(i, "Item")) && !xpath.Contains("not(", StringComparison.Ordinal) && !xpath.Contains('|'))
                    lints.Add(new(LintLevel.Warning, $"dlclist.xml: <add xpath=\"{xpath}\"> adds {Text(add)} with no guard, so every reinstall lists the pack again " +
                                                     "(vml generates a guarded add: \"registerPacks\" in the project)"));
            }

        // ---- the version in the file name should be the package's
        var fileVersion = Regex.Match(System.IO.Path.GetFileNameWithoutExtension(oiv.Path), @"\d+\.\d+(\.\d+)+").Value;
        if (fileVersion.Length > 0 && !report.VersionText.Contains(fileVersion))
            lints.Add(new(LintLevel.Warning, $"the file name says {fileVersion} but the package says {report.VersionText}"));

        if (oiv.Entries.Any(e => e.FullName.Contains('\\')))
            lints.Add(new(LintLevel.Warning, "zip entry names with backslashes: some installers can't find those files"));
        return report;
    }

    static void ReadRegistration(XElement edit, string path, OivReport report)
    {
        if (path.EndsWith("dlclist.xml", StringComparison.OrdinalIgnoreCase))
            foreach (var item in edit.Descendants().Where(e => Is(e, "Item")))
                report.RegisteredPacks.Add(item.Value.Trim());
        if (path.EndsWith("gameconfig.xml", StringComparison.OrdinalIgnoreCase))
            foreach (var replace in edit.Elements().Where(e => Is(e, "replace")))
            {
                var xpath = OivZip.Attr(replace, "xpath") ?? "";
                var pool = Regex.Match(xpath, @"PoolName='([^']+)'").Groups[1].Value;
                foreach (var v in replace.Elements())
                    report.RaisedValues.Add($"{(pool.Length > 0 ? pool : v.Name.LocalName)} {OivZip.Attr(v, "value")}{(xpath.Contains("@value <") || xpath.Contains("@value &lt;") ? " (only raised)" : "")}");
            }
    }

    static string? GuessEdition(OivZip oiv)
    {
        foreach (var hint in new[] { oiv.GameVersion, oiv.Target, oiv.Name, oiv.Tag, System.IO.Path.GetFileName(oiv.Path) })
        {
            if (string.IsNullOrEmpty(hint)) continue;
            if (hint.Contains("enhanced", StringComparison.OrdinalIgnoreCase) || hint.Contains("gen9", StringComparison.OrdinalIgnoreCase)) return "Enhanced";
            if (hint.Contains("legacy", StringComparison.OrdinalIgnoreCase)) return "Legacy";
        }
        return null;
    }

    static bool Is(XElement e, string name) => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase);
    static string Text(XElement e) => string.Concat(e.Elements().Select(x => x.Value.Trim()));
    static string Scope(string scope) => scope.Length == 0 ? "the game" : scope;
}
