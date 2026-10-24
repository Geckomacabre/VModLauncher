using VModLauncher.Core.Game;
using VModLauncher.Core.Install;
using VModLauncher.Core.Library;

namespace VModLauncher.Core.Tools;

/// <summary>One of the game's own archives. State: original, edited (opened and saved in place) or unreadable.</summary>
public sealed record GameFileArchive(string Path, string State, long Size);

/// <summary>A pack someone put straight into the game's own update\x64\dlcpacks folder.</summary>
public sealed record GameFileStray(string Path, string Name);

public sealed record GameFileReport(List<GameFileArchive> Archives, List<GameFileStray> StrayPacks, List<string> Advice);

/// <summary>
/// Finds edits made straight to the game's own archives (OpenIV used without its mods folder), which
/// a game update or a store's file check silently wipes. It only reads the 16-byte archive headers:
/// Rockstar ships every archive encrypted, and OpenIV saves the archives it edits as OPEN.
/// </summary>
public static class GameFileChecker
{
    public const string RecordName = "Game file fixes";
    const uint Rpf7Magic = 0x52504637;           // "RPF7"
    const uint EncryptionOpen = 0x4E45504F;      // "OPEN": edited by OpenIV / CodeWalker
    const uint EncryptionNone = 0;               // some modding tools write this

    /// <summary>original / edited / unreadable from the header alone.</summary>
    public static string StateOf(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var header = new byte[16];
            if (fs.Read(header, 0, 16) < 16) return "unreadable";
            if (BitConverter.ToUInt32(header, 0) != Rpf7Magic) return "unreadable";
            var encryption = BitConverter.ToUInt32(header, 12);
            return encryption is EncryptionOpen or EncryptionNone ? "edited" : "original";
        }
        catch
        {
            return "unreadable";
        }
    }

    /// <summary>True for an archive as Rockstar ships it (encrypted header).</summary>
    public static bool IsOriginal(string path) => StateOf(path) == "original";

    /// <summary>The game's own archives: the root *.rpf, update\*.rpf and the DLC packs in update\x64\dlcpacks (and x64\dlcpacks).</summary>
    static IEnumerable<string> OwnArchives(GameInstall game)
    {
        IEnumerable<string> Files(string rel, string pattern) =>
            Directory.Exists(game.Resolve(rel.Length == 0 ? "." : rel))
                ? Directory.EnumerateFiles(rel.Length == 0 ? game.Root : game.Resolve(rel), pattern, SearchOption.TopDirectoryOnly)
                : Enumerable.Empty<string>();
        IEnumerable<string> Packs(string rel) =>
            Directory.Exists(game.Resolve(rel))
                ? Directory.EnumerateDirectories(game.Resolve(rel)).Select(d => Path.Combine(d, "dlc.rpf")).Where(File.Exists)
                : Enumerable.Empty<string>();

        return Files("", "*.rpf")
            .Concat(Files("update", "*.rpf"))
            .Concat(Packs(@"update\x64\dlcpacks"))
            .Concat(Packs(@"x64\dlcpacks"))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Header reads only (plus the game's own dlclist when an OPEN pack needs telling apart). Never writes.</summary>
    public static GameFileReport Check(GameInstall game)
    {
        var archives = new List<GameFileArchive>();
        var openPacks = new List<string>();
        foreach (var full in OwnArchives(game))
        {
            var rel = game.ToRelative(full);
            var state = StateOf(full);
            long size = 0;
            try { size = new FileInfo(full).Length; } catch { }
            if (state == "edited" && rel.EndsWith(@"\dlc.rpf", StringComparison.OrdinalIgnoreCase) && rel.Contains(@"\dlcpacks\", StringComparison.OrdinalIgnoreCase))
                openPacks.Add(rel);
            archives.Add(new GameFileArchive(rel, state, size));
        }

        // An OPEN pack in the game's own folder is either one of Rockstar's packs edited in place, or a
        // mod's pack dropped in there. Rockstar's packs are in the game's own dlclist.
        var strays = new List<GameFileStray>();
        if (openPacks.Count > 0)
        {
            HashSet<string>? rockstar = null;
            try
            {
                using (RpfService.Session(game))
                {
                    var bytes = new RpfService(game).TryRead(game.GameUpdateRpf, DlcList.InnerPath);
                    if (bytes != null)
                        rockstar = DlcList.Items(DlcList.Decode(bytes, out _))
                            .Select(i => i.TrimEnd('/').Split('/').Last()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                }
            }
            catch { }
            foreach (var rel in openPacks)
            {
                var name = Path.GetFileName(Path.GetDirectoryName(rel))!;
                if (rockstar != null && rockstar.Contains(name)) continue; // Rockstar's pack, edited: stays "edited"
                strays.Add(new GameFileStray(Path.GetDirectoryName(rel)!, name));
                archives.RemoveAll(a => string.Equals(a.Path, rel, StringComparison.OrdinalIgnoreCase));
            }
        }

        var advice = new List<string>();
        var edited = archives.Where(a => a.State == "edited").ToList();
        if (edited.Count > 0)
        {
            var withCopy = edited.Count(a => File.Exists(game.Resolve(Path.Combine("mods", a.Path))));
            advice.Add($"{edited.Count} of the game's own archive{(edited.Count == 1 ? " was" : "s were")} edited in place, so the next game update or a file check in your store undoes those edits (and an update can crash a game with half-old files).");
            advice.Add(withCopy == edited.Count
                ? "Each one already has a copy in the mods folder, which is what the game loads with OpenIV.asi / OpenRPF. Restore the originals through your store."
                : "Move the edits into the mods folder first (they keep working from there), then restore the originals through your store.");
        }
        if (strays.Count > 0)
            advice.Add($"{strays.Count} mod pack{(strays.Count == 1 ? " is" : "s are")} in the game's own update\\x64\\dlcpacks folder. Packs belong in mods\\update\\x64\\dlcpacks, where game updates and file checks leave them alone: copy them there and register them on the Add-on packs tab.");
        if (archives.Any(a => a.State == "unreadable"))
            advice.Add("Some archives couldn't be read (damaged, or still downloading). A file check in your store fixes damaged ones.");
        if (advice.Count == 0)
            advice.Add("Every archive is as Rockstar ships it. Mods only live in the mods folder.");
        return new GameFileReport(archives, strays, advice);
    }

    /// <summary>
    /// Copies an edited game archive into mods\ (when there's no mods copy yet), so its edits keep working
    /// after the original is restored. False when a mods copy already exists. Journaled in a
    /// "Game file fixes" Library record; the copy itself is kept if that record is removed.
    /// </summary>
    public static bool MoveToMods(GameInstall game, LauncherPaths paths, ModLibrary library, string path)
    {
        var rel = Journal.NormalizeRel(path ?? "");
        if (rel.Length == 0 || rel.Contains("..") || rel.StartsWith("mods\\", StringComparison.OrdinalIgnoreCase) || !rel.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("That isn't one of the game's own archives.");
        var full = game.Resolve(rel);
        if (!File.Exists(full) || StateOf(full) != "edited")
            throw new InvalidOperationException($"{rel} isn't an edited game archive (any more).");
        if (File.Exists(game.Resolve(Path.Combine("mods", rel)))) return false;
        PackManager.Change(game, paths, library, RecordName, "Edited game archives copied into mods", journal => journal.ModsArchive(rel));
        return true;
    }

    /// <summary>How to get Rockstar's original files back through the store the game came from.</summary>
    public static (string How, string? Url) Verify(GameInstall game)
    {
        const string after = " It puts Rockstar's original files back and leaves the mods folder alone.";
        switch (game.Store)
        {
            case "Steam":
                return ("Steam checks the files for you: press the button, or in Steam go to Library, right-click Grand Theft Auto V" +
                        (game.Edition == GameEdition.Enhanced ? " Enhanced" : "") + ", Properties, Installed Files, Verify integrity of game files." + after,
                    $"steam://validate/{(game.Edition == GameEdition.Enhanced ? 3240220 : 271590)}");
            case "Epic":
                return ("In the Epic Games Launcher: Library, the three dots on Grand Theft Auto V" + (game.Edition == GameEdition.Enhanced ? " Enhanced" : "") +
                        ", Manage, then Verify." + after, null);
            case "Rockstar":
                return ("In the Rockstar Games Launcher: Settings, My installed games, Grand Theft Auto V" + (game.Edition == GameEdition.Enhanced ? " Enhanced" : "") +
                        ", then Verify Integrity." + after, null);
            default:
                return ("Use your store's file check (Steam: Verify integrity of game files; Epic: Manage, Verify; Rockstar Games Launcher: Verify Integrity)." + after, null);
        }
    }
}
