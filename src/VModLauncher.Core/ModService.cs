using VModLauncher.Core.Game;
using VModLauncher.Core.Install;
using VModLauncher.Core.Library;

namespace VModLauncher.Core;

/// <summary>Download → unpack → plan → install, plus disable / enable / uninstall. What the UI talks to.</summary>
public sealed class ModService
{
    public LauncherPaths Paths { get; }
    public ModLibrary Library { get; }

    public ModService(LauncherPaths paths, ModLibrary library)
    {
        Paths = paths;
        Library = library;
    }

    /// <summary>Unpacks an archive (or reads a single file / folder) into staging and builds a plan.</summary>
    public InstallPlan Prepare(GameInstall game, string sourcePath, string modName, IGameIndex? index = null, IProgress<InstallProgress>? progress = null)
    {
        var staging = Path.Combine(Paths.Staging, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(staging);

        if (Directory.Exists(sourcePath))
        {
            CopyDir(sourcePath, Path.Combine(staging, Path.GetFileName(sourcePath.TrimEnd('\\'))));
        }
        else if (sourcePath.EndsWith(".oiv", StringComparison.OrdinalIgnoreCase) || !ArchiveExtractor.IsArchive(sourcePath))
        {
            // a bare .oiv / .asi / .dll / dlc.rpf: analyse it as a one-file mod
            File.Copy(sourcePath, Path.Combine(staging, Path.GetFileName(sourcePath)));
        }
        else
        {
            progress?.Report(new InstallProgress("Unpacking", 0));
            ArchiveExtractor.Extract(sourcePath, staging, new InlineProgress<double>(f => progress?.Report(new InstallProgress("Unpacking", f))));
            ArchiveExtractor.ExtractNested(staging);
        }

        progress?.Report(new InstallProgress("Working out where everything goes"));
        InstallPlan plan;
        using (RpfService.Session(game)) // reads dlc.rpf names and, for replace mods, the game's own files
            plan = new ModAnalyzer(game, index).Analyze(staging, modName, sourcePath);
        return plan;
    }

    public InstalledMod Install(GameInstall game, InstallPlan plan, InstalledMod meta, IProgress<InstallProgress>? progress = null)
    {
        var installed = new ModInstaller(game, Paths, Library).Install(plan, meta, progress);
        // the mod is in the game now: failing to keep its download only costs the offline re-enable
        try { KeepPayload(installed, plan.ArchivePath); }
        catch (Exception ex) { LastNotes.Add($"Couldn't keep the download for {installed.Name}: {ex.Message}"); }
        Library.Upsert(installed);
        DeleteStaging(plan);
        return installed;
    }

    /// <summary>Notes from the last uninstall / install (files kept aside, a download that couldn't be kept).</summary>
    public List<string> LastNotes { get; } = new();

    /// <summary>Takes the mod out of the game but remembers it (and its archive) so it can come back.</summary>
    public List<string> Disable(GameInstall game, InstalledMod mod)
    {
        var installer = new ModInstaller(game, Paths, Library);
        var problems = installer.Uninstall(mod, keepRecord: true);
        LastNotes.Clear();
        LastNotes.AddRange(installer.LastNotes);
        return problems;
    }

    public InstalledMod Enable(GameInstall game, InstalledMod mod, IGameIndex? index = null, IProgress<InstallProgress>? progress = null)
    {
        if (mod.ArchivePath == null || !File.Exists(mod.ArchivePath))
            throw new InstallBlockedException("The launcher no longer has this mod's download. Install it again from its page.");
        var plan = Prepare(game, mod.ArchivePath, mod.Name, index, progress);
        try
        {
            if (mod.OpTargets.Count > 0) Replay(plan, mod);
            else
            {
                // records from before exact targets were kept: re-apply the choices
                foreach (var (group, option) in mod.Choices)
                {
                    var g = plan.Variants.FirstOrDefault(v => v.Id == group);
                    if (g != null && g.Options.Any(o => o.Id == option)) g.Selected = option;
                }
                foreach (var op in plan.Ops.Where(o => mod.DisabledOps.Contains(o.SourceRel))) op.Enabled = false;
            }
            return new ModInstaller(game, Paths, Library).Install(plan, mod, progress);
        }
        finally
        {
            DeleteStaging(plan);
        }
    }

    /// <summary>Puts every file exactly where it went last time (game-scan and hand placements included).</summary>
    static void Replay(InstallPlan plan, InstalledMod mod)
    {
        // keep the recorded choices too, so the mod's record still says what was picked
        foreach (var (group, option) in mod.Choices)
        {
            var g = plan.Variants.FirstOrDefault(v => v.Id == group);
            if (g != null && g.Options.Any(o => o.Id == option)) g.Selected = option;
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var op in plan.Ops)
        {
            if (mod.OpTargets.TryGetValue(op.SourceRel, out var kt))
            {
                var i = kt.IndexOf('|');
                if (i > 0 && Enum.TryParse<OpKind>(kt[..i], out var kind))
                {
                    op.Kind = kind;
                    op.Target = kt[(i + 1)..];
                    op.Enabled = true;
                    op.Variant = null;
                    seen.Add(op.SourceRel);
                    continue;
                }
            }
            if (op.Kind != OpKind.Skip) op.Enabled = false;
        }
        var missing = mod.OpTargets.Keys.Where(k => !seen.Contains(k)).ToList();
        if (missing.Count > 0)
            throw new InstallBlockedException($"The kept download for {mod.Name} no longer matches what was installed ({missing[0]} is missing). Install it again from its page.");
        // the analysis no longer decides anything: drop "nothing could be placed" style blocks
        plan.Warnings.RemoveAll(w => w.Level == WarningLevel.Block);
    }

    public List<string> Uninstall(GameInstall game, InstalledMod mod)
    {
        // a disabled mod has nothing in the game, unless an earlier removal left changes behind
        if (!mod.Enabled && mod.Entries.Count == 0) return RemoveRecord(mod);
        var installer = new ModInstaller(game, Paths, Library);
        var problems = installer.Uninstall(mod, keepRecord: false);
        LastNotes.Clear();
        LastNotes.AddRange(installer.LastNotes);
        return problems;
    }

    List<string> RemoveRecord(InstalledMod mod)
    {
        Library.Remove(mod);
        if (mod.ArchivePath != null && mod.ArchivePath.StartsWith(Paths.Payloads, StringComparison.OrdinalIgnoreCase))
        {
            try { Directory.Delete(Path.GetDirectoryName(mod.ArchivePath)!, recursive: true); } catch { }
        }
        return new List<string>();
    }

    void KeepPayload(InstalledMod mod, string archivePath)
    {
        if (!string.IsNullOrEmpty(archivePath) && Directory.Exists(archivePath))
        {
            // a dropped folder: keep it zipped so the mod can be turned off and on like any download
            var folderDir = Path.Combine(Paths.Payloads, mod.Id);
            Directory.CreateDirectory(folderDir);
            var zip = Path.Combine(folderDir, Path.GetFileName(archivePath.TrimEnd('\\')) + ".zip");
            if (File.Exists(zip)) File.Delete(zip);
            System.IO.Compression.ZipFile.CreateFromDirectory(archivePath, zip, System.IO.Compression.CompressionLevel.Fastest, includeBaseDirectory: true);
            mod.ArchivePath = zip;
            return;
        }
        if (string.IsNullOrEmpty(archivePath) || !File.Exists(archivePath)) return;
        if (archivePath.StartsWith(Paths.Payloads, StringComparison.OrdinalIgnoreCase)) { mod.ArchivePath = archivePath; return; }
        var dir = Path.Combine(Paths.Payloads, mod.Id);
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, Path.GetFileName(archivePath));
        // our own downloads move; files the player dropped in are copied and left where they were
        if (archivePath.StartsWith(Paths.Downloads, StringComparison.OrdinalIgnoreCase)) File.Move(archivePath, dest, overwrite: true);
        else File.Copy(archivePath, dest, overwrite: true);
        mod.ArchivePath = dest;
    }

    public void DeleteStaging(InstallPlan plan)
    {
        try
        {
            if (plan.StagingDir.StartsWith(Paths.Staging, StringComparison.OrdinalIgnoreCase) && Directory.Exists(plan.StagingDir))
                Directory.Delete(plan.StagingDir, recursive: true);
        }
        catch { }
    }

    static void CopyDir(string from, string to)
    {
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest, true);
        }
    }
}
