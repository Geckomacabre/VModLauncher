using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace VModLauncher.Core.Game;

/// <summary>Finds GTA V installs from Steam, Epic and Rockstar Games Launcher records.</summary>
public static class GameLocator
{
    const int SteamLegacyAppId = 271590;
    const int SteamEnhancedAppId = 3240220;

    public static List<GameInstall> FindAll(IEnumerable<string>? extraRoots = null)
    {
        var found = new Dictionary<string, GameInstall>();
        void Add(GameInstall? g)
        {
            if (g != null && !found.ContainsKey(g.Id)) found[g.Id] = g;
        }

        foreach (var g in FromEpic()) Add(g);
        foreach (var g in FromSteam()) Add(g);
        foreach (var g in FromRockstar()) Add(g);
        if (extraRoots != null)
            foreach (var r in extraRoots) Add(GameInstall.TryOpen(r));

        return found.Values.OrderBy(g => g.Edition).ThenBy(g => g.Root).ToList();
    }

    static IEnumerable<GameInstall> FromEpic()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(dir)) yield break;
        foreach (var file in Directory.EnumerateFiles(dir, "*.item"))
        {
            GameInstall? g = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var r = doc.RootElement;
                var name = r.TryGetProperty("DisplayName", out var dn) ? dn.GetString() ?? "" : "";
                if (!name.StartsWith("Grand Theft Auto V", StringComparison.OrdinalIgnoreCase)) continue;
                var loc = r.GetProperty("InstallLocation").GetString() ?? "";
                var ns = r.GetProperty("CatalogNamespace").GetString();
                var item = r.GetProperty("CatalogItemId").GetString();
                var app = r.GetProperty("AppName").GetString();
                var uri = $"com.epicgames.launcher://apps/{ns}%3A{item}%3A{app}?action=launch&silent=true";
                g = GameInstall.TryOpen(loc, "Epic", uri);
            }
            catch { }
            if (g != null) yield return g;
        }
    }

    static IEnumerable<GameInstall> FromSteam()
    {
        string? steam = null;
        try
        {
            steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                    ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
        }
        catch { }
        if (string.IsNullOrEmpty(steam)) yield break;

        var libraries = new List<string> { steam };
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        }

        foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var common = Path.Combine(lib, "steamapps", "common");
            var legacy = GameInstall.TryOpen(Path.Combine(common, "Grand Theft Auto V"), "Steam", $"steam://rungameid/{SteamLegacyAppId}");
            if (legacy != null) yield return legacy;
            var enhanced = GameInstall.TryOpen(Path.Combine(common, "Grand Theft Auto V Enhanced"), "Steam", $"steam://rungameid/{SteamEnhancedAppId}");
            if (enhanced != null) yield return enhanced;
        }
    }

    static IEnumerable<GameInstall> FromRockstar()
    {
        string[] keys =
        {
            @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Rockstar Games\Grand Theft Auto V",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Rockstar Games\GTAV Enhanced",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Rockstar Games\Grand Theft Auto V Enhanced",
        };
        foreach (var key in keys)
        {
            foreach (var value in new[] { "InstallFolder", "InstallFolderSteam", "InstallFolderEpic" })
            {
                string? path = null;
                try { path = Registry.GetValue(key, value, null) as string; } catch { }
                var store = value switch { "InstallFolderSteam" => "Steam", "InstallFolderEpic" => "Epic", _ => "Rockstar" };
                var g = path == null ? null : GameInstall.TryOpen(path, store);
                if (g != null) yield return g;
            }
        }
    }
}
