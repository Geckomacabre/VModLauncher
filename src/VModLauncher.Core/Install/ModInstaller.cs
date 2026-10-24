using VModLauncher.Core.Game;
using VModLauncher.Core.Library;

namespace VModLauncher.Core.Install;

public sealed record InstallProgress(string Stage, double? Fraction = null, string? Detail = null);

public sealed class InstallBlockedException(string message) : Exception(message);

/// <summary>Reports on the calling thread. Progress&lt;T&gt; hops through the thread pool, so a nested one arrives out of order.</summary>
public sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

/// <summary>Runs a plan through a Journal, so a failed install rolls back and every install can be undone.</summary>
public sealed class ModInstaller
{
    /// <summary>Runs an .oiv (file or unpacked folder) through the journal. Set by the OIV module.</summary>
    public static Action<Journal, string, IProgress<string>?>? OivRunner { get; set; }

    readonly GameInstall game;
    readonly LauncherPaths paths;
    readonly ModLibrary library;
    readonly RpfService rpf;

    public ModInstaller(GameInstall game, LauncherPaths paths, ModLibrary library)
    {
        this.game = game;
        this.paths = paths;
        this.library = library;
        rpf = new RpfService(game);
    }

    /// <summary>Messages about files the last uninstall kept aside (changed after install).</summary>
    public List<string> LastNotes { get; } = new();

    public void Preflight(InstallPlan? plan = null)
    {
        if (GameLauncher.IsGameRunning(game))
            throw new InstallBlockedException($"{game.Name} is running. Close the game first: its files are locked while it plays.");
        if (!GameLauncher.AreModsEnabled(game))
            throw new InstallBlockedException("Mods are switched OFF (parked for GTA Online). Switch them ON on the Home page first, then try again.");
        if (!game.IsWritable())
            throw new InstallBlockedException($"Windows won't let the launcher write to {game.Root}. Use \"Fix folder access\" on the Home page (it asks for administrator rights once).");
        var block = plan?.Warnings.FirstOrDefault(w => w.Level == WarningLevel.Block);
        if (block != null) throw new InstallBlockedException(block.Message);
    }

    /// <summary>Saves the journal at least once a second while it grows, so a crash loses almost nothing.</summary>
    Action Saver()
    {
        var last = DateTime.UtcNow;
        return () =>
        {
            if ((DateTime.UtcNow - last).TotalMilliseconds < 1000) return;
            last = DateTime.UtcNow;
            library.Save();
        };
    }

    public InstalledMod Install(InstallPlan plan, InstalledMod mod, IProgress<InstallProgress>? progress = null)
    {
        // the check runs after taking the archive lock: a long scan could hold it while the game starts
        using var session = RpfService.Session(game);
        Preflight(plan);

        // what a failed re-enable / update must put back
        var existing = library.Find(mod.Id);
        (bool, Dictionary<string, string>, List<string>, Dictionary<string, string>)? before =
            existing == null ? null : (existing.Enabled, existing.Choices, existing.DisabledOps, existing.OpTargets);

        if (mod.Entries.Count > 0)
        {
            // an earlier uninstall left changes behind: finish removing them first
            var leftovers = new Journal(game, rpf, mod, paths.Backups, library).UndoAll();
            if (leftovers.Count > 0)
                throw new InstallBlockedException($"{mod.Name} still has {leftovers.Count} change(s) the launcher couldn't remove ({leftovers[0]}). Close whatever is using them and remove it from Library first.");
        }

        mod.GameId = game.Id;
        mod.Summary = plan.Summary;
        mod.Enabled = true;
        mod.NeedsCleanup = false;
        mod.InstalledAt = DateTime.UtcNow;
        lock (library.Gate) mod.Entries.Clear();
        mod.Choices = plan.Variants.ToDictionary(v => v.Id, v => v.Selected);
        mod.DisabledOps = plan.Ops.Where(o => o.Kind != OpKind.Skip && !o.Enabled).Select(o => o.SourceRel).ToList();
        library.Upsert(mod);

        var journal = new Journal(game, rpf, mod, paths.Backups, library, onChange: Saver());
        var log = new InlineProgress<string>(s => progress?.Report(new InstallProgress("Installing", null, s)));
        journal.Log = log;

        var ops = plan.ActiveOps.OrderBy(o => o.Kind switch
        {
            OpKind.CopyFile => 0, OpKind.SaveGame => 1, OpKind.DlcPack => 2, OpKind.RpfFile => 3, OpKind.Oiv => 4, _ => 5,
        }).ToList();

        try
        {
            for (int i = 0; i < ops.Count; i++)
            {
                var op = ops[i];
                progress?.Report(new InstallProgress("Installing", (double)i / Math.Max(1, ops.Count), op.SourceRel));
                Apply(journal, op, progress);
            }
        }
        catch (Exception ex)
        {
            progress?.Report(new InstallProgress("Rolling back", null, ex.Message));
            var problems = journal.UndoAll();
            if (problems.Count > 0)
            {
                // keep the record: its remaining changes are what a retry needs
                mod.NeedsCleanup = true;
                library.Upsert(mod);
                throw new InvalidOperationException($"Install failed ({ex.Message}) and {problems.Count} change(s) couldn't be rolled back: {string.Join("; ", problems.Take(3))}. The mod stays in Library so you can remove it.", ex);
            }
            if (before != null)
            {
                // a re-enable or update of a mod the player still has: it goes back to how it was
                (mod.Enabled, mod.Choices, mod.DisabledOps, mod.OpTargets) = before.Value;
                library.Upsert(mod);
            }
            else
            {
                library.Remove(mod);
            }
            throw;
        }

        progress?.Report(new InstallProgress("Done", 1));
        // exactly what went where, so turning the mod back on replays it (game-scan placements, hand placements)
        mod.OpTargets = new Dictionary<string, string>();
        foreach (var op in ops) mod.OpTargets.TryAdd(op.SourceRel, $"{op.Kind}|{op.Target}");
        library.Upsert(mod);
        // remember which game update.rpf the mods copy came from, so a Rockstar update is spotted exactly
        if (mod.Entries.Any(e => e.Type == JournalEntry.RpfCopy && e.Path.Equals(@"mods\update\update.rpf", StringComparison.OrdinalIgnoreCase)))
            Features.GameState.RecordModsCopy(paths, game);
        return mod;
    }

    void Apply(Journal journal, InstallOp op, IProgress<InstallProgress>? progress)
    {
        switch (op.Kind)
        {
            case OpKind.CopyFile:
                if (ModAnalyzer.ArgFiles.Contains(op.Target) && File.Exists(game.Resolve(op.Target)))
                {
                    // args.txt / commandline.txt: add the mod's options to the player's instead of replacing them
                    var current = File.ReadAllText(game.Resolve(op.Target));
                    var have = current.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var add = File.ReadAllText(op.Source).Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Where(a => have.Add(a)).ToList();
                    if (add.Count == 0) break;
                    var merged = current.TrimEnd() + (current.Trim().Length > 0 ? Environment.NewLine : "") + string.Join(Environment.NewLine, add) + Environment.NewLine;
                    journal.WriteFile(op.Target, System.Text.Encoding.UTF8.GetBytes(merged));
                    break;
                }
                journal.CopyFile(op.Source, op.Target);
                break;

            case OpKind.SaveGame:
                journal.CopyExternal(op.Source, Path.Combine(SaveProfileDir(), op.Target));
                break;

            case OpKind.DlcPack:
            {
                var target = DlcListTarget.For(game);
                if (target.Kind == DlcListTarget.ModsUpdateRpf)
                {
                    // first add-on pack on this game: copy update.rpf into mods (GBs) with visible progress
                    journal.ModsArchive(@"update\update.rpf",
                        new InlineProgress<double>(f => progress?.Report(new InstallProgress("Copying update.rpf into mods", f))));
                }
                journal.CopyFile(op.Source, Path.Combine(target.PacksFolder, op.Target, "dlc.rpf"));
                journal.AddDlcItem(target, DlcList.ItemFor(op.Target));
                break;
            }

            case OpKind.RpfFile:
            {
                var (archive, inner) = ModAnalyzer.SplitRpfTarget(op.Target);
                journal.WriteRpf(archive, inner, File.ReadAllBytes(op.Source),
                    new InlineProgress<double>(f => progress?.Report(new InstallProgress($"Copying {Path.GetFileName(archive)} into mods", f))));
                break;
            }

            case OpKind.Oiv:
            {
                if (OivRunner == null) throw new NotSupportedException("OIV packages aren't supported in this build.");
                var before = game.HasOnigiri ? DlcItemsInUpdateRpf(journal) : null;
                OivRunner(journal, op.Source, journal.Log);
                if (before != null) MirrorToOnigiri(journal, before);
                break;
            }
        }
    }

    static HashSet<string> DlcItemsInUpdateRpf(Journal journal)
    {
        var bytes = journal.ReadRpf(@"update\update.rpf", DlcList.InnerPath);
        return bytes == null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : DlcList.Items(DlcList.Decode(bytes, out _)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// OIV packages register their packs in update.rpf (as OpenIV does), but with NaturalVision's
    /// onigiri.asi the game reads onigiri's own dlclist instead. Copy new entries (and their pack
    /// folders) across so the packs actually load.
    /// </summary>
    void MirrorToOnigiri(Journal journal, HashSet<string> before)
    {
        var onigiri = DlcListTarget.For(game);
        foreach (var item in DlcItemsInUpdateRpf(journal).Where(i => !before.Contains(i)))
        {
            var m = System.Text.RegularExpressions.Regex.Match(item, @"^dlcpacks:/([^/]+)/?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var src = Path.Combine(game.ModsDlcPacksDir, m.Groups[1].Value);
                var dst = Path.Combine(game.OnigiriDlcPacksDir, m.Groups[1].Value);
                if (Directory.Exists(src) && !Directory.Exists(dst))
                    foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
                        journal.CopyFile(f, game.ToRelative(Path.Combine(dst, Path.GetRelativePath(src, f))));
            }
            journal.AddDlcItem(onigiri, item);
        }
    }

    string SaveProfileDir()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var candidates = game.Edition == GameEdition.Enhanced
            ? new[] { @"Rockstar Games\GTAV Enhanced\Profiles", @"Rockstar Games\GTA V Enhanced\Profiles", @"Rockstar Games\GTA V\Profiles" }
            : new[] { @"Rockstar Games\GTA V\Profiles" };
        foreach (var c in candidates)
        {
            var dir = Path.Combine(docs, c);
            if (!Directory.Exists(dir)) continue;
            var profile = new DirectoryInfo(dir).GetDirectories().OrderByDescending(d => d.LastWriteTimeUtc).FirstOrDefault();
            if (profile != null) return profile.FullName;
        }
        throw new InstallBlockedException("No GTA V save profile found. Start story mode once so the game creates one, then install again.");
    }

    /// <summary>
    /// Undoes the mod. Anything that couldn't be undone (a locked file) stays on the record with its
    /// backup and NeedsCleanup set, so the player can retry; nothing is thrown away.
    /// </summary>
    public List<string> Uninstall(InstalledMod mod, bool keepRecord = false)
    {
        using var session = RpfService.Session(game);
        Preflight();
        var journal = new Journal(game, rpf, mod, paths.Backups, library, onChange: Saver());
        var problems = journal.UndoAll();
        LastNotes.Clear();
        LastNotes.AddRange(journal.Notes);
        if (problems.Count > 0)
        {
            mod.NeedsCleanup = true;
            if (keepRecord) mod.Enabled = false;
            library.Upsert(mod);
            return problems;
        }
        mod.NeedsCleanup = false;
        if (keepRecord)
        {
            mod.Enabled = false;
            library.Upsert(mod);
        }
        else
        {
            library.Remove(mod);
            if (mod.ArchivePath != null && mod.ArchivePath.StartsWith(paths.Payloads, StringComparison.OrdinalIgnoreCase))
            {
                try { Directory.Delete(Path.GetDirectoryName(mod.ArchivePath)!, recursive: true); } catch { }
            }
        }
        return problems;
    }
}
