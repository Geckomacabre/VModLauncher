using System.Text;
using CodeWalker.GameFiles;
using VModLauncher.Core;
using VModLauncher.Core.Game;
using VModLauncher.Core.Library;

namespace VModLauncher.Tests;

/// <summary>Throwaway folders: a fake game, a mod layout to analyse, launcher data.</summary>
public sealed class Sandbox : IDisposable
{
    public string Root { get; }
    public string GameRoot => Path.Combine(Root, "game");
    public string ModRoot => Path.Combine(Root, "mod");
    public LauncherPaths Paths { get; }
    public ModLibrary Library { get; }

    /// <summary>Real game used for .NET reference checks (read only). Tests that need it skip when it's missing.</summary>
    public const string RealLegacy = @"D:\Games\GTAV";

    public const string OriginalDlcList =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<SMandatoryPacksData>\r\n\t<Paths>\r\n\t\t<Item>platform:/dlc_patch/patchday1ng/</Item>\r\n\t\t<Item>dlcpacks:/mpbeach/</Item>\r\n\t</Paths>\r\n</SMandatoryPacksData>\r\n";

    public Sandbox(GameEdition edition = GameEdition.Legacy, bool withModsRpf = true)
    {
        Root = Path.Combine(Path.GetTempPath(), "vml-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(GameRoot);
        Directory.CreateDirectory(ModRoot);
        File.WriteAllText(Path.Combine(GameRoot, edition == GameEdition.Enhanced ? "GTA5_Enhanced.exe" : "GTA5.exe"), "fake");
        if (withModsRpf) MakeUpdateRpf(Path.Combine(GameRoot, "mods", "update"), "update.rpf", OriginalDlcList);
        Paths = new LauncherPaths(Path.Combine(Root, "data"));
        Library = new ModLibrary(Paths);
    }

    public GameInstall Game => GameInstall.TryOpen(GameRoot)!;

    public static void MakeUpdateRpf(string dir, string name, string dlclist)
    {
        Directory.CreateDirectory(dir);
        var rpf = RpfFile.CreateNew(dir, name, RpfEncryption.OPEN);
        var common = RpfFile.CreateDirectory(rpf.Root, "common");
        var data = RpfFile.CreateDirectory(common, "data");
        RpfFile.CreateFile(data, "dlclist.xml", Encoding.UTF8.GetBytes(dlclist), true);
    }

    /// <summary>A real (tiny) dlc.rpf whose setup2.xml names the pack.</summary>
    public static void MakeDlcRpf(string path, string packName)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var rpf = RpfFile.CreateNew(dir, Path.GetFileName(path), RpfEncryption.OPEN);
        RpfFile.CreateFile(rpf.Root, "setup2.xml", Encoding.UTF8.GetBytes(
            $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<SSetupData>\n  <deviceName>dlc_{packName}</deviceName>\n  <nameHash>{packName}</nameHash>\n</SSetupData>\n"), true);
        RpfFile.CreateFile(rpf.Root, "content.xml", Encoding.UTF8.GetBytes("<CDataFileMgr__ContentsOfDataFileXml />"), true);
    }

    public string ModFile(string rel, string? text = null)
    {
        var p = Path.Combine(ModRoot, rel.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text ?? "x " + rel);
        return p;
    }

    public string ModCopy(string rel, string source)
    {
        var p = Path.Combine(ModRoot, rel.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.Copy(source, p, true);
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch { }
    }
}
