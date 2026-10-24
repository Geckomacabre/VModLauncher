namespace VModLauncher.DevKit;

/// <summary>Guards for the tools: they read games, they never write into one.</summary>
public static class Safety
{
    /// <summary>
    /// Throws when <paramref name="path"/> is inside a GTA V folder (one of its parents holds GTA5.exe or
    /// GTA5_Enhanced.exe). Installing is the launcher's or an installer's job, with backups and an undo.
    /// </summary>
    public static void EnsureNotInGame(string path)
    {
        var dir = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(dir) && (File.Exists(Path.Combine(dir, "GTA5.exe")) || File.Exists(Path.Combine(dir, "GTA5_Enhanced.exe"))))
                throw new InvalidOperationException($"{path} is inside the game folder {dir}: the dev tools don't write into games");
            dir = Path.GetDirectoryName(dir);
        }
    }
}
