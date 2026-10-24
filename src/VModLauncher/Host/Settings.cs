using System.IO;
using System.Text.Json;

namespace VModLauncher.Host;

public sealed class Settings
{
    public bool KeepDownloads { get; set; } = true;
    public bool ConfirmUninstall { get; set; } = true;
    public string DefaultSite { get; set; } = "gta5mods";
    public string Accent { get; set; } = "vice";
    public string? SelectedGameId { get; set; }
    public List<string> ExtraGameRoots { get; set; } = new();

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load(string file)
    {
        try
        {
            if (File.Exists(file)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(file)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save(string file)
    {
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, file, overwrite: true);
    }
}
