using VModLauncher.Core.Game;
using VModLauncher.Core.Install;
using VModLauncher.Core.Library;

namespace VModLauncher.Core.Features;

public sealed record ModConflict(string ModId, string ModName, List<string> Paths);

/// <summary>Which mods change the same files. The later install wins; uninstalling it brings the earlier one back.</summary>
public static class Conflicts
{
    /// <summary>"path" for loose files, "archive|inner" for files inside archives.</summary>
    static IEnumerable<string> Keys(InstalledMod m) =>
        m.Entries.Where(e => e.Type is JournalEntry.File or JournalEntry.RpfEntry)
            .Select(e => e.Type == JournalEntry.RpfEntry ? $"{e.Path}|{(e.Inner ?? "").Replace('\\', '/')}" : e.Path);

    static string Display(string key) => key.Contains('|') ? key.Replace("|", " › ") : key;

    public static List<ModConflict> For(ModLibrary library, InstalledMod mod)
    {
        lock (library.Gate) // journals grow on install threads
        {
            var mine = Keys(mod).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (mine.Count == 0) return new();
            return library.ForGame(mod.GameId)
                .Where(o => o.Id != mod.Id && o.Enabled)
                .Select(o => new ModConflict(o.Id, o.Name, Keys(o).Where(mine.Contains).Distinct(StringComparer.OrdinalIgnoreCase).Select(Display).ToList()))
                .Where(c => c.Paths.Count > 0)
                .ToList();
        }
    }

    /// <summary>Warnings for a plan that would overwrite files another installed mod put there.</summary>
    public static List<PlanWarning> ForPlan(ModLibrary library, GameInstall game, InstallPlan plan, string? updatingPageUrl = null)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dlcTarget = DlcListTarget.For(game);
        foreach (var op in plan.ActiveOps)
        {
            switch (op.Kind)
            {
                case OpKind.CopyFile:
                    targets.Add(op.Target.Replace('/', '\\'));
                    break;
                case OpKind.DlcPack:
                    targets.Add(Path.Combine(dlcTarget.PacksFolder, op.Target, "dlc.rpf"));
                    break;
                case OpKind.RpfFile:
                {
                    var (archive, inner) = ModAnalyzer.SplitRpfTarget(op.Target);
                    targets.Add($"{Journal.ModsRel(archive)}|{inner.Replace('\\', '/')}");
                    break;
                }
            }
        }
        if (targets.Count == 0) return new();

        static string Norm(string? u) => (u ?? "").Trim().TrimEnd('/').ToLowerInvariant();
        var warnings = new List<PlanWarning>();
        lock (library.Gate)
        foreach (var other in library.ForGame(game.Id).Where(m => m.Enabled))
        {
            if (updatingPageUrl != null && other.PageUrl != null && Norm(other.PageUrl) == Norm(updatingPageUrl)) continue; // its own older version
            var hits = Keys(other).Where(targets.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (hits.Count == 0) continue;
            warnings.Add(new PlanWarning(WarningLevel.Warn,
                $"This replaces {hits.Count} file{(hits.Count == 1 ? "" : "s")} from {other.Name} ({Display(hits[0])}{(hits.Count > 1 ? ", …" : "")}). They come back when you uninstall this."));
        }
        return warnings;
    }
}
