using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VModLauncher.Core.Game;
using VModLauncher.Core.Library;

namespace VModLauncher.Core.Features;

public sealed record LogProblem(string Level, string Text, int Line);

public sealed class LogInfo
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Exists { get; set; }
    public DateTime? Modified { get; set; }
    public long Size { get; set; }
    public string Tail { get; set; } = "";
    public List<LogProblem> Problems { get; set; } = new();
}

public sealed record CrashInfo(string Path, DateTime Time, long Size);

public sealed record UnmanagedFile(string Path, string Kind, long Size, DateTime Modified, string? Note);

public sealed class DiagReport
{
    public List<LogInfo> Logs { get; set; } = new();
    public List<CrashInfo> Crashes { get; set; } = new();
    public List<UnmanagedFile> Unmanaged { get; set; } = new();
}

/// <summary>Reads the mod frameworks' logs and crash files, and finds mod files the launcher didn't install.</summary>
public static class Diagnostics
{
    /// <summary>Reads a file the game may still have open (never locks it). Returns at most the last <paramref name="maxBytes"/>.</summary>
    public static string? ReadShared(string path, int maxBytes = 256_000)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, fs.Length - maxBytes);
            fs.Seek(start, SeekOrigin.Begin);
            var buf = new byte[fs.Length - start];
            int read = 0, n;
            while (read < buf.Length && (n = fs.Read(buf, read, buf.Length - read)) > 0) read += n;
            var text = Encoding.UTF8.GetString(buf, 0, read).Replace("\r\n", "\n");
            // started mid-file: drop the partial first line
            if (start > 0) { var nl = text.IndexOf('\n'); if (nl >= 0) text = text[(nl + 1)..]; }
            return text;
        }
        catch { return null; }
    }

    static readonly (string Name, string File)[] LogFiles =
    {
        ("Script Hook V", "ScriptHookV.log"),
        ("Script Hook V .NET", "ScriptHookVDotNet.log"),
        ("ASI loader", "asiloader.log"),
        ("RAGE Plugin Hook / LSPDFR", "RagePluginHook.log"),
        ("OpenIV.asi", "OpenIV.log"),
        ("OpenRPF", "OpenRPF.log"),
        ("ELS", "ELS.log"),
        ("Menyoo", "menyooLog.txt"),
        ("ReShade", "ReShade.log"),
    };

    public static DiagReport Build(GameInstall game, ModLibrary library)
    {
        var report = new DiagReport();
        foreach (var (name, file) in LogFiles)
        {
            var path = System.IO.Path.Combine(game.Root, file);
            var info = new LogInfo { Name = name, Path = path, Exists = File.Exists(path) };
            if (!info.Exists) { if (file is "ScriptHookV.log" or "asiloader.log") report.Logs.Add(info); continue; }
            var fi = new FileInfo(path);
            info.Modified = fi.LastWriteTime;
            info.Size = fi.Length;
            var text = ReadShared(path) ?? "";
            var lines = text.Split('\n');
            info.Tail = string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 150)));
            info.Problems = Analyze(file, lines);
            report.Logs.Add(info);
        }
        report.Crashes = Crashes(game);
        report.Unmanaged = Unmanaged(game, library);
        return report;
    }

    // ---------------------------------------------------------------- log problems

    public static List<LogProblem> Analyze(string file, string[] lines)
    {
        var found = new List<LogProblem>();
        var seen = new HashSet<string>();
        void Add(string level, string text, int line)
        {
            var key = Regex.Replace(text, @"^\[[^\]]*\]\s*", "").Trim();   // ignore timestamps when de-duplicating
            if (key.Length == 0 || !seen.Add(key) || found.Count >= 30) return;
            found.Add(new LogProblem(level, text.Trim(), line + 1));
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var l = lines[i];
            switch (file.ToLowerInvariant())
            {
                case "scripthookv.log":
                    if (Regex.IsMatch(l, @"FATAL|unknown game version|not supported", RegexOptions.IgnoreCase)) Add("Block", l, i);
                    else if (Regex.IsMatch(l, @"\bERROR\b", RegexOptions.IgnoreCase)) Add("Warn", l, i);
                    break;
                case "scripthookvdotnet.log":
                    if (l.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase)) Add("Warn", l, i);
                    else if (Regex.IsMatch(l, @"Exception\b") && !l.Contains("[DEBUG]")) Add("Warn", l, i);
                    break;
                case "asiloader.log":
                    if (Regex.IsMatch(l, @"fail|error|could not|cannot|not loaded", RegexOptions.IgnoreCase)) Add("Warn", l, i);
                    break;
                case "ragepluginhook.log":
                    // LSPDFR's [TRACE]/[WARN] chatter is normal; real trouble says ERROR, crashed or unhandled
                    if (Regex.IsMatch(l, @"\[(TRACE|DEBUG|INFO|WARN)\]")) break;
                    if (Regex.IsMatch(l, @"crash|unhandled exception|was terminated|has been aborted", RegexOptions.IgnoreCase)) Add("Block", l, i);
                    else if (Regex.IsMatch(l, @"\[ERROR\]|Exception:", RegexOptions.IgnoreCase)) Add("Warn", l, i);
                    break;
                case "openiv.log":
                case "openrpf.log":
                    if (Regex.IsMatch(l, @"\berror\b|failed|exception", RegexOptions.IgnoreCase)) Add("Warn", l, i);
                    break;
                case "reshade.log":
                    if (l.Contains("| ERROR |", StringComparison.OrdinalIgnoreCase)) Add("Info", l, i);
                    break;
                default:
                    if (Regex.IsMatch(l, @"\berror\b|exception|failed", RegexOptions.IgnoreCase)) Add("Warn", l, i);
                    break;
            }
        }
        return found;
    }

    // ---------------------------------------------------------------- crash files

    public static List<CrashInfo> Crashes(GameInstall game)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var rockstar = System.IO.Path.Combine(local, "Rockstar Games", game.Edition == GameEdition.Enhanced ? "GTAV Enhanced" : "GTAV", "CrashLogs");
        var files = new List<FileInfo>();
        try { if (Directory.Exists(rockstar)) files.AddRange(new DirectoryInfo(rockstar).GetFiles()); } catch { }
        try
        {
            var dumps = System.IO.Path.Combine(local, "CrashDumps");
            // "GTA5.exe.1234.dmp" — the exact exe name, so Legacy doesn't list GTA5_Enhanced.exe dumps
            if (Directory.Exists(dumps)) files.AddRange(new DirectoryInfo(dumps).GetFiles(game.ExeName + ".*.dmp"));
        }
        catch { }
        return files.OrderByDescending(f => f.LastWriteTimeUtc).Take(10)
            .Select(f => new CrashInfo(f.FullName, f.LastWriteTime, f.Length)).ToList();
    }

    // ---------------------------------------------------------------- unmanaged files

    /// <summary>Root files Rockstar's own install ships (both editions; launcher and store builds).</summary>
    static readonly HashSet<string> VanillaCommon = new(StringComparer.OrdinalIgnoreCase)
    {
        "PlayGTAV.exe", "RedistributableUninstaller.exe", "GTAVLauncher.exe", "GTAVLanguageSelect.exe",
        "bink2w64.dll", "libcurl.dll", "XCurl.dll", "zlib1.dll", "fvad.dll", "libtox.dll", "opus.dll", "opusenc.dll",
        "steam_api64.dll", "amd_ags_x64.dll",
    };

    static readonly HashSet<string> VanillaLegacy = new(StringComparer.OrdinalIgnoreCase)
    {
        "GTA5.exe", "GTA5_BE.exe", "d3dcompiler_46.dll", "d3dcsx_46.dll", "GFSDK_ShadowLib.win64.dll", "GFSDK_TXAA.win64.dll",
        "GFSDK_TXAA_AlphaResolve.win64.dll", "GPUPerfAPIDX11-x64.dll", "NvPmApi.Core.win64.dll",
    };

    static readonly HashSet<string> VanillaEnhanced = new(StringComparer.OrdinalIgnoreCase)
    {
        "GTA5_Enhanced.exe", "GTA5_Enhanced_BE.exe", "amd_fidelityfx_dx12.dll", "dstorage.dll", "dstoragecore.dll",
        "GFSDK_Aftermath_Lib.x64.dll", "oo2core_5_win64.dll", "msdmo.dll",
    };

    static bool IsVanilla(GameInstall game, string name)
    {
        if (VanillaCommon.Contains(name)) return true;
        if (name.StartsWith("EOSSDK-Win64-Shipping", StringComparison.OrdinalIgnoreCase)) return true;
        if (game.Edition == GameEdition.Legacy) return VanillaLegacy.Contains(name);
        // Enhanced ships NVIDIA's DLSS / Streamline libraries
        return VanillaEnhanced.Contains(name)
               || Regex.IsMatch(name, @"^(nvngx_[a-z0-9_]+|sl\.[a-z0-9_]+)\.dll$", RegexOptions.IgnoreCase);
    }

    static readonly Dictionary<string, string> KnownNotes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ScriptHookV.dll"] = "Script Hook V",
        ["dinput8.dll"] = "ASI loader (Script Hook V)",
        ["xinput1_4.dll"] = "ASI loader (Script Hook V, Enhanced)",
        ["dsound.dll"] = "Another ASI loader: two loaders load every plugin twice",
        ["version.dll"] = "Another ASI loader: two loaders load every plugin twice",
        ["winmm.dll"] = "Another ASI loader: two loaders load every plugin twice",
        ["NativeTrainer.asi"] = "Script Hook V's built-in trainer",
        ["ScriptHookVDotNet.asi"] = "Script Hook V .NET",
        ["ScriptHookVDotNet2.dll"] = "Script Hook V .NET",
        ["ScriptHookVDotNet3.dll"] = "Script Hook V .NET",
        ["MinHook.x64.dll"] = "Used by Script Hook V .NET Enhanced",
        ["OpenIV.asi"] = "OpenIV's mods-folder loader",
        ["OpenRPF.asi"] = "OpenRPF mods-folder loader",
        ["Menyoo.asi"] = "Menyoo trainer",
        ["NVE.asi"] = "NaturalVision Enhanced",
        ["onigiri.asi"] = "NaturalVision Enhanced (onigiri)",
        ["ReShade.asi"] = "ReShade",
        ["dxgi.dll"] = "ReShade / ENB graphics hook",
        ["d3d11.dll"] = "ReShade / ENB graphics hook",
        ["d3d12.dll"] = "ReShade graphics hook",
        ["ELS.asi"] = "Emergency Lighting System",
        ["RAGENativeUI.dll"] = "RAGENativeUI (library for LSPDFR plugins)",
        ["AuraV.asi"] = "AuraV",
    };

    static readonly HashSet<string> RphFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "RAGEPluginHook.exe", "EasyHook.dll", "EasyHook64.dll", "EasyLoad64.dll", "DdsConvert.dll", "DiscordRpcNet.dll", "discord-rpc.dll",
        "FW1FontWrapper.dll", "Gwen.dll", "Gwen.UnitTest.dll", "LMS.Common.dll", "LMS.PortableExecutable.dll", "Microsoft.Expression.Drawing.dll",
        "Microsoft.VisualStudio.QualityTools.UnitTestFramework.dll", "Mono.Cecil.dll", "Mono.Cecil.Mdb.dll", "Mono.Cecil.Pdb.dll",
        "Mono.Cecil.Rocks.dll", "SlimDX.dll", "System.ValueTuple.dll",
    };

    static readonly HashSet<string> Loaders = new(StringComparer.OrdinalIgnoreCase) { "dinput8.dll", "xinput1_4.dll", "dsound.dll", "version.dll", "winmm.dll" };

    public static List<UnmanagedFile> Unmanaged(GameInstall game, ModLibrary library)
    {
        HashSet<string> owned;
        lock (library.Gate) // journals grow on install threads
            owned = library.ForGame(game.Id).Where(m => m.Enabled)
                .SelectMany(m => m.Entries.Where(e => e.Type == JournalEntry.File).Select(e => e.Path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = new List<UnmanagedFile>();

        UnmanagedFile Make(FileInfo f, string kind)
        {
            var rel = game.ToRelative(f.FullName);
            string? note = KnownNotes.TryGetValue(f.Name, out var n) ? n : RphFiles.Contains(f.Name) ? "Part of RAGE Plugin Hook / LSPDFR" : null;
            return new UnmanagedFile(rel, kind, f.Length, f.LastWriteTime, note);
        }

        try
        {
            foreach (var f in new DirectoryInfo(game.Root).EnumerateFiles())
            {
                var ext = f.Extension.ToLowerInvariant();
                if (ext is not (".asi" or ".dll")) continue;
                if (IsVanilla(game, f.Name) || owned.Contains(f.Name)) continue;
                list.Add(Make(f, ext == ".asi" ? "asi" : Loaders.Contains(f.Name) ? "loader" : "other"));
            }
            foreach (var (folder, kind) in new[] { ("scripts", "script"), ("plugins", "plugin") })
            {
                var dir = new DirectoryInfo(System.IO.Path.Combine(game.Root, folder));
                if (!dir.Exists) continue;
                foreach (var f in dir.EnumerateFiles("*", SearchOption.AllDirectories).Take(600))
                {
                    var rel = game.ToRelative(f.FullName);
                    if (owned.Contains(rel)) continue;
                    if (f.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase)) continue; // scripts write their own logs
                    list.Add(Make(f, f.Extension.Equals(".asi", StringComparison.OrdinalIgnoreCase) ? "asi" : kind));
                }
            }
        }
        catch { }
        // only what actually loads (plugins and scripts); their textures, sounds and configs would bury them
        return list.Where(u => Path.GetExtension(u.Path).ToLowerInvariant() is ".asi" or ".dll" or ".cs" or ".vb").ToList();
    }
}

public sealed class QuarantineItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string GameId { get; set; } = "";
    /// <summary>Game-relative path the file came from.</summary>
    public string Path { get; set; } = "";
    public DateTime MovedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Moves unmanaged files out of the game folder and back again. Nothing is ever deleted.</summary>
public sealed class Quarantine
{
    readonly string root;
    readonly string index;
    public List<QuarantineItem> Items { get; private set; } = new();

    public Quarantine(LauncherPaths paths)
    {
        root = paths.Quarantine;
        index = System.IO.Path.Combine(root, "quarantine.json");
        if (File.Exists(index))
        {
            try { Items = JsonSerializer.Deserialize<List<QuarantineItem>>(File.ReadAllText(index)) ?? new(); } catch { }
        }
    }

    void Save() => File.WriteAllText(index, JsonSerializer.Serialize(Items, new JsonSerializerOptions { WriteIndented = true }));

    string StoredFile(QuarantineItem item) => System.IO.Path.Combine(root, item.Id, System.IO.Path.GetFileName(item.Path));

    public QuarantineItem Move(GameInstall game, string gameRelative)
    {
        var full = game.Resolve(gameRelative);
        if (!full.StartsWith(game.Root + "\\", StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            throw new FileNotFoundException("That file isn't in the game folder any more.", gameRelative);
        var item = new QuarantineItem { GameId = game.Id, Path = game.ToRelative(full) };
        var stored = StoredFile(item);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(stored)!);
        File.Move(full, stored);
        Items.Add(item);
        Save();
        return item;
    }

    public void Restore(GameInstall game, string id)
    {
        var item = Items.FirstOrDefault(i => i.Id == id) ?? throw new ArgumentException("Nothing with that id is in quarantine.");
        var target = game.Resolve(item.Path);
        if (File.Exists(target)) throw new IOException($"{item.Path} is back in the game folder already; move that one away first.");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        File.Move(StoredFile(item), target);
        Items.Remove(item);
        Save();
        try { Directory.Delete(System.IO.Path.Combine(root, item.Id)); } catch { }
    }
}
