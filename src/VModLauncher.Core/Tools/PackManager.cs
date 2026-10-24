using System.Text.RegularExpressions;
using VModLauncher.Core.Game;
using VModLauncher.Core.Install;
using VModLauncher.Core.Library;

namespace VModLauncher.Core.Tools;

public sealed record PackManagerOwner(string ModId, string Name);

/// <summary>One add-on pack as the player sees it (see docs/bridge.md, PackRow).</summary>
public sealed record PackManagerRow(
    string Name, string Item, string? Folder, long SizeBytes, string Status, string Source,
    PackManagerOwner? Owner, List<string> SpawnNames);

public sealed record PackManagerProblem(string Level, string Text, string? Fix);

/// <summary>Which dlclist the game actually reads: NaturalVision's (onigiri), the mods copy of update.rpf, or the game's own.</summary>
public sealed record PackManagerList(string Kind, string Path);

public sealed record PackManagerReport(PackManagerList List, List<PackManagerRow> Packs, List<PackManagerProblem> Problems);

/// <summary>
/// Lists every pack in the dlclist the game reads, finds the ones that are broken (files gone, listed
/// twice, on disk but never listed, dropped by NaturalVision's own list) and switches packs on and off.
/// Every change is journaled in one "Pack manager changes" Library record per game, so uninstalling
/// that record puts the lists back.
/// </summary>
public static class PackManager
{
    public const string RecordName = "Pack manager changes";

    public const string StatusOk = "ok", StatusMissing = "missing", StatusDuplicate = "duplicate", StatusDisabled = "disabled",
        StatusNotRegistered = "notRegistered", StatusNotInActiveList = "notInActiveList";
    public const string SourceRockstar = "rockstar", SourceLauncher = "launcher", SourceOther = "other";

    static readonly Regex DlcPacksItem = new(@"^dlcpacks:/([^/]+)/?$", RegexOptions.IgnoreCase);
    static readonly Regex SafeName = new(@"^[A-Za-z0-9_\-]+$");

    const string GameDlcPacks = @"update\x64\dlcpacks";
    const string ModsDlcPacks = @"mods\update\x64\dlcpacks";
    const string OnigiriDlcPacks = @"onigiri\dlcpacks";
    const string OnigiriListRel = @"onigiri\common\data\dlclist.xml";

    // ---------------------------------------------------------------- reading

    /// <summary>Call inside RpfService.Session(game): the game's own update.rpf needs its keys.</summary>
    public static PackManagerReport List(GameInstall game, ModLibrary library)
    {
        var rpf = new RpfService(game);
        var list = ActiveList(game);
        var activeItems = ReadItems(game, rpf, list) ?? new List<string>();
        // with onigiri the game ignores update.rpf's list; what's only there doesn't load
        var updateItems = list.Kind == DlcListTarget.Onigiri ? ReadUpdateItems(game, rpf) : null;

        var record = FindRecord(library, game, RecordName);
        var listRel = game.ToRelative(list.Path); // the journal keeps game-relative paths
        List<JournalEntry> removed;
        lock (library.Gate)
            removed = record?.Entries.Where(e => e.Type == JournalEntry.DlcRemoved && e.Item != null
                                                  && string.Equals(e.Path, listRel, StringComparison.OrdinalIgnoreCase)).ToList()
                      ?? new List<JournalEntry>();

        var rows = new List<PackManagerRow>();
        var problems = new List<PackManagerProblem>();
        var counts = activeItems.GroupBy(i => i, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // pack names the active list points at

        foreach (var item in activeItems.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var row = Row(game, library, list, item);
            if (DlcPacksItem.Match(item) is { Success: true } m) listed.Add(m.Groups[1].Value);
            var status = counts[item] > 1 ? StatusDuplicate : row.Status;
            rows.Add(row with { Status = status });
        }

        // packs the player switched off here earlier
        foreach (var item in removed.Select(e => e.Item!).Distinct(StringComparer.OrdinalIgnoreCase)
                     .Where(i => !counts.ContainsKey(i)))
        {
            var row = Row(game, library, list, item);
            if (row.Status == StatusMissing) continue; // the pack itself is gone since: nothing to switch back on
            rows.Add(row with { Status = StatusDisabled });
            if (DlcPacksItem.Match(item) is { Success: true } m) listed.Add(m.Groups[1].Value);
        }

        // registered in update.rpf, but NaturalVision's list wins and lacks them (the LWE problem)
        var dropped = new List<PackManagerRow>();
        if (updateItems != null)
        {
            foreach (var item in updateItems.Distinct(StringComparer.OrdinalIgnoreCase).Where(i => !counts.ContainsKey(i)))
            {
                if (rows.Any(r => string.Equals(r.Item, item, StringComparison.OrdinalIgnoreCase))) continue; // switched off on purpose
                var row = Row(game, library, list, item) with { Status = StatusNotInActiveList };
                dropped.Add(row);
                rows.Add(row);
                if (DlcPacksItem.Match(item) is { Success: true } m) listed.Add(m.Groups[1].Value);
            }
        }

        // pack folders nobody lists
        foreach (var (folderRel, name) in AddonFolders(game))
        {
            if (listed.Contains(name)) continue;
            if (IsRockstarName(game, name)) continue; // a mods\ copy of one of Rockstar's packs
            var size = SizeOf(game.Resolve(Path.Combine(folderRel, "dlc.rpf")));
            var owner = OwnerOf(library, game, folderRel);
            rows.Add(new PackManagerRow(name, DlcList.ItemFor(name), folderRel, size, StatusNotRegistered,
                owner != null ? SourceLauncher : SourceOther, owner, SpawnNamesOf(library, owner)));
            listed.Add(name);
            problems.Add(new PackManagerProblem("Warn", $"{name} is in {folderRel.Substring(0, folderRel.LastIndexOf('\\'))} but no list loads it, so it never shows up in the game.", "register:" + name));
        }

        // problems the player can fix from the list
        var dupes = counts.Where(kv => kv.Value > 1).Select(kv => kv.Key).ToList();
        if (dupes.Count > 0)
            problems.Insert(0, new PackManagerProblem("Warn",
                $"{dupes.Count} pack{(dupes.Count == 1 ? " is" : "s are")} listed more than once ({string.Join(", ", dupes.Take(3).Select(NameOf))}{(dupes.Count > 3 ? ", …" : "")}). The extra lines can be removed; the first one stays where it is.",
                "duplicates"));
        foreach (var r in rows.Where(r => r.Status == StatusMissing))
            problems.Insert(0, new PackManagerProblem("Warn",
                $"{r.Name} is listed but its files are gone, so it doesn't load. Take it off the list to tidy it up.", "missing:" + r.Item));
        if (dropped.Count > 0)
            problems.Insert(0, new PackManagerProblem("Block",
                $"NaturalVision's own dlclist is the one the game reads, and {dropped.Count} pack{(dropped.Count == 1 ? "" : "s")} from update.rpf's list {(dropped.Count == 1 ? "isn't" : "aren't")} in it ({string.Join(", ", dropped.Take(4).Select(r => r.Name))}{(dropped.Count > 4 ? ", …" : "")}). They don't load until they're added there.",
                "onigiri-sync"));
        if (list.Kind == "game")
            problems.Add(new PackManagerProblem("Info",
                "The game still reads its own dlclist. Any change here first makes the usual copy in the mods folder, which needs OpenIV.asi (Legacy) or OpenRPF (Enhanced) to load.", null));

        // order: problems first, then the player's packs by name, then Rockstar's in list order
        var ordered = rows.Where(r => r.Source != SourceRockstar)
            .OrderBy(r => r.Status == StatusOk ? 1 : 0).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Concat(rows.Where(r => r.Source == SourceRockstar)).ToList();
        return new PackManagerReport(new PackManagerList(list.Kind, game.ToRelative(list.Path)), ordered, problems);
    }

    sealed record ActiveListInfo(string Kind, string Path)
    {
        public DlcListTarget Target => Kind == DlcListTarget.Onigiri
            ? new DlcListTarget(DlcListTarget.Onigiri, Path)
            : new DlcListTarget(DlcListTarget.ModsUpdateRpf, Path);
    }

    static ActiveListInfo ActiveList(GameInstall game)
    {
        if (game.HasOnigiri) return new ActiveListInfo(DlcListTarget.Onigiri, game.OnigiriDlcList);
        if (File.Exists(game.ModsUpdateRpf)) return new ActiveListInfo("mods", game.ModsUpdateRpf);
        return new ActiveListInfo("game", game.GameUpdateRpf);
    }

    /// <summary>Where changes go: onigiri's list, or the mods copy of update.rpf (made on the first change).</summary>
    static DlcListTarget ChangeTarget(GameInstall game) =>
        game.HasOnigiri ? new DlcListTarget(DlcListTarget.Onigiri, game.OnigiriDlcList) : new DlcListTarget(DlcListTarget.ModsUpdateRpf, game.ModsUpdateRpf);

    static List<string>? ReadItems(GameInstall game, RpfService rpf, ActiveListInfo list)
    {
        try
        {
            if (list.Kind == DlcListTarget.Onigiri)
                return File.Exists(list.Path) ? DlcList.Items(DlcList.Decode(File.ReadAllBytes(list.Path), out _)) : null;
            if (!File.Exists(list.Path)) return null;
            var bytes = rpf.TryRead(list.Path, DlcList.InnerPath);
            return bytes == null ? null : DlcList.Items(DlcList.Decode(bytes, out _));
        }
        catch { return null; }
    }

    static List<string>? ReadUpdateItems(GameInstall game, RpfService rpf) =>
        ReadItems(game, rpf, File.Exists(game.ModsUpdateRpf) ? new ActiveListInfo("mods", game.ModsUpdateRpf) : new ActiveListInfo("game", game.GameUpdateRpf));

    static string NameOf(string item) => item.TrimEnd('/').Split('/').Last();

    static PackManagerRow Row(GameInstall game, ModLibrary library, ActiveListInfo list, string item)
    {
        var name = NameOf(item);
        var m = DlcPacksItem.Match(item);
        if (!m.Success)
        {
            // platform:/dlcPacks/..., dlc_patch, ...: Rockstar's own entries
            var rockstarFolder = Path.Combine(GameDlcPacks, name);
            var exists = File.Exists(game.Resolve(Path.Combine(rockstarFolder, "dlc.rpf")));
            return new PackManagerRow(name, item, exists ? rockstarFolder : null, exists ? SizeOf(game.Resolve(Path.Combine(rockstarFolder, "dlc.rpf"))) : 0,
                StatusOk, SourceRockstar, null, new List<string>());
        }

        var folder = ResolveFolder(game, list.Kind == DlcListTarget.Onigiri, name);
        if (folder == null)
            return new PackManagerRow(name, item, null, 0, StatusMissing, SourceOther, OwnerOf(library, game, null, name), new List<string>());

        string source;
        PackManagerOwner? owner = OwnerOf(library, game, folder);
        if (folder.StartsWith(GameDlcPacks + "\\", StringComparison.OrdinalIgnoreCase))
            source = GameFileChecker.IsOriginal(game.Resolve(Path.Combine(folder, "dlc.rpf"))) ? SourceRockstar : SourceOther;
        else if (IsRockstarName(game, name))
            source = SourceRockstar; // a mods\ override of one of Rockstar's packs
        else
            source = owner != null ? SourceLauncher : SourceOther;
        return new PackManagerRow(name, item, folder, SizeOf(game.Resolve(Path.Combine(folder, "dlc.rpf"))), StatusOk, source, owner, SpawnNamesOf(library, owner));
    }

    /// <summary>The folder dlcpacks:/name/ loads from: onigiri's first when NaturalVision is in, then mods\, then the game's own.</summary>
    static string? ResolveFolder(GameInstall game, bool onigiri, string name)
    {
        var candidates = new List<string>();
        if (onigiri) candidates.Add(Path.Combine(OnigiriDlcPacks, name));
        candidates.Add(Path.Combine(ModsDlcPacks, name));
        candidates.Add(Path.Combine(GameDlcPacks, name));
        return candidates.FirstOrDefault(c => File.Exists(game.Resolve(Path.Combine(c, "dlc.rpf"))));
    }

    static bool IsRockstarName(GameInstall game, string name)
    {
        var dlc = game.Resolve(Path.Combine(GameDlcPacks, name, "dlc.rpf"));
        return File.Exists(dlc) && GameFileChecker.IsOriginal(dlc);
    }

    /// <summary>Add-on pack folders (with a dlc.rpf) in mods\ and, with NaturalVision, in onigiri\.</summary>
    static IEnumerable<(string FolderRel, string Name)> AddonFolders(GameInstall game)
    {
        var roots = new List<string> { ModsDlcPacks };
        if (game.HasOnigiri) roots.Insert(0, OnigiriDlcPacks);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            var full = game.Resolve(root);
            if (!Directory.Exists(full)) continue;
            foreach (var dir in Directory.EnumerateDirectories(full).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(dir);
                if (!File.Exists(Path.Combine(dir, "dlc.rpf")) || !seen.Add(name)) continue;
                yield return (Path.Combine(root, name), name);
            }
        }
    }

    static PackManagerOwner? OwnerOf(ModLibrary library, GameInstall game, string? folderRel, string? name = null)
    {
        lock (library.Gate)
        {
            bool Match(JournalEntry e) => e.Type == JournalEntry.File && e.Path.EndsWith("\\dlc.rpf", StringComparison.OrdinalIgnoreCase)
                && (folderRel != null
                    ? string.Equals(e.Path, Path.Combine(folderRel, "dlc.rpf"), StringComparison.OrdinalIgnoreCase)
                    : name != null && string.Equals(Path.GetFileName(Path.GetDirectoryName(e.Path)), name, StringComparison.OrdinalIgnoreCase));
            var m = library.Mods.Where(x => x.GameId == game.Id && x.Source != "tool" && x.Entries.Any(Match))
                .OrderByDescending(x => x.Enabled).ThenByDescending(x => x.InstalledAt).FirstOrDefault();
            return m == null ? null : new PackManagerOwner(m.Id, m.Name);
        }
    }

    static List<string> SpawnNamesOf(ModLibrary library, PackManagerOwner? owner) =>
        owner == null ? new List<string>() : library.Find(owner.ModId)?.SpawnNames?.ToList() ?? new List<string>();

    static long SizeOf(string file)
    {
        try { return File.Exists(file) ? new FileInfo(file).Length : 0; } catch { return 0; }
    }

    // ---------------------------------------------------------------- changing

    public static void SetEnabled(GameInstall game, LauncherPaths paths, ModLibrary library, string item, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(item)) throw new ArgumentException("Which pack?");
        Change(game, paths, library, RecordName, "dlclist edits", journal =>
        {
            var report = List(game, library);
            var row = report.Packs.FirstOrDefault(r => string.Equals(r.Item, item, StringComparison.OrdinalIgnoreCase))
                      ?? throw new ArgumentException($"{item} isn't in the list.");
            if (row.Source == SourceRockstar)
                throw new InvalidOperationException($"{row.Name} is one of Rockstar's own packs. The launcher leaves those alone: switching one off can break story mode saves.");
            var target = ChangeTarget(game);
            if (enabled)
            {
                if (target.Kind == DlcListTarget.Onigiri && row.Folder != null) CopyPackToOnigiri(game, journal, row.Name);
                journal.AddDlcItem(target, row.Item);
            }
            else
            {
                journal.RemoveDlcItem(target, row.Item);
            }
        });
    }

    public static void Register(GameInstall game, LauncherPaths paths, ModLibrary library, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !SafeName.IsMatch(name)) throw new ArgumentException("That isn't a pack folder name.");
        Change(game, paths, library, RecordName, "dlclist edits", journal => RegisterIn(game, journal, name));
    }

    static void RegisterIn(GameInstall game, Journal journal, string name)
    {
        var folder = ResolveFolder(game, game.HasOnigiri, name)
                     ?? throw new InvalidOperationException($"There's no {name}\\dlc.rpf in the mods or onigiri pack folders.");
        if (folder.StartsWith(GameDlcPacks + "\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{name} sits in the game's own update folder. Move it to mods\\update\\x64\\dlcpacks first.");
        var target = ChangeTarget(game);
        if (target.Kind == DlcListTarget.Onigiri) CopyPackToOnigiri(game, journal, name);
        journal.AddDlcItem(target, DlcList.ItemFor(name));
    }

    /// <summary>Runs one of the fixes List() offered: "duplicates", "onigiri-sync", "register:name", "missing:item".</summary>
    public static void Fix(GameInstall game, LauncherPaths paths, ModLibrary library, string fix)
    {
        fix = (fix ?? "").Trim();
        if (fix.StartsWith("register:", StringComparison.OrdinalIgnoreCase))
        {
            Register(game, paths, library, fix.Substring(9));
            return;
        }
        Change(game, paths, library, RecordName, "dlclist edits", journal =>
        {
            var report = List(game, library);
            var target = ChangeTarget(game);
            if (fix.Equals("duplicates", StringComparison.OrdinalIgnoreCase))
            {
                RemoveDuplicates(game, journal, target);
            }
            else if (fix.Equals("onigiri-sync", StringComparison.OrdinalIgnoreCase))
            {
                if (target.Kind != DlcListTarget.Onigiri) throw new InvalidOperationException("NaturalVision's onigiri isn't installed, so there's nothing to sync.");
                var dropped = report.Packs.Where(r => r.Status == StatusNotInActiveList).ToList();
                if (dropped.Count == 0) throw new InvalidOperationException("Every pack in update.rpf's list is already in NaturalVision's list.");
                foreach (var r in dropped)
                {
                    if (r.Source != SourceRockstar && r.Folder != null) CopyPackToOnigiri(game, journal, r.Name);
                    journal.AddDlcItem(target, r.Item);
                }
            }
            else if (fix.StartsWith("missing:", StringComparison.OrdinalIgnoreCase))
            {
                var item = fix.Substring(8);
                var row = report.Packs.FirstOrDefault(r => string.Equals(r.Item, item, StringComparison.OrdinalIgnoreCase) && r.Status == StatusMissing)
                          ?? throw new InvalidOperationException($"{item} isn't a missing pack (its files are there, or it's not listed).");
                journal.RemoveDlcItem(target, row.Item);
            }
            else
            {
                throw new ArgumentException("Unknown fix " + fix);
            }
        });
    }

    /// <summary>
    /// Keeps the first line of each item and drops the later ones, so load order stays as it was.
    /// </summary>
    static void RemoveDuplicates(GameInstall game, Journal journal, DlcListTarget target)
    {
        byte[]? bytes = target.Kind == DlcListTarget.Onigiri
            ? (File.Exists(target.Path) ? File.ReadAllBytes(target.Path) : null)
            : journal.ReadRpf(@"update\update.rpf", DlcList.InnerPath);
        if (bytes == null) throw new InvalidOperationException("The dlclist couldn't be read.");
        var xml = DlcList.Decode(bytes, out var bom);
        var fixedXml = KeepFirstOccurrences(xml);
        if (fixedXml == xml) throw new InvalidOperationException("No pack is listed twice any more.");
        var data = DlcList.Encode(fixedXml, bom);
        if (target.Kind == DlcListTarget.Onigiri) journal.WriteFile(OnigiriListRel, data);
        else journal.WriteRpf(@"update\update.rpf", DlcList.InnerPath, data);
    }

    public static string KeepFirstOccurrences(string xml)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var item = new Regex(@"<Item>\s*([^<]+?)\s*</Item>", RegexOptions.IgnoreCase);
        var lines = Regex.Split(xml, @"(?<=\n)");
        var kept = new System.Text.StringBuilder(xml.Length);
        bool inComment = false;
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            bool commentLine = inComment || trimmed.StartsWith("<!--");
            if (trimmed.Contains("<!--")) inComment = !trimmed.Contains("-->") || trimmed.LastIndexOf("<!--") > trimmed.LastIndexOf("-->");
            else if (inComment && trimmed.Contains("-->")) inComment = false;
            var m = item.Match(line);
            if (!commentLine && m.Success && !seen.Add(m.Groups[1].Value.Trim())) continue; // a later copy: dropped
            kept.Append(line);
        }
        return kept.ToString();
    }

    /// <summary>NaturalVision's onigiri reads packs from its own dlcpacks folder: copy the pack there (journaled).</summary>
    static void CopyPackToOnigiri(GameInstall game, Journal journal, string name)
    {
        var dst = game.Resolve(Path.Combine(OnigiriDlcPacks, name));
        if (File.Exists(Path.Combine(dst, "dlc.rpf"))) return;
        var src = game.Resolve(Path.Combine(ModsDlcPacks, name));
        if (!File.Exists(Path.Combine(src, "dlc.rpf"))) return;
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            journal.CopyFile(f, game.ToRelative(Path.Combine(dst, Path.GetRelativePath(src, f))));
    }

    // ---------------------------------------------------------------- the tool's Library record

    internal static InstalledMod? FindRecord(ModLibrary library, GameInstall game, string name)
    {
        lock (library.Gate)
            return library.Mods.FirstOrDefault(m => m.GameId == game.Id && m.Source == "tool" && m.Name == name);
    }

    /// <summary>
    /// Runs a change through a Journal on the tool's Library record (created on first use), inside the
    /// archive session and after the usual checks (game closed, mods on, folder writable).
    /// </summary>
    internal static void Change(GameInstall game, LauncherPaths paths, ModLibrary library, string recordName, string summary, Action<Journal> work)
    {
        using var session = RpfService.Session(game);
        new ModInstaller(game, paths, library).Preflight();

        var record = FindRecord(library, game, recordName);
        bool created = record == null;
        record ??= new InstalledMod
        {
            Name = recordName,
            Source = "tool",
            Summary = summary,
            GameId = game.Id,
            InstalledAt = DateTime.UtcNow,
            Kind = "other",
            SpawnNames = new List<string>(),
        };
        record.Enabled = true;
        if (created) library.Upsert(record);

        var journal = new Journal(game, new RpfService(game), record, paths.Backups, library, onChange: library.Save);
        try
        {
            work(journal);
        }
        finally
        {
            bool empty;
            lock (library.Gate) empty = record.Entries.Count == 0;
            if (created && empty) library.Remove(record);
            else library.Upsert(record);
        }
    }
}
