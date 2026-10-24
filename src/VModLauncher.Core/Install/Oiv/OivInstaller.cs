using System.Xml.Linq;
using CodeWalker.GameFiles;

namespace VModLauncher.Core.Install.Oiv;

/// <summary>
/// Runs an OIV's &lt;content&gt; steps through the <see cref="Journal"/>, so every change can be undone.
/// Installs to the mods folder the way OpenIV does: archive edits go to mods\ copies, and loose
/// game-data files (update\, x64*, common\, *.rpf, dlcpacks) land under mods\ too; scripts, plugins
/// and root files (.asi, .ini, .dll) go to the game folder.
/// </summary>
public sealed class OivInstaller
{
    readonly Journal journal;
    IProgress<string>? log;

    /// <summary>Non-fatal problems (missing sources, XPaths that matched nothing, unknown steps).</summary>
    public List<string> Warnings { get; } = new();

    public OivInstaller(Journal journal) => this.journal = journal;

    public void Install(OivPackage pkg, IProgress<string>? log = null)
    {
        this.log = log;
        if (pkg.Content.Descendants().Any(e => e.Name.LocalName.Equals("archive", StringComparison.OrdinalIgnoreCase)))
        {
            // Rockstar's nested archives (scaleform_*.rpf, ...) stay NG-encrypted inside an OPEN update.rpf,
            // so the keys are needed even when the top archive is OPEN.
            try
            {
                journal.Rpf.EnsureKeys();
                // GTA5Hash copies the lookup table in its static constructor; if anything touched it before
                // the keys were loaded it holds null for good, and every NG archive then fails to open.
                GTA5Hash.LUT ??= GTA5Keys.PC_LUT;
            }
            catch (Exception ex) { Warn("Couldn't load the game's archive keys: " + ex.Message); }
        }

        foreach (var step in pkg.Content.Elements())
        {
            var kind = step.Name.LocalName.ToLowerInvariant();
            switch (kind)
            {
                case "add":
                    AddLoose(pkg, step);
                    break;
                case "delete":
                    journal.DeleteFile(GameTarget(step.Value));
                    break;
                case "archive":
                    Archive(pkg, step, top: null, parentInner: "");
                    break;
                case "xml":
                case "text":
                    EditLoose(step, kind);
                    break;
                default:
                    Warn($"Unknown step <{step.Name.LocalName}>, skipped");
                    break;
            }
        }
    }

    // ---------------------------------------------------------------- paths

    public static string Normalize(string p) => p.Replace('/', '\\').Trim().Trim('\\');

    /// <summary>
    /// Where a loose file from a package goes. Game data (update\, x64*, common\, *.rpf, dlcpacks)
    /// is redirected into mods\, as OpenIV does when installing to the mods folder.
    /// </summary>
    public static string GameTarget(string target)
    {
        var rel = Normalize(target);
        var lower = rel.ToLowerInvariant();
        if (lower.StartsWith("mods\\")) return rel;
        bool gameData = lower.StartsWith("update\\") || lower == "update"
                        || lower.StartsWith("x64")
                        || lower.StartsWith("common\\")
                        || lower.EndsWith(".rpf")
                        || lower.StartsWith("dlcpacks\\") || lower.Contains("\\dlcpacks\\");
        return gameData ? "mods\\" + rel : rel;
    }

    static string InnerJoin(string prefix, string path)
    {
        var p = path.Replace('\\', '/').Trim().Trim('/');
        return prefix.Length == 0 ? p : prefix + "/" + p;
    }

    string? SourceFile(OivPackage pkg, XElement step)
    {
        var src = OivPackage.Attr(step, "source");
        if (string.IsNullOrWhiteSpace(src)) { Warn("<add> without source, skipped"); return null; }
        var full = Path.GetFullPath(Path.Combine(pkg.ContentRoot, Normalize(src)));
        if (!full.StartsWith(Path.GetFullPath(pkg.ContentRoot), StringComparison.OrdinalIgnoreCase))
        {
            Warn($"Source outside the package: {src}, skipped");
            return null;
        }
        if (File.Exists(full)) return full;
        Warn($"Package file missing: content\\{Normalize(src)}, skipped");
        return null;
    }

    // ---------------------------------------------------------------- loose steps

    void AddLoose(OivPackage pkg, XElement step)
    {
        var src = SourceFile(pkg, step);
        if (src == null) return;
        var target = step.Value.Trim();
        if (target.Length == 0) target = Normalize(OivPackage.Attr(step, "source")!);
        journal.CopyFile(src, GameTarget(target));
    }

    void EditLoose(XElement step, string kind)
    {
        var path = OivPackage.Attr(step, "path");
        if (string.IsNullOrWhiteSpace(path)) { Warn($"<{kind}> without path, skipped"); return; }
        var rel = GameTarget(path);
        var data = journal.ReadGameFile(rel);
        if (data == null && rel.StartsWith("mods\\", StringComparison.OrdinalIgnoreCase))
            data = journal.ReadGameFile(rel.Substring(5)); // edit a copy of the original into mods\
        if (data == null) { Warn($"File not found for <{kind}>: {path}"); return; }

        var result = Apply(kind, data, step, path);
        if (ReferenceEquals(result, data)) return;
        if (rel.EndsWith(@"onigiri\common\data\dlclist.xml", StringComparison.OrdinalIgnoreCase) && File.Exists(journal.Game.Resolve(rel)))
        {
            DlcListDiff(new DlcListTarget(DlcListTarget.Onigiri, journal.Game.Resolve(rel)), data, result);
            return;
        }
        journal.WriteFile(rel, result);
    }

    /// <summary>
    /// dlclist.xml is shared by every mod, and the launcher edits it item by item. So a package's edit is
    /// journaled the same way (items added / removed), not as a whole-file replacement that the next
    /// add-on install would make impossible to undo.
    /// </summary>
    void DlcListDiff(DlcListTarget target, byte[] before, byte[] after)
    {
        var old = DlcList.Items(DlcList.Decode(before, out _)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = DlcList.Items(DlcList.Decode(after, out _));
        var nowSet = now.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in now.Where(i => !old.Contains(i)).Distinct(StringComparer.OrdinalIgnoreCase)) journal.AddDlcItem(target, item);
        foreach (var item in old.Where(i => !nowSet.Contains(i))) journal.RemoveDlcItem(target, item);
    }

    byte[] Apply(string kind, byte[] data, XElement step, string path) =>
        kind == "xml"
            ? OivEdits.ApplyXml(data, step, w => Warn($"{path}: {w}"))
            : OivEdits.ApplyText(data, step, w => Warn($"{path}: {w}"));

    // ---------------------------------------------------------------- archives

    void Archive(OivPackage pkg, XElement archive, string? top, string parentInner)
    {
        var path = OivPackage.Attr(archive, "path");
        if (string.IsNullOrWhiteSpace(path)) { Warn("<archive> without path, skipped"); return; }
        bool create = string.Equals(OivPackage.Attr(archive, "createIfNotExist"), "true", StringComparison.OrdinalIgnoreCase);

        string inner;
        if (top == null)
        {
            top = Normalize(path);
            inner = "";
            if (!TopArchiveExists(top))
            {
                if (!create) { Warn($"Archive not found: {top} (its steps skipped)"); return; }
                journal.CreateEmptyArchive(top);
                Log($"created {top}");
            }
        }
        else
        {
            inner = InnerJoin(parentInner, path);
            if (!NestedArchiveExists(top, inner))
            {
                if (!create) { Warn($"Archive not found: {top} > {inner} (its steps skipped)"); return; }
                journal.WriteRpf(top, inner, EmptyRpfBytes(Path.GetFileName(inner)));
                Log($"created {top} > {inner}");
            }
        }

        foreach (var step in archive.Elements())
        {
            var kind = step.Name.LocalName.ToLowerInvariant();
            switch (kind)
            {
                case "add":
                {
                    var src = SourceFile(pkg, step);
                    if (src == null) break;
                    var target = step.Value.Trim();
                    if (target.Length == 0) target = Path.GetFileName(src);
                    journal.WriteRpf(top, InnerJoin(inner, target), File.ReadAllBytes(src));
                    break;
                }
                case "delete":
                    journal.DeleteRpf(top, InnerJoin(inner, step.Value));
                    break;
                case "xml":
                case "text":
                {
                    var p = OivPackage.Attr(step, "path");
                    if (string.IsNullOrWhiteSpace(p)) { Warn($"<{kind}> without path, skipped"); break; }
                    var entry = InnerJoin(inner, p);
                    var data = journal.ReadRpf(top, entry);
                    if (data == null) { Warn($"File not found for <{kind}>: {top} > {entry}"); break; }
                    var result = Apply(kind, data, step, $"{top} > {entry}");
                    if (ReferenceEquals(result, data)) break;
                    var t = Normalize(top);
                    if (entry.Equals(DlcList.InnerPath, StringComparison.OrdinalIgnoreCase)
                        && (t.Equals(@"update\update.rpf", StringComparison.OrdinalIgnoreCase) || t.Equals(@"mods\update\update.rpf", StringComparison.OrdinalIgnoreCase)))
                    {
                        DlcListDiff(new DlcListTarget(DlcListTarget.ModsUpdateRpf, journal.Game.ModsUpdateRpf), data, result);
                        break;
                    }
                    journal.WriteRpf(top, entry, result);
                    break;
                }
                case "archive":
                    Archive(pkg, step, top, inner);
                    break;
                default:
                    Warn($"Unknown step <{step.Name.LocalName}> in {top}, skipped");
                    break;
            }
        }
    }

    bool TopArchiveExists(string top)
    {
        var game = journal.Game;
        var mods = game.Resolve(Journal.ModsRel(top));
        if (File.Exists(mods)) return true;
        var rel = top.StartsWith("mods\\", StringComparison.OrdinalIgnoreCase) ? top.Substring(5) : top;
        return File.Exists(game.Resolve(rel));
    }

    bool NestedArchiveExists(string top, string inner)
    {
        var game = journal.Game;
        var mods = game.Resolve(Journal.ModsRel(top));
        var rel = top.StartsWith("mods\\", StringComparison.OrdinalIgnoreCase) ? top.Substring(5) : top;
        var physical = File.Exists(mods) ? mods : game.Resolve(rel);
        if (!File.Exists(physical)) return false;
        try
        {
            var rpf = journal.Rpf.Open(physical);
            var loc = journal.Rpf.Locate(rpf, inner, createDirs: false);
            return loc.Entry != null;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    /// <summary>An empty OPEN RPF7, built on disk by CodeWalker and read back as bytes.</summary>
    static byte[] EmptyRpfBytes(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vml-rpf-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            RpfFile.CreateNew(dir, name, RpfEncryption.OPEN);
            return File.ReadAllBytes(Path.Combine(dir, name));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    void Warn(string message)
    {
        Warnings.Add(message);
        log?.Report("warning: " + message);
    }

    void Log(string message) => log?.Report(message);
}
