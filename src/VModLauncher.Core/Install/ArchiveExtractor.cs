using SharpCompress.Archives;
using SharpCompress.Common;

namespace VModLauncher.Core.Install;

/// <summary>Unpacks .zip / .rar / .7z / .oiv into a staging folder.</summary>
public static class ArchiveExtractor
{
    static readonly string[] ArchiveExtensions = { ".zip", ".rar", ".7z", ".oiv", ".tar", ".gz" };

    public static bool IsArchive(string path) =>
        ArchiveExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static void Extract(string archivePath, string destDir, IProgress<double>? progress = null)
    {
        Directory.CreateDirectory(destDir);
        var root = Path.GetFullPath(destDir).TrimEnd('\\') + '\\';
        using var archive = ArchiveFactory.OpenArchive(archivePath);
        var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
        long total = Math.Max(1, entries.Sum(e => e.Size));
        long done = 0;
        foreach (var entry in entries)
        {
            var key = (entry.Key ?? "").Replace('/', '\\').TrimStart('\\');
            if (key.Length == 0) continue;
            var target = Path.GetFullPath(Path.Combine(root, key));
            // zip-slip guard: never write outside the staging folder
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.WriteToFile(target, new ExtractionOptions { Overwrite = true, PreserveFileTime = true });
            done += entry.Size;
            progress?.Report((double)done / total);
        }
    }

    /// <summary>
    /// Mods often ship an archive inside an archive ("Mod.zip" holding "Mod v2.rar").
    /// Unpack nested archives into a folder named after them, a couple of levels deep.
    /// .oiv packages are left alone: they are installed as packages.
    /// </summary>
    public static void ExtractNested(string dir, int depth = 2)
    {
        for (int level = 0; level < depth; level++)
        {
            var nested = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Where(f => IsArchive(f) && !f.EndsWith(".oiv", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (nested.Count == 0) return;
            foreach (var file in nested)
            {
                var into = Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file));
                if (Directory.Exists(into)) into += "_unpacked";
                try
                {
                    Extract(file, into);
                    File.Delete(file);
                }
                catch
                {
                    // not really an archive, or a format SharpCompress can't read: leave the file in place
                }
            }
        }
    }
}

