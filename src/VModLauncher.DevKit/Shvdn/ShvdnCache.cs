using System.Diagnostics;
using System.Text;

namespace VModLauncher.DevKit.Shvdn;

/// <summary>
/// Script Hook V .NET loads scripts through .NET shadow copies in %LOCALAPPDATA%\assembly\dl3, keyed on the
/// script's path and file time. If an update writes a script with the same time as before (packages that stamp
/// one date on every file), the game keeps running the cached old copy. This lists the cache and clears it.
/// </summary>
public static class ShvdnCache
{
    public sealed record Entry(string Name, string Version, string OriginalPath, string Folder, DateTime Cached, long Size)
    {
        public override string ToString() => $"{Name} {Version}  from {OriginalPath}  (cached {Cached:yyyy-MM-dd HH:mm}, {Size:N0} bytes)";
    }

    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "assembly", "dl3");

    /// <param name="filter">Only entries whose name or original path contains this (case-insensitive).</param>
    public static List<Entry> List(string? filter = null, string? root = null)
    {
        var dir = root ?? Root;
        var list = new List<Entry>();
        if (!Directory.Exists(dir)) return list;
        foreach (var ini in Directory.EnumerateFiles(dir, "__AssemblyInfo__.ini", SearchOption.AllDirectories))
        {
            var folder = Path.GetDirectoryName(ini)!;
            var dll = Directory.EnumerateFiles(folder).FirstOrDefault(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
            var (name, version, url) = ParseInfo(File.ReadAllBytes(ini));
            if (name.Length == 0 && dll != null) name = Path.GetFileNameWithoutExtension(dll);
            var e = new Entry(name, version, url, folder, Directory.GetLastWriteTime(folder), dll != null ? new FileInfo(dll).Length : 0);
            if (filter == null || e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || e.OriginalPath.Contains(filter, StringComparison.OrdinalIgnoreCase))
                list.Add(e);
        }
        return list.OrderBy(e => e.OriginalPath, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Cached).ToList();
    }

    /// <summary>"Name,Version,Culture,PublicKeyToken, file:///path" in UTF-16 (the .NET Framework writes it without a BOM).</summary>
    internal static (string Name, string Version, string Url) ParseInfo(byte[] data)
    {
        string text = data.Length >= 2 && data[1] == 0 ? Encoding.Unicode.GetString(data) : Encoding.UTF8.GetString(data);
        text = text.Trim('﻿', '\0', ' ', '\r', '\n');
        var fields = text.Split(',');
        var at = text.IndexOf("file:///", StringComparison.OrdinalIgnoreCase);
        var url = at >= 0 ? text[(at + 8)..].Split('\0', '\r', '\n')[0].Trim().Replace('/', '\\') : "";
        return (fields.Length > 0 ? fields[0].Trim() : "", fields.Length > 1 ? fields[1].Trim() : "", url);
    }

    /// <summary>Deletes the matching cache entries (all with no filter). Refuses while GTA V is running: it holds them open.</summary>
    public static List<Entry> Clear(string? filter = null, string? root = null)
    {
        var running = Process.GetProcesses().Where(p => p.ProcessName.Equals("GTA5", StringComparison.OrdinalIgnoreCase) || p.ProcessName.Equals("GTA5_Enhanced", StringComparison.OrdinalIgnoreCase)).ToList();
        if (running.Count > 0) throw new InvalidOperationException("GTA V is running; close it first, it keeps the cached scripts open");
        var entries = List(filter, root);
        foreach (var e in entries)
        {
            Directory.Delete(e.Folder, true);
            // the path-hash folder above is empty once its last date folder is gone
            var parent = Path.GetDirectoryName(e.Folder);
            if (parent != null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
        }
        return entries;
    }
}
