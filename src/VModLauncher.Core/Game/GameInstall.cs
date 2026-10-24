namespace VModLauncher.Core.Game;

public enum GameEdition { Legacy, Enhanced }

/// <summary>One GTA V install on disk (Legacy = GTA5.exe, Enhanced = GTA5_Enhanced.exe).</summary>
public sealed class GameInstall
{
    public required string Root { get; init; }
    public required GameEdition Edition { get; init; }
    /// <summary>Steam, Epic, Rockstar or Custom.</summary>
    public string Store { get; init; } = "Custom";
    /// <summary>Store-specific launch URI (Steam / Epic); null means start PlayGTAV.exe.</summary>
    public string? LaunchUri { get; init; }
    /// <summary>
    /// Folder whose exe the archive keys are read from; null means <see cref="Root"/>. A test sandbox points
    /// this at a real install (read only) so Rockstar's encrypted archives copied into it stay readable.
    /// </summary>
    public string? KeysRoot { get; init; }

    public string Id => (Edition + "|" + Path.GetFullPath(Root).TrimEnd('\\')).ToLowerInvariant();
    public string Name => Edition == GameEdition.Enhanced ? "GTA V Enhanced" : "GTA V Legacy";
    public string ExeName => Edition == GameEdition.Enhanced ? "GTA5_Enhanced.exe" : "GTA5.exe";
    public string ExePath => Path.Combine(Root, ExeName);

    /// <summary>Script Hook V's ASI loader: dinput8.dll on Legacy, xinput1_4.dll on Enhanced.</summary>
    public string AsiLoaderName => Edition == GameEdition.Enhanced ? "xinput1_4.dll" : "dinput8.dll";

    public string ModsDir => Path.Combine(Root, "mods");
    public string ModsUpdateRpf => Path.Combine(Root, "mods", "update", "update.rpf");
    public string GameUpdateRpf => Path.Combine(Root, "update", "update.rpf");
    public string ModsDlcPacksDir => Path.Combine(Root, "mods", "update", "x64", "dlcpacks");

    /// <summary>NaturalVision's onigiri.asi loads its own loose dlclist.xml, which wins over update.rpf's.</summary>
    public string OnigiriDlcList => Path.Combine(Root, "onigiri", "common", "data", "dlclist.xml");
    public bool HasOnigiri => File.Exists(Path.Combine(Root, "onigiri.asi")) && File.Exists(OnigiriDlcList);
    public string OnigiriDlcPacksDir => Path.Combine(Root, "onigiri", "dlcpacks");

    public string Resolve(string gameRelative) =>
        Path.GetFullPath(Path.Combine(Root, gameRelative.Replace('/', '\\')));

    public string ToRelative(string fullPath) =>
        Path.GetRelativePath(Root, fullPath).Replace('/', '\\');

    public static GameInstall? TryOpen(string root, string store = "Custom", string? launchUri = null)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
        root = Path.GetFullPath(root).TrimEnd('\\');
        if (File.Exists(Path.Combine(root, "GTA5_Enhanced.exe")))
            return new GameInstall { Root = root, Edition = GameEdition.Enhanced, Store = store, LaunchUri = launchUri };
        if (File.Exists(Path.Combine(root, "GTA5.exe")))
            return new GameInstall { Root = root, Edition = GameEdition.Legacy, Store = store, LaunchUri = launchUri };
        return null;
    }

    public bool IsWritable()
    {
        try
        {
            var probe = Path.Combine(Root, ".vml_write_test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    public override string ToString() => $"{Name} ({Store}) - {Root}";
}
