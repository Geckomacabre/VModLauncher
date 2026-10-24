namespace VModLauncher.Core;

/// <summary>Where the launcher keeps its own data (never inside the game folder).</summary>
public sealed class LauncherPaths
{
    public string Root { get; }
    public string Downloads => Dir("downloads");
    public string Staging => Dir("staging");
    public string Backups => Dir("backups");
    public string Payloads => Dir("payloads");
    public string Cache => Dir("cache");
    public string Logs => Dir("logs");
    public string WebProfile => Dir("webview");
    public string LibraryFile => Path.Combine(Root, "library.json");
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string ProfilesFile => Path.Combine(Root, "profiles.json");
    public string FavoritesFile => Path.Combine(Root, "favorites.json");
    /// <summary>Files the player moved out of the game folder from Diagnostics (never deleted).</summary>
    public string Quarantine => Dir("quarantine");
    /// <summary>Small per-game state (when mods\update\update.rpf was made, launch option backups).</summary>
    public string State => Dir("state");

    public LauncherPaths(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VModLauncher");
        Directory.CreateDirectory(Root);
    }

    string Dir(string name)
    {
        var p = Path.Combine(Root, name);
        Directory.CreateDirectory(p);
        return p;
    }
}
