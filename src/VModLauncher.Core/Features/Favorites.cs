using System.Text.Json;
using System.Text.Json.Nodes;
using VModLauncher.Core.Game;

namespace VModLauncher.Core.Features;

/// <summary>Mods the player starred while browsing, kept exactly as the site extractor returned them.</summary>
public sealed class Favorites
{
    readonly string file;
    readonly List<JsonObject> items = new();

    public Favorites(string file)
    {
        this.file = file;
        try
        {
            if (File.Exists(file) && JsonNode.Parse(File.ReadAllText(file)) is JsonArray arr)
                items.AddRange(arr.OfType<JsonObject>());
        }
        catch { }
    }

    public IReadOnlyList<JsonObject> All => items;

    static string Key(JsonObject o) => (o["url"]?.GetValue<string>() ?? "").Trim().TrimEnd('/').ToLowerInvariant();

    /// <summary>Adds the item, or removes it when it's already a favourite. Returns whether it's a favourite now.</summary>
    public bool Toggle(JsonObject item)
    {
        var key = Key(item);
        if (key.Length == 0) throw new ArgumentException("That mod has no page URL.");
        var existing = items.FindIndex(i => Key(i) == key);
        if (existing >= 0)
        {
            items.RemoveAt(existing);
            Save();
            return false;
        }
        var copy = (JsonObject)item.DeepClone();
        copy.Remove("installed"); // library state changes; it's added fresh when listed
        items.Insert(0, copy);
        Save();
        return true;
    }

    public bool Contains(string url) => items.Any(i => Key(i) == url.Trim().TrimEnd('/').ToLowerInvariant());

    void Save()
    {
        var arr = new JsonArray(items.Select(i => (JsonNode)i.DeepClone()).ToArray());
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, arr.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, file, overwrite: true);
    }
}

public sealed record StorageReport(long ModsFolder, long Backups, long Downloads, long Payloads, long Cache);

/// <summary>How much disk the launcher and the mods folder use; cleans only the launcher's own throwaway folders.</summary>
public static class Storage
{
    public static long SizeOf(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        long total = 0;
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try { total += f.Length; } catch { }
            }
        }
        catch { }
        return total;
    }

    public static StorageReport Report(LauncherPaths paths, GameInstall? game) =>
        new(game == null ? 0 : SizeOf(game.ModsDir), SizeOf(paths.Backups), SizeOf(paths.Downloads), SizeOf(paths.Payloads), SizeOf(paths.Cache));

    /// <summary>
    /// downloads: finished downloads waiting for a plan (anything older than 30 minutes);
    /// staging: unpacked mods from abandoned plans (older than an hour); cache: game index and art (rebuilt on demand).
    /// Kept downloads of installed mods (payloads) and backups are never touched.
    /// </summary>
    public static void Clean(LauncherPaths paths, string what)
    {
        var now = DateTime.UtcNow;
        switch (what)
        {
            case "downloads":
                foreach (var f in new DirectoryInfo(paths.Downloads).EnumerateFiles())
                    if (now - f.LastWriteTimeUtc > TimeSpan.FromMinutes(30)) TryDelete(f.FullName);
                break;
            case "staging":
                foreach (var d in new DirectoryInfo(paths.Staging).EnumerateDirectories())
                    if (now - d.LastWriteTimeUtc > TimeSpan.FromHours(1)) { try { d.Delete(true); } catch { } }
                break;
            case "cache":
                foreach (var f in new DirectoryInfo(paths.Cache).EnumerateFiles("*", SearchOption.AllDirectories)) TryDelete(f.FullName);
                foreach (var d in new DirectoryInfo(paths.Cache).EnumerateDirectories()) { try { d.Delete(true); } catch { } }
                break;
            default:
                throw new ArgumentException("Can only clean downloads, staging or cache.");
        }
    }

    static void TryDelete(string f) { try { File.Delete(f); } catch { } }
}
