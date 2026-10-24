using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using VModLauncher.Core.Game;
using VModLauncher.Core.Install;

namespace VModLauncher.Core.Features;

public sealed record ShvInfo(bool Installed, bool? SupportsGame, string? Version, string? Message);

public sealed record HealthProblem(string Level, string Text, string? Fix);

public sealed class HealthReport
{
    public string? GameVersion { get; set; }
    public bool ModsUpdateStale { get; set; }
    public string? StaleReason { get; set; }
    public ShvInfo ScriptHookV { get; set; } = new(false, null, null, null);
    public int AddonPacks { get; set; }
    public string? PackLimitWarning { get; set; }
    public List<HealthProblem> Problems { get; set; } = new();
}

/// <summary>Small per-game facts the launcher remembers (when it last built mods\update\update.rpf).</summary>
public sealed class GameState
{
    public long? UpdateRpfLength { get; set; }
    public DateTime? UpdateRpfWrittenUtc { get; set; }
    public DateTime? RepairedAtUtc { get; set; }

    static string FileFor(LauncherPaths paths, GameInstall game)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(game.Id)))[..12];
        return Path.Combine(paths.State, $"game-{hash}.json");
    }

    public static GameState Load(LauncherPaths paths, GameInstall game)
    {
        try
        {
            var f = FileFor(paths, game);
            if (File.Exists(f)) return JsonSerializer.Deserialize<GameState>(File.ReadAllText(f)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save(LauncherPaths paths, GameInstall game) =>
        File.WriteAllText(FileFor(paths, game), JsonSerializer.Serialize(this));

    /// <summary>Remember which game update.rpf the mods copy was made from (call after copying it into mods).</summary>
    public static void RecordModsCopy(LauncherPaths paths, GameInstall game)
    {
        if (!File.Exists(game.GameUpdateRpf)) return;
        var s = Load(paths, game);
        var info = new FileInfo(game.GameUpdateRpf);
        s.UpdateRpfLength = info.Length;
        s.UpdateRpfWrittenUtc = info.LastWriteTimeUtc;
        s.Save(paths, game);
    }
}

/// <summary>
/// "Will my game still start?" checks: a stale mods\update\update.rpf after a Rockstar update
/// (the classic crash on load), Script Hook V not supporting the game build, and too many packs.
/// </summary>
public static class GameHealth
{
    /// <summary>dlclist items per archive, cached by size + write time (reading update.rpf takes a few seconds).</summary>
    static readonly Dictionary<string, (long Len, DateTime Written, List<string> Items)> ListCache = new(StringComparer.OrdinalIgnoreCase);
    static readonly object CacheLock = new();

    public static List<string>? DlcItems(RpfService rpf, string archive)
    {
        if (!File.Exists(archive)) return null;
        var info = new FileInfo(archive);
        lock (CacheLock)
        {
            if (ListCache.TryGetValue(archive, out var hit) && hit.Len == info.Length && hit.Written == info.LastWriteTimeUtc)
                return hit.Items;
        }
        var bytes = rpf.TryRead(archive, DlcList.InnerPath);
        if (bytes == null) return null;
        var items = DlcList.Items(DlcList.Decode(bytes, out _));
        lock (CacheLock) ListCache[archive] = (info.Length, info.LastWriteTimeUtc, items);
        rpf.Forget(archive);
        return items;
    }

    public static HealthReport Check(GameInstall game, LauncherPaths paths)
    {
        var report = new HealthReport();
        try { report.GameVersion = FileVersionInfo.GetVersionInfo(game.ExePath).FileVersion; } catch { }

        using (RpfService.Session(game)) // reads Rockstar archives: one edition's keys at a time
        {
            var rpf = new RpfService(game);
            CheckStale(game, paths, rpf, report);
            CheckPacks(game, rpf, report);
        }
        report.ScriptHookV = CheckScriptHookV(game, report.GameVersion);

        if (report.ModsUpdateStale)
            report.Problems.Add(new("Warn", report.StaleReason ?? "Your mods copy of update.rpf is older than the game's.", "game.repair"));
        if (report.ScriptHookV.Installed && report.ScriptHookV.SupportsGame == false)
            report.Problems.Add(new("Block", report.ScriptHookV.Message ?? "Script Hook V doesn't support this game version yet.", null));
        if (report.PackLimitWarning != null)
            report.Problems.Add(new("Warn", report.PackLimitWarning, null));
        return report;
    }

    // ---------------------------------------------------------------- stale mods\update\update.rpf

    static void CheckStale(GameInstall game, LauncherPaths paths, RpfService rpf, HealthReport report)
    {
        var mods = game.ModsUpdateRpf;
        var original = game.GameUpdateRpf;
        if (!File.Exists(mods) || !File.Exists(original)) return;

        // (a) the game's list names DLC packs the mods copy doesn't: a Rockstar update added content
        try
        {
            var gameItems = DlcItems(rpf, original);
            var modItems = DlcItems(rpf, mods);
            if (gameItems != null && modItems != null)
            {
                var have = modItems.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var missing = gameItems.Where(i => !have.Contains(i)).ToList();
                if (missing.Count > 0)
                {
                    report.ModsUpdateStale = true;
                    report.StaleReason = $"A game update added {missing.Count} DLC pack(s) ({string.Join(", ", missing.Take(3).Select(PackName))}{(missing.Count > 3 ? ", …" : "")}) that your mods copy of update.rpf doesn't have. The game usually crashes on load until it's rebuilt.";
                    return;
                }
            }
        }
        catch { /* encrypted and no keys: fall back to dates */ }

        // (b) the game's update.rpf changed after the mods copy was made
        var state = GameState.Load(paths, game);
        var gi = new FileInfo(original);
        if (state.UpdateRpfLength != null)
        {
            if (state.UpdateRpfLength != gi.Length || state.UpdateRpfWrittenUtc != gi.LastWriteTimeUtc)
            {
                report.ModsUpdateStale = true;
                report.StaleReason = "The game's update.rpf changed since the launcher made your mods copy (a game update). Rebuild it so the game starts.";
            }
            return;
        }
        var mi = new FileInfo(mods);
        if (gi.LastWriteTimeUtc > mi.CreationTimeUtc.AddMinutes(1))
        {
            report.ModsUpdateStale = true;
            report.StaleReason = $"The game's update.rpf ({gi.LastWriteTime:d}) is newer than your mods copy ({mi.CreationTime:d}). If the game crashes while loading after an update, rebuild it.";
        }
    }

    static string PackName(string item) => item.TrimEnd('/').Split('/').Last();

    // ---------------------------------------------------------------- Script Hook V

    static readonly Regex UnsupportedLine = new(@"unknown game version|not supported|unsupported|game version is not|check (for )?updates|FATAL", RegexOptions.IgnoreCase);

    public static ShvInfo CheckScriptHookV(GameInstall game, string? gameVersion)
    {
        var dll = Path.Combine(game.Root, "ScriptHookV.dll");
        if (!File.Exists(dll)) return new ShvInfo(false, null, null, null);
        string? shvVersion = null;
        try { shvVersion = FileVersionInfo.GetVersionInfo(dll).FileVersion; } catch { }

        // 1. the last session's log is the real answer, as long as it was this game build
        var log = Path.Combine(game.Root, "ScriptHookV.log");
        var gameBuild = BuildOf(gameVersion);
        var logVerdict = LastSessionVerdict(log, out var logMessage, out var loggedBuild);
        if (logVerdict == true && loggedBuild != null && gameBuild != null && loggedBuild != gameBuild) logVerdict = null; // game updated since

        // 2. otherwise compare builds: SHV "3889.0.1158.13" = Legacy build 3889, Enhanced build 1158
        bool? byVersion = null;
        if (shvVersion != null && gameBuild != null)
        {
            var parts = shvVersion.Split('.');
            string? shvBuild = game.Edition == GameEdition.Enhanced
                ? (parts.Length >= 3 ? parts[2] : null)
                : (parts.Length >= 1 ? parts[0] : null);
            if (shvBuild != null) byVersion = shvBuild == gameBuild;
        }

        bool? supports = logVerdict ?? byVersion;
        string? message = logVerdict == false
            ? $"Script Hook V refused this game version last time ({logMessage}). Get the update from dev-c.com — new versions come out a few days after each game update."
            : byVersion == false
                ? $"Script Hook V {shvVersion} was made for a different game build than yours ({gameVersion}). If scripts don't load, get the newest one from dev-c.com."
                : null;
        return new ShvInfo(true, supports, shvVersion, message);
    }

    static string? BuildOf(string? version)
    {
        // 1.0.3889.0 -> 3889 ; 1.0.1158.13 -> 1158
        var parts = (version ?? "").Split('.');
        return parts.Length >= 3 ? parts[2] : null;
    }

    static readonly Regex LoggedVersion = new(@"game version is VER_(?:EN_)?\d+_\d+_(\d+)_\d+", RegexOptions.IgnoreCase);

    /// <summary>Reads the newest session in ScriptHookV.log: false if it said the version isn't supported.</summary>
    public static bool? LastSessionVerdict(string log, out string? message, out string? loggedBuild)
    {
        message = null;
        loggedBuild = null;
        var lines = Diagnostics.ReadShared(log, maxBytes: 64_000)?.Split('\n');
        if (lines == null) return null;
        // a session starts with "INIT: Started" (or similar); only judge the latest one
        int start = 0;
        for (int i = lines.Length - 1; i >= 0; i--)
            if (lines[i].Contains("INIT", StringComparison.OrdinalIgnoreCase) && lines[i].Contains("Start", StringComparison.OrdinalIgnoreCase)) { start = i; break; }
        var session = lines.Skip(start).ToList();
        var ver = session.Select(l => LoggedVersion.Match(l)).FirstOrDefault(m => m.Success);
        if (ver != null) loggedBuild = ver.Groups[1].Value;
        var bad = session.FirstOrDefault(l => UnsupportedLine.IsMatch(l));
        if (bad != null) { message = bad.Trim(); return false; }
        if (session.Any(l => l.Contains("game version", StringComparison.OrdinalIgnoreCase) || l.Contains("Registering script", StringComparison.OrdinalIgnoreCase) || l.Contains("Pool 1 extended", StringComparison.OrdinalIgnoreCase)))
            return true;
        return null;
    }

    // ---------------------------------------------------------------- packs

    static void CheckPacks(GameInstall game, RpfService rpf, HealthReport report)
    {
        try
        {
            var target = DlcListTarget.For(game);
            List<string>? active = target.Kind == DlcListTarget.Onigiri
                ? File.Exists(target.Path) ? DlcList.Items(DlcList.Decode(File.ReadAllBytes(target.Path), out _)) : null
                : DlcItems(rpf, File.Exists(game.ModsUpdateRpf) ? game.ModsUpdateRpf : game.GameUpdateRpf);
            if (active == null) return;
            report.AddonPacks = active.Count(i => i.StartsWith("dlcpacks:/", StringComparison.OrdinalIgnoreCase));

            var vanilla = DlcItems(rpf, game.GameUpdateRpf)?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>();
            int added = active.Count(i => i.StartsWith("dlcpacks:/", StringComparison.OrdinalIgnoreCase) && !vanilla.Contains(i));
            if (added <= 60) return;

            bool hasGameconfig = target.Kind == DlcListTarget.Onigiri
                ? File.Exists(Path.Combine(game.Root, "onigiri", "common", "data", "gameconfig.xml"))
                : File.Exists(game.ModsUpdateRpf) && rpf.TryRead(game.ModsUpdateRpf, "common/data/gameconfig.xml") != null;
            bool hasLimitFix = Directory.EnumerateFiles(game.Root, "*.asi").Any(f =>
                Regex.IsMatch(Path.GetFileName(f), "packfile|dlclimit|limitadjust|heapadjust", RegexOptions.IgnoreCase));
            if (!hasGameconfig && !hasLimitFix)
                report.PackLimitWarning = $"{added} add-on packs are installed. Past about 60 the game runs out of room for them: install a gameconfig and the Packfile Limit Adjuster (both on GTA5-Mods).";
        }
        catch { }
    }
}
