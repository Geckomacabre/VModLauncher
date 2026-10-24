using CodeWalker.GameFiles;
using VModLauncher.Core.Game;

namespace VModLauncher.Core.Install;

/// <summary>
/// RPF7 access through CodeWalker.Core. Every write goes to an archive under the game's
/// mods\ folder; the originals in the game folder are only ever read or copied.
/// </summary>
public sealed class RpfService
{
    readonly GameInstall game;
    static readonly object KeyLock = new();
    static string? loadedKeysFor;
    static string? failedKeysFor;

    /// <summary>Opened archives, reused while nothing outside this service has touched the file.</summary>
    readonly Dictionary<string, (RpfFile Rpf, long Length, DateTime Written)> cache = new(StringComparer.OrdinalIgnoreCase);

    public RpfService(GameInstall game) => this.game = game;

    /// <summary>Loads the AES/NG keys from the game's exe (needed for Rockstar's encrypted archives).</summary>
    public void EnsureKeys()
    {
        lock (KeyLock)
        {
            if (loadedKeysFor == game.Id) return;
            GTA5Keys.LoadFromPath(game.KeysRoot ?? game.Root, game.Edition == GameEdition.Enhanced);
            // GTA5Hash copies the lookup table in its static constructor; if anything touched it
            // before the keys were loaded it holds null for the rest of the process.
            GTA5Hash.LUT = GTA5Keys.PC_LUT;
            RpfManager.IsGen9 = game.Edition == GameEdition.Enhanced;
            loadedKeysFor = game.Id;
        }
    }

    static readonly object SessionLock = new();

    /// <summary>
    /// CodeWalker keeps the archive keys in static state, so work on Legacy and Enhanced archives must
    /// never overlap: an Enhanced header re-encrypted with Legacy keys is a corrupt archive. Every
    /// install, uninstall, scan and art pass runs inside one of these (re-entrant on the same thread).
    /// </summary>
    public static IDisposable Session(GameInstall game)
    {
        Monitor.Enter(SessionLock);
        try { new RpfService(game).TryEnsureKeys(); }
        catch { }
        return new SessionScope();
    }

    sealed class SessionScope : IDisposable
    {
        int released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0) Monitor.Exit(SessionLock);
        }
    }

    /// <summary>Makes the next EnsureKeys reload (after another edition's keys were loaded for a read-only pass).</summary>
    public static void ForgetKeys()
    {
        lock (KeyLock) { loadedKeysFor = null; failedKeysFor = null; }
    }

    /// <summary>
    /// Loads keys before the first scan: an OPEN archive can still hold NG-encrypted nested archives
    /// (scaleform_frontend.rpf inside update.rpf), and those can only be read with keys already loaded.
    /// A missing or unreadable exe (tests, a broken install) just means encrypted content stays unreadable.
    /// </summary>
    internal void TryEnsureKeys()
    {
        if (failedKeysFor == game.Id) return;
        try { EnsureKeys(); }
        catch { failedKeysFor = game.Id; }
    }

    public RpfFile Open(string physicalPath)
    {
        physicalPath = Path.GetFullPath(physicalPath);
        if (!File.Exists(physicalPath)) throw new FileNotFoundException("Archive not found", physicalPath);

        var info = new FileInfo(physicalPath);
        if (cache.TryGetValue(physicalPath, out var hit) && hit.Length == info.Length && hit.Written == info.LastWriteTimeUtc)
            return hit.Rpf;

        var rel = physicalPath.StartsWith(game.Root, StringComparison.OrdinalIgnoreCase)
            ? game.ToRelative(physicalPath)
            : Path.GetFileName(physicalPath);

        TryEnsureKeys();
        var rpf = Scan(physicalPath, rel);
        if (rpf.Root == null)
            throw new InvalidDataException($"Couldn't read {rel}: {rpf.LastError}" +
                (failedKeysFor == game.Id ? " (the game's keys couldn't be read from " + game.ExeName + ")" : ""));
        Remember(physicalPath, rpf);
        return rpf;
    }

    void Remember(string physicalPath, RpfFile rpf)
    {
        var info = new FileInfo(physicalPath);
        cache[physicalPath] = (rpf, info.Length, info.LastWriteTimeUtc);
    }

    /// <summary>Call after writing through a cached archive so the next Open reuses it.</summary>
    void Touched(RpfFile top)
    {
        var path = top.GetPhysicalFilePath();
        if (cache.ContainsKey(path)) Remember(path, top);
    }

    public void Forget(string physicalPath) => cache.Remove(Path.GetFullPath(physicalPath));

    static RpfFile Scan(string path, string rel)
    {
        var errors = new List<string>();
        var rpf = new RpfFile(path, rel);
        rpf.ScanStructure(null, e => errors.Add(e));
        if (rpf.Root == null && errors.Count > 0) rpf.LastError = string.Join("; ", errors);
        return rpf;
    }

    /// <summary>
    /// Returns mods\&lt;rel&gt; for a game archive, copying the original there first if needed.
    /// <paramref name="created"/> tells the caller to journal the new file.
    /// </summary>
    public string EnsureModsCopy(string gameRelativeRpf, out bool created, IProgress<double>? progress = null)
    {
        gameRelativeRpf = gameRelativeRpf.Replace('/', '\\').TrimStart('\\');
        if (gameRelativeRpf.StartsWith("mods\\", StringComparison.OrdinalIgnoreCase))
            gameRelativeRpf = gameRelativeRpf.Substring(5);

        var modsPath = Path.Combine(game.ModsDir, gameRelativeRpf);
        created = false;
        if (File.Exists(modsPath)) return modsPath;

        var original = game.Resolve(gameRelativeRpf);
        if (!File.Exists(original))
            throw new FileNotFoundException($"The game has no {gameRelativeRpf} to copy into mods", original);

        Directory.CreateDirectory(Path.GetDirectoryName(modsPath)!);
        var temp = modsPath + ".vml-copying";
        CopyWithProgress(original, temp, progress);
        File.Move(temp, modsPath, overwrite: true);
        created = true;
        return modsPath;
    }

    static void CopyWithProgress(string from, string to, IProgress<double>? progress)
    {
        const int buf = 4 << 20;
        using var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read, buf);
        using var dst = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None, buf);
        var buffer = new byte[buf];
        long total = Math.Max(1, src.Length), done = 0;
        int n;
        while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
        {
            dst.Write(buffer, 0, n);
            done += n;
            progress?.Report((double)done / total);
        }
    }

    // ---------------------------------------------------------------- entry paths

    /// <summary>A resolved location inside an archive: the (possibly nested) archive and its directory.</summary>
    public sealed record Location(RpfFile Archive, RpfDirectoryEntry Directory, string FileName, RpfFileEntry? Entry);

    /// <summary>
    /// Walks "x64/levels/gta5/vehicles.rpf/adder.yft": plain segments are directories,
    /// segments ending in .rpf step into the nested archive.
    /// </summary>
    public Location Locate(RpfFile top, string innerPath, bool createDirs)
    {
        var parts = innerPath.Replace('\\', '/').Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new ArgumentException("Empty path inside archive");

        var archive = top;
        var dir = top.Root;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            var seg = parts[i];
            var lower = seg.ToLowerInvariant();
            if (lower.EndsWith(".rpf"))
            {
                var child = archive.Children?.FirstOrDefault(c =>
                    c.ParentFileEntry != null && c.ParentFileEntry.Parent == dir && c.Name.Equals(seg, StringComparison.OrdinalIgnoreCase));
                if (child == null || child.Root == null)
                    throw new FileNotFoundException($"No readable nested archive {seg} in {dir.Path}" +
                        (child != null ? " (it's encrypted and the game's keys aren't loaded)" : ""));
                archive = child;
                dir = child.Root;
                continue;
            }
            var next = dir.Directories.FirstOrDefault(d => d.NameLower == lower);
            if (next == null)
            {
                if (!createDirs) throw new DirectoryNotFoundException($"No folder {seg} in {dir.Path}");
                MakeEditable(archive);
                next = RpfFile.CreateDirectory(dir, seg);
            }
            dir = next;
        }

        var name = parts[^1];
        var entry = dir.Files.FirstOrDefault(f => f.NameLower == name.ToLowerInvariant());
        return new Location(archive, dir, name, entry);
    }

    /// <summary>
    /// Switches the archive (and its parents) to OPEN, like OpenIV and CodeWalker do before an edit.
    /// Entries that were NG-encrypted keep their own encryption, which the game still reads.
    /// </summary>
    public void MakeEditable(RpfFile archive)
    {
        if (RpfFile.IsValidEncryption(archive)) return;
        if (archive.GetTopParent().Encryption != RpfEncryption.OPEN || archive.Encryption != RpfEncryption.OPEN)
            EnsureKeys();
        RpfFile.EnsureValidEncryption(archive, _ => true, recursive: false);
    }

    /// <summary>Raw bytes as they'd sit on disk (resources keep their RSC7 header).</summary>
    public static byte[] ReadRaw(RpfFile archive, RpfFileEntry entry)
    {
        var data = archive.ExtractFile(entry)
                   ?? throw new IOException($"Couldn't read {entry.Path}: {archive.LastError}");
        if (entry is RpfResourceFileEntry res)
        {
            data = ResourceBuilder.Compress(data);
            data = ResourceBuilder.AddResourceHeader(res, data);
        }
        return data;
    }

    public byte[]? TryRead(string physicalRpf, string innerPath)
    {
        var top = Open(physicalRpf);
        try
        {
            var loc = Locate(top, innerPath, createDirs: false);
            return loc.Entry == null ? null : ReadRaw(loc.Archive, loc.Entry);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    /// <summary>Writes (adds or replaces) one file. Returns the bytes it replaced, or null if it was new.</summary>
    public byte[]? Write(string physicalRpf, string innerPath, byte[] data)
    {
        var top = Open(physicalRpf);
        try
        {
            var loc = Locate(top, innerPath, createDirs: true);
            MakeEditable(loc.Archive);
            byte[]? previous = loc.Entry != null ? ReadRaw(loc.Archive, loc.Entry) : null;
            // CreateFile may mangle the buffer (huge resources get their size packed into the header)
            RpfFile.CreateFile(loc.Directory, loc.FileName, (byte[])data.Clone(), overwrite: true);
            Touched(top);
            return previous;
        }
        catch
        {
            Forget(physicalRpf); // the in-memory tree may no longer match the file
            throw;
        }
    }

    /// <summary>Deletes one file. Returns its bytes, or null if it wasn't there.</summary>
    public byte[]? Delete(string physicalRpf, string innerPath)
    {
        var top = Open(physicalRpf);
        Location loc;
        try { loc = Locate(top, innerPath, createDirs: false); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        if (loc.Entry == null) return null;
        try
        {
            MakeEditable(loc.Archive);
            var previous = ReadRaw(loc.Archive, loc.Entry);
            RpfFile.DeleteEntry(loc.Entry);
            Touched(top);
            return previous;
        }
        catch
        {
            Forget(physicalRpf);
            throw;
        }
    }

    /// <summary>
    /// Hash that survives CodeWalker's round trip: resources are recompressed on read, so for RSC7
    /// data hash the header flags plus the decompressed body instead of the raw bytes.
    /// </summary>
    public static string ContentHash(byte[] data)
    {
        if (data.Length >= 16 && BitConverter.ToUInt32(data, 0) == 0x37435352) // 'RSC7'
        {
            try
            {
                var body = ResourceBuilder.Decompress(data.AsSpan(16).ToArray());
                using var sha = System.Security.Cryptography.SHA256.Create();
                sha.TransformBlock(data, 4, 12, null, 0);
                sha.TransformFinalBlock(body, 0, body.Length);
                return "rsc:" + Convert.ToHexString(sha.Hash!);
            }
            catch { }
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));
    }

    /// <summary>Reads setup2.xml from a dlc.rpf to learn the pack's real name.</summary>
    public string? ReadDlcPackName(string dlcRpf)
    {
        try
        {
            var data = TryRead(dlcRpf, "setup2.xml");
            if (data == null) return null;
            var xml = System.Text.Encoding.UTF8.GetString(data);
            var doc = new System.Xml.XmlDocument();
            doc.LoadXml(xml.TrimStart('﻿'));
            var name = doc.SelectSingleNode("//nameHash")?.InnerText?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                name = doc.SelectSingleNode("//deviceName")?.InnerText?.Trim();
                if (name != null && name.StartsWith("dlc_", StringComparison.OrdinalIgnoreCase)) name = name.Substring(4);
            }
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch { return null; }
    }
}
