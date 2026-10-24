using System.Text;
using System.Xml.Linq;
using VModLauncher.Core.Game;
using VModLauncher.Core.Install;
using VModLauncher.DevKit.Rpf;

namespace VModLauncher.DevKit.Game;

/// <summary>
/// What a game will load: the dlclist.xml it reads (mods\update\update.rpf's, else update\update.rpf's, or
/// NaturalVision's onigiri list) with packs listed twice, listed but missing, or present but not listed; and the
/// gameconfig.xml limits add-ons usually need. Read only.
/// </summary>
public sealed class RegistrationReport
{
    /// <summary>The pools add-ons most often run out of (and what map mods like Forests of San Andreas need).</summary>
    public static readonly string[] CommonPools =
    {
        "MetaDataStore", "ExprDictStore", "TxdStore", "DwdStore", "DrawableStore", "FragmentStore", "Building",
        "phInstGta", "StaticBounds", "MapDataStore", "ScenarioPointsStore", "LightEntity", "HandlingData",
    };

    public string Game { get; init; } = "";
    public string? DlcListSource { get; set; }
    public List<string> Items { get; } = new();
    public List<string> ListedTwice { get; } = new();
    public List<string> ListedButMissing { get; } = new();
    public List<string> PresentButUnlisted { get; } = new();
    public string? GameConfigSource { get; set; }
    public Dictionary<string, int> Pools { get; } = new();
    public Dictionary<string, int> ModelInfo { get; } = new();
    public List<string> Notes { get; } = new();

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Game);
        sb.AppendLine($"  dlclist.xml: {DlcListSource ?? "not found"}, {Items.Count} items");
        foreach (var i in Items) sb.AppendLine("    " + i);
        if (ListedTwice.Count > 0) sb.AppendLine("  LISTED TWICE: " + string.Join(", ", ListedTwice));
        if (ListedButMissing.Count > 0) sb.AppendLine("  listed, but no dlc.rpf found: " + string.Join(", ", ListedButMissing));
        if (PresentButUnlisted.Count > 0) sb.AppendLine("  in dlcpacks, but not listed (never loaded): " + string.Join(", ", PresentButUnlisted));
        sb.AppendLine($"  gameconfig.xml: {GameConfigSource ?? "not found"}");
        foreach (var (k, v) in Pools) sb.AppendLine($"    pool {k} = {v}");
        foreach (var (k, v) in ModelInfo) sb.AppendLine($"    ConfigModelInfo {k} = {v}");
        foreach (var n in Notes) sb.AppendLine("  " + n);
        return sb.ToString();
    }

    public static RegistrationReport Read(string gameRoot, IEnumerable<string>? pools = null)
    {
        var game = GameInstall.TryOpen(gameRoot) ?? throw new DirectoryNotFoundException("Not a GTA V folder: " + gameRoot);
        var report = new RegistrationReport { Game = game.ToString() };
        using var session = RpfService.Session(game);
        var tools = new RpfTools(game);

        string? update = File.Exists(game.ModsUpdateRpf) ? game.ModsUpdateRpf : File.Exists(game.GameUpdateRpf) ? game.GameUpdateRpf : null;
        if (update == null) report.Notes.Add("no update\\update.rpf (in mods\\ or the game)");

        string? list = null;
        if (game.HasOnigiri)
        {
            list = File.ReadAllText(game.OnigiriDlcList);
            report.DlcListSource = game.ToRelative(game.OnigiriDlcList) + " (NaturalVision's onigiri: the game reads this one)";
        }
        else if (update != null && tools.Read(update, DlcList.InnerPath) is { } bytes)
        {
            list = DlcList.Decode(bytes, out _);
            report.DlcListSource = game.ToRelative(update) + " > " + DlcList.InnerPath;
        }
        if (list != null)
        {
            report.Items.AddRange(DlcList.Items(list));
            report.ListedTwice.AddRange(report.Items.GroupBy(i => i, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key));
            var dirs = new[] { game.ModsDlcPacksDir, Path.Combine(game.Root, "update", "x64", "dlcpacks"), game.OnigiriDlcPacksDir }
                .Where(Directory.Exists).ToList();
            foreach (var item in report.Items.Distinct(StringComparer.OrdinalIgnoreCase).Where(i => i.StartsWith("dlcpacks:/", StringComparison.OrdinalIgnoreCase)))
            {
                var name = item["dlcpacks:/".Length..].Trim('/');
                if (!dirs.Any(d => File.Exists(Path.Combine(d, name, "dlc.rpf")))) report.ListedButMissing.Add(item);
            }
            var listed = report.Items.Select(i => i.Trim('/').Split('/').Last()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var d in dirs.Where(d => !d.Equals(Path.Combine(game.Root, "update", "x64", "dlcpacks"), StringComparison.OrdinalIgnoreCase)))
                foreach (var pack in Directory.GetDirectories(d).Where(p => File.Exists(Path.Combine(p, "dlc.rpf"))))
                    if (!listed.Contains(Path.GetFileName(pack))) report.PresentButUnlisted.Add(game.ToRelative(pack));
        }

        if (update != null && tools.Read(update, "common/data/gameconfig.xml") is { } config)
        {
            report.GameConfigSource = game.ToRelative(update) + " > common/data/gameconfig.xml";
            var doc = XDocument.Parse(Oiv.OivZip.Decode(config));
            var want = (pools ?? CommonPools).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool all = want.Contains("*") || want.Contains("all");
            foreach (var item in doc.Descendants("Item").Where(i => i.Element("PoolName") != null && i.Element("PoolSize") != null))
            {
                var name = item.Element("PoolName")!.Value.Trim();
                if (!all && !want.Contains(name)) continue;
                if (int.TryParse(item.Element("PoolSize")!.Attribute("value")?.Value, out var v)) report.Pools[name] = v;
                if (item.Elements("PoolSize").Count() > 1) report.Notes.Add($"pool {name} has {item.Elements("PoolSize").Count()} PoolSize elements (the game uses the first)");
            }
            foreach (var e in doc.Descendants("ConfigModelInfo").Elements())
                if (int.TryParse(e.Attribute("value")?.Value, out var v)) report.ModelInfo[e.Name.LocalName] = v;
            if (doc.Descendants().Any(e => e.Name.LocalName.EndsWith("Scratch", StringComparison.OrdinalIgnoreCase)))
                report.Notes.Add("gameconfig.xml has a leftover *Scratch element from an install");
        }
        return report;
    }
}
