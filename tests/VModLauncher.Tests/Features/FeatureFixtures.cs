using System.IO.Compression;
using System.Text;
using CodeWalker.GameFiles;
using VModLauncher.Core;
using VModLauncher.Core.Install;
using VModLauncher.Core.Library;
using Xunit;

namespace VModLauncher.Tests.Features;

static class FeatureFixtures
{
    /// <summary>Zips a folder of mod files and installs it through ModService (so the download is kept for re-enabling).</summary>
    public static InstalledMod InstallZip(Sandbox s, string name, Action<string> fill, string? pageUrl = null)
    {
        var dir = Path.Combine(s.Root, "src-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        fill(dir);
        var zip = Path.Combine(s.Root, name + "-" + Guid.NewGuid().ToString("N")[..4] + ".zip");
        ZipFile.CreateFromDirectory(dir, zip);
        var service = new ModService(s.Paths, s.Library);
        var plan = service.Prepare(s.Game, zip, name);
        Assert.DoesNotContain(plan.Warnings, w => w.Level == WarningLevel.Block);
        return service.Install(s.Game, plan, new InstalledMod { Name = name, PageUrl = pageUrl, Source = pageUrl == null ? "local" : "gta5mods" });
    }

    public static void Write(string root, string rel, string text)
    {
        var p = Path.Combine(root, rel.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    /// <summary>A dlc.rpf with extra files (path inside -> text), e.g. a vehicles.meta.</summary>
    public static void MakePack(string path, string packName, params (string Inner, string Text)[] files)
    {
        Sandbox.MakeDlcRpf(path, packName);
        var rpf = new RpfFile(path, Path.GetFileName(path));
        rpf.ScanStructure(null, null);
        foreach (var (inner, text) in files)
        {
            var parts = inner.Split('/');
            var dir = rpf.Root;
            foreach (var seg in parts.Take(parts.Length - 1))
                dir = dir.Directories.FirstOrDefault(d => d.NameLower == seg.ToLowerInvariant()) ?? RpfFile.CreateDirectory(dir, seg);
            RpfFile.CreateFile(dir, parts[^1], Encoding.UTF8.GetBytes(text), true);
        }
    }

    public static string DlcListText(params string[] items) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<SMandatoryPacksData>\r\n\t<Paths>\r\n"
        + string.Concat(items.Select(i => $"\t\t<Item>{i}</Item>\r\n"))
        + "\t</Paths>\r\n</SMandatoryPacksData>\r\n";
}
