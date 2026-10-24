using System.Diagnostics;

namespace VModLauncher.Core.Game;

public static class GameLauncher
{
    static readonly string[] GameProcesses = { "GTA5", "GTA5_Enhanced", "PlayGTAV", "RAGEPluginHook", "GTAVLauncher" };

    /// <summary>Any GTA V (or its launcher / RAGE Plugin Hook) running anywhere: only one game can play at a time.</summary>
    public static bool IsGameRunning() =>
        GameProcesses.Any(p => Process.GetProcessesByName(p).Length > 0);

    /// <summary>
    /// Is this install's own game running (its files are locked)? A game running from another
    /// folder doesn't count. When Windows hides a process's path, assume it's this one.
    /// </summary>
    public static bool IsGameRunning(GameInstall g)
    {
        var root = g.Root.TrimEnd('\\') + "\\";
        foreach (var name in GameProcesses)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                string? path = null;
                try { path = p.MainModule?.FileName; } catch { }
                if (path == null || path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    /// <summary>Starts story mode through the store the game came from.</summary>
    public static void Play(GameInstall g)
    {
        if (!string.IsNullOrEmpty(g.LaunchUri))
        {
            Process.Start(new ProcessStartInfo(g.LaunchUri) { UseShellExecute = true });
            return;
        }
        var play = Path.Combine(g.Root, "PlayGTAV.exe");
        Process.Start(new ProcessStartInfo(File.Exists(play) ? play : g.ExePath)
        {
            UseShellExecute = true,
            WorkingDirectory = g.Root,
        });
    }

    public static bool CanPlayLspdfr(GameInstall g) =>
        File.Exists(Path.Combine(g.Root, "RAGEPluginHook.exe"));

    /// <summary>LSPDFR runs through RAGE Plugin Hook, which starts the game itself.</summary>
    public static void PlayLspdfr(GameInstall g)
    {
        var rph = Path.Combine(g.Root, "RAGEPluginHook.exe");
        Process.Start(new ProcessStartInfo(rph) { UseShellExecute = true, WorkingDirectory = g.Root });
    }

    // ---- mods on/off (for GTA Online): park the ASI loaders so nothing loads ----

    static readonly string[] LoaderFiles = { "dinput8.dll", "xinput1_4.dll" };
    const string ParkedSuffix = ".vml-off";

    public static bool AreModsEnabled(GameInstall g) =>
        !LoaderFiles.Any(f => File.Exists(Path.Combine(g.Root, f + ParkedSuffix)))
        && !Directory.Exists(g.ModsDir + ParkedSuffix);

    /// <summary>
    /// Renames the ASI loaders (and the mods folder) so the game starts clean, and back.
    /// Only loaders that came from Script Hook V are touched: a real system xinput1_4.dll
    /// never lives in the game folder.
    /// </summary>
    public static void SetModsEnabled(GameInstall g, bool enabled)
    {
        // never overwrite: if both the live and the parked copy exist, something outside the launcher
        // changed the folder and a person has to choose
        var clash = LoaderFiles.Select(f => Path.Combine(g.Root, f))
            .Where(on => File.Exists(on) && File.Exists(on + ParkedSuffix)).Select(Path.GetFileName).ToList();
        if (Directory.Exists(g.ModsDir) && Directory.Exists(g.ModsDir + ParkedSuffix)) clash.Add("mods");
        if (clash.Count > 0)
            throw new InvalidOperationException(
                $"Both {string.Join(", ", clash)} and a parked copy ({ParkedSuffix}) exist in the game folder, so the launcher won't switch mods over them. Keep the one you want and delete or rename the other.");

        foreach (var f in LoaderFiles)
        {
            var on = Path.Combine(g.Root, f);
            var off = on + ParkedSuffix;
            if (enabled && File.Exists(off) && !File.Exists(on)) File.Move(off, on);
            if (!enabled && File.Exists(on) && !File.Exists(off)) File.Move(on, off);
        }
        var mods = g.ModsDir;
        var modsOff = mods + ParkedSuffix;
        if (enabled && Directory.Exists(modsOff) && !Directory.Exists(mods)) Directory.Move(modsOff, mods);
        if (!enabled && Directory.Exists(mods) && !Directory.Exists(modsOff)) Directory.Move(mods, modsOff);
    }
}
