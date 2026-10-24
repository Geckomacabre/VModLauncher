using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using CodeWalker.GameFiles;

namespace VModLauncher.Core.Tools;

/// <summary>One add-on car or ped (or a few of them) shipped as loose files.</summary>
public sealed class AddonSet
{
    /// <summary>"vehicle" or "ped".</summary>
    public string Kind { get; set; } = "vehicle";
    /// <summary>Folder (forward slashes, relative to the staging root) that holds the whole set.</summary>
    public string RootRel { get; set; } = "";
    /// <summary>Model names that have their model files in the mod.</summary>
    public List<string> Models { get; } = new();
    /// <summary>Model names the meta lists but the mod has no model file for.</summary>
    public List<string> MissingModels { get; } = new();
    /// <summary>Data files for common\data (vehicles.meta, handling.meta, ...).</summary>
    public List<(string Rel, string Full, string FileType)> Metas { get; } = new();
    /// <summary>Models, textures and animations for the pack's own archive.</summary>
    public List<(string Rel, string Full)> Assets { get; } = new();
    public List<string> Warnings { get; } = new();

    public IEnumerable<string> AllRels => Metas.Select(m => m.Rel).Concat(Assets.Select(a => a.Rel));
}

/// <summary>
/// Turns loose add-on cars and peds (models + vehicles.meta / peds.meta and friends) into a proper
/// add-on DLC pack: dlc.rpf with content.xml, setup2.xml, the metas in common\data and the models
/// in a nested x64\vehicles.rpf (or peds.rpf). The layout follows the dlc template in the user's
/// CodeWalker tool, the same one that loads in their game.
/// </summary>
public static class AddonPackBuilder
{
    /// <summary>Folder inside the plan's staging folder where built packs go (never analysed as part of the mod).</summary>
    public const string BuildFolder = "~vml_built";

    /// <summary>XML root element → content.xml fileType, for the data files an add-on car or ped uses.</summary>
    static readonly Dictionary<string, string> MetaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CHandlingDataMgr"] = "HANDLING_FILE",
        ["CVehicleModelInfo__InitDataList"] = "VEHICLE_METADATA_FILE",
        ["CVehicleModelInfoVarGlobal"] = "CARCOLS_FILE",
        ["CVehicleModelInfoVariation"] = "VEHICLE_VARIATION_FILE",
        ["CVehicleMetadataMgr"] = "VEHICLE_LAYOUTS_FILE",
        ["CPedModelInfo__InitDataList"] = "PED_METADATA_FILE",
    };

    /// <summary>Order the data files are listed in content.xml: whatever vehicles.meta names (handling, layouts) loads first.</summary>
    static readonly string[] MetaOrder =
        { "HANDLING_FILE", "VEHICLE_LAYOUTS_FILE", "VEHICLE_METADATA_FILE", "CARCOLS_FILE", "VEHICLE_VARIATION_FILE", "PED_METADATA_FILE" };

    static readonly HashSet<string> AssetExts = new(StringComparer.OrdinalIgnoreCase)
        { ".yft", ".ytd", ".ydd", ".ymt", ".ycd", ".ydr", ".yld", ".ypt" };

    // ---------------------------------------------------------------- detection

    /// <summary>The fileType a data file would get in content.xml, or null when it isn't one a pack needs.</summary>
    public static string? MetaType(string path)
    {
        var root = XmlRoot(path);
        return root != null && MetaTypes.TryGetValue(root, out var t) ? t : null;
    }

    /// <summary>
    /// Finds add-on sets among the loose files: a vehicles.meta (or peds.meta) whose model names have their
    /// model files next to it. A set is skipped when its models are the game's own (that's a replace mod),
    /// or when most of the listed models are missing (a full copy of the game's vehicles.meta, say).
    /// </summary>
    /// <param name="loose">Files nothing else could place (forward-slash Rel, full path).</param>
    /// <param name="isVanilla">True for a model file name the game itself already has ("adder.yft").</param>
    public static List<AddonSet> Detect(IReadOnlyList<(string Rel, string Full)> loose, Func<string, bool> isVanilla)
    {
        var sets = new List<AddonSet>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var metaTypes = loose.Where(f => IsMetaFile(f.Rel))
            .Select(f => (f.Rel, f.Full, Type: MetaType(f.Full)))
            .Where(m => m.Type != null)
            .ToList();

        foreach (var main in metaTypes.Where(m => m.Type is "VEHICLE_METADATA_FILE" or "PED_METADATA_FILE"))
        {
            if (claimed.Contains(main.Rel)) continue;
            bool ped = main.Type == "PED_METADATA_FILE";
            var names = ped ? PedNames(main.Full) : VehicleNames(main.Full);
            if (names.Count == 0) continue;
            var modelExts = ped ? new[] { ".ydd", ".yft", ".ymt" } : new[] { ".yft" };

            // look next to the meta first, then one and two folders up (data\ + stream\ layouts)
            var metaDir = Dir(main.Rel);
            List<(string Rel, string Full)>? hits = null;
            string scope = metaDir;
            for (int up = 0; up <= 2; up++)
            {
                scope = Ancestor(metaDir, up);
                if (up > 0 && scope == Ancestor(metaDir, up - 1)) break;
                hits = loose.Where(f => IsUnder(Dir(f.Rel), scope) && modelExts.Contains(Path.GetExtension(f.Rel), StringComparer.OrdinalIgnoreCase)
                                        && names.Contains(Path.GetFileNameWithoutExtension(f.Rel), StringComparer.OrdinalIgnoreCase)).ToList();
                if (hits.Count > 0) break;
            }
            if (hits == null || hits.Count == 0) continue;

            var found = names.Where(n => hits.Any(h => Path.GetFileNameWithoutExtension(h.Rel).Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
            // most of what the meta lists must be here: a full copy of the game's vehicles.meta isn't an add-on
            if (found.Count * 2 < names.Count) continue;
            // the game already has these models: it's a replace mod, the files go into the game's copy
            if (found.Any(n => modelExts.Any(e => isVanilla(n + e)))) continue;

            var root = CommonDir(hits.Select(h => Dir(h.Rel)).Append(metaDir));
            var set = new AddonSet { Kind = ped ? "ped" : "vehicle", RootRel = root };
            set.Models.AddRange(found);
            set.MissingModels.AddRange(names.Except(found, StringComparer.OrdinalIgnoreCase));

            foreach (var m in metaTypes.Where(m => IsUnder(Dir(m.Rel), root) && !claimed.Contains(m.Rel)))
            {
                // a ped pack takes ped data only; a car pack takes the car data files
                if (ped != (m.Type == "PED_METADATA_FILE")) continue;
                set.Metas.Add((m.Rel, m.Full, m.Type!));
            }
            foreach (var f in loose.Where(f => IsUnder(Dir(f.Rel), root) && AssetExts.Contains(Path.GetExtension(f.Rel)) && !claimed.Contains(f.Rel)))
                set.Assets.Add(f);

            foreach (var rel in set.AllRels) claimed.Add(rel);
            Validate(set);
            sets.Add(set);
        }
        return sets;
    }

    static bool IsMetaFile(string rel) =>
        rel.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) || rel.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

    /// <summary>Problems the player should hear about before installing (the pack still builds).</summary>
    static void Validate(AddonSet set)
    {
        foreach (var m in set.MissingModels.Take(5))
            set.Warnings.Add($"The mod's {(set.Kind == "ped" ? "peds" : "vehicles")}.meta lists \"{m}\", but there's no {m}{(set.Kind == "ped" ? ".ydd" : ".yft")} in the download, so it won't spawn.");
        if (set.Kind != "vehicle") return;

        var handlingNames = set.Metas.Where(m => m.FileType == "HANDLING_FILE")
            .SelectMany(m => Values(m.Full, "handlingName")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var vehicleMetas = set.Metas.Where(m => m.FileType == "VEHICLE_METADATA_FILE").ToList();
        var items = vehicleMetas.SelectMany(m => VehicleItems(m.Full)).ToList();
        if (handlingNames.Count > 0)
        {
            foreach (var it in items.Where(i => i.HandlingId != null && !handlingNames.Contains(i.HandlingId!)).Take(5))
                set.Warnings.Add($"\"{it.Model}\" uses the handling line \"{it.HandlingId}\", which isn't in the mod's handling.meta. It falls back to the game's own (if there is one), or the car won't load.");
        }

        // textures nothing uses: usually leftovers, sometimes a sign the names don't match
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in items)
        {
            used.Add(it.Model); used.Add(it.Model + "+hi"); used.Add(it.Model + "_hi");
            if (it.Txd != null) { used.Add(it.Txd); used.Add(it.Txd + "+hi"); }
        }
        var allMetaText = string.Join("\n", set.Metas.Select(m => SafeRead(m.Full)));
        var unused = set.Assets.Where(a => a.Rel.EndsWith(".ytd", StringComparison.OrdinalIgnoreCase))
            .Select(a => Path.GetFileNameWithoutExtension(a.Rel))
            .Where(n => !used.Contains(n) && allMetaText.IndexOf(n, StringComparison.OrdinalIgnoreCase) < 0)
            .ToList();
        if (unused.Count > 0)
            set.Warnings.Add($"No car in the mod uses {string.Join(", ", unused.Take(4).Select(u => u + ".ytd"))}{(unused.Count > 4 ? ", …" : "")}. It's packed anyway; if a car shows up untextured, its texture names don't match.");
    }

    // ---------------------------------------------------------------- naming

    /// <summary>[a-z0-9_], at most 20 characters, from the first model name; unique against <paramref name="taken"/>.</summary>
    public static string PackName(AddonSet set, ISet<string> taken)
    {
        var raw = set.Models.FirstOrDefault() ?? (set.Kind == "ped" ? "addonped" : "addoncar");
        var baseName = Regex.Replace(raw.ToLowerInvariant(), "[^a-z0-9_]", "");
        if (baseName.Length == 0) baseName = set.Kind == "ped" ? "addonped" : "addoncar";
        if (baseName.Length > 20) baseName = baseName[..20];
        var name = baseName;
        for (int i = 2; taken.Contains(name); i++)
        {
            var suffix = "_" + i;
            name = (baseName.Length + suffix.Length > 20 ? baseName[..(20 - suffix.Length)] : baseName) + suffix;
        }
        return name;
    }

    // ---------------------------------------------------------------- building

    /// <summary>Builds outDir\&lt;name&gt;\dlc.rpf for the set and returns its path.</summary>
    public static string Build(AddonSet set, string outDir, string name)
    {
        var packDir = Path.Combine(outDir, name);
        Directory.CreateDirectory(packDir);
        var dlcPath = Path.Combine(packDir, "dlc.rpf");
        if (File.Exists(dlcPath)) File.Delete(dlcPath);

        // common\data file names must be unique; two "handling.meta" from different folders get a suffix
        var dataNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var metas = set.Metas
            .OrderBy(m => Array.IndexOf(MetaOrder, m.FileType))
            .Select(m => (m.Full, m.FileType, Name: Unique(Path.GetFileName(m.Rel), dataNames)))
            .ToList();
        var assetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assets = set.Assets.Select(a => (a.Full, Name: Unique(Path.GetFileName(a.Rel), assetNames))).ToList();
        var rpfName = set.Kind == "ped" ? "peds.rpf" : "vehicles.rpf";

        var dlc = RpfFile.CreateNew(packDir, "dlc.rpf", RpfEncryption.OPEN);
        RpfFile.CreateFile(dlc.Root, "content.xml", Encoding.UTF8.GetBytes(ContentXml(name, metas.Select(m => (m.Name, m.FileType)), rpfName)), true);
        RpfFile.CreateFile(dlc.Root, "setup2.xml", Encoding.UTF8.GetBytes(SetupXml(name)), true);

        if (metas.Count > 0)
        {
            var data = RpfFile.CreateDirectory(RpfFile.CreateDirectory(dlc.Root, "common"), "data");
            foreach (var m in metas) RpfFile.CreateFile(data, m.Name, File.ReadAllBytes(m.Full), true);
        }

        var x64 = RpfFile.CreateDirectory(dlc.Root, "x64");
        var models = RpfFile.CreateNew(x64, rpfName, RpfEncryption.OPEN);
        foreach (var a in assets) RpfFile.CreateFile(models.Root, a.Name, File.ReadAllBytes(a.Full), true);
        return dlcPath;
    }

    static string Unique(string fileName, HashSet<string> used)
    {
        var name = fileName.ToLowerInvariant();
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (int i = 2; !used.Add(name); i++) name = $"{stem}_{i}{ext}";
        return name;
    }

    // ---------------------------------------------------------------- templates

    public static string ContentXml(string name, IEnumerable<(string FileName, string FileType)> metas, string rpfName)
    {
        var dev = "dlc_" + name;
        var dataFiles = new StringBuilder();
        var enable = new StringBuilder();
        void Item(string path, string type, bool persistent)
        {
            dataFiles.Append(
                "    <Item>\n" +
                "      <filename>" + path + "</filename>\n" +
                "      <fileType>" + type + "</fileType>\n" +
                "      <overlay value=\"false\" />\n" +
                "      <disabled value=\"true\" />\n" +
                "      <persistent value=\"" + (persistent ? "true" : "false") + "\" />\n" +
                "    </Item>\n");
            enable.Append("        <Item>" + path + "</Item>\n");
        }
        foreach (var (file, type) in metas) Item(dev + ":/common/data/" + file, type, persistent: false);
        Item(dev + ":/%PLATFORM%/" + rpfName, "RPF_FILE", persistent: true);

        return
"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
"<CDataFileMgr__ContentsOfDataFileXml>\n" +
"  <disabledFiles />\n" +
"  <includedXmlFiles />\n" +
"  <includedDataFiles />\n" +
"  <dataFiles>\n" +
dataFiles +
"  </dataFiles>\n" +
"  <contentChangeSets>\n" +
"    <Item>\n" +
"      <changeSetName>" + name.ToUpperInvariant() + "_AUTOGEN</changeSetName>\n" +
"      <mapChangeSetData />\n" +
"      <filesToInvalidate />\n" +
"      <filesToDisable />\n" +
"      <filesToEnable>\n" +
enable +
"      </filesToEnable>\n" +
"      <txdToLoad />\n" +
"      <txdToUnload />\n" +
"      <residentResources />\n" +
"      <unregisterResources />\n" +
"      <executionConditions />\n" +
"    </Item>\n" +
"  </contentChangeSets>\n" +
"  <patchFiles />\n" +
"</CDataFileMgr__ContentsOfDataFileXml>\n";
    }

    public static string SetupXml(string name, int order = 21) =>
"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
"<SSetupData>\n" +
"  <deviceName>dlc_" + name + "</deviceName>\n" +
"  <datFile>content.xml</datFile>\n" +
"  <timeStamp>" + DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "</timeStamp>\n" +
"  <nameHash>" + name + "</nameHash>\n" +
"  <contentChangeSets />\n" +
"  <contentChangeSetGroups>\n" +
"    <Item>\n" +
"      <NameHash>GROUP_STARTUP</NameHash>\n" +
"      <ContentChangeSets>\n" +
"        <Item>" + name.ToUpperInvariant() + "_AUTOGEN</Item>\n" +
"      </ContentChangeSets>\n" +
"    </Item>\n" +
"  </contentChangeSetGroups>\n" +
"  <startupScript />\n" +
"  <scriptCallstackSize value=\"0\" />\n" +
"  <type>EXTRACONTENT_COMPAT_PACK</type>\n" +
"  <order value=\"" + order + "\" />\n" +
"  <minorOrder value=\"0\" />\n" +
"  <isLevelPack value=\"false\" />\n" +
"  <dependencyPackHash />\n" +
"  <requiredVersion />\n" +
"  <subPackCount value=\"0\" />\n" +
"</SSetupData>\n";

    // ---------------------------------------------------------------- meta reading

    sealed record VehicleItem(string Model, string? HandlingId, string? Txd);

    static XDocument? Load(string path)
    {
        try
        {
            return XDocument.Load(path, LoadOptions.None);
        }
        catch
        {
            // some authors ship metas with a stray BOM or junk before the root
            try { return XDocument.Parse(SafeRead(path).TrimStart('﻿', ' ', '\r', '\n', '\t')); }
            catch { return null; }
        }
    }

    static List<VehicleItem> VehicleItems(string path)
    {
        var doc = Load(path);
        if (doc?.Root == null) return new();
        return doc.Descendants()
            .Where(e => e.Name.LocalName == "Item" && e.Elements().Any(c => c.Name.LocalName == "modelName"))
            .Select(e => new VehicleItem(
                e.Elements().First(c => c.Name.LocalName == "modelName").Value.Trim(),
                e.Elements().FirstOrDefault(c => c.Name.LocalName == "handlingId")?.Value.Trim(),
                e.Elements().FirstOrDefault(c => c.Name.LocalName == "txdName")?.Value.Trim()))
            .Where(i => i.Model.Length > 0)
            .ToList();
    }

    static List<string> VehicleNames(string path) =>
        VehicleItems(path).Select(i => i.Model).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>peds.meta: InitDatas/Item/Name.</summary>
    static List<string> PedNames(string path)
    {
        var doc = Load(path);
        if (doc?.Root == null) return new();
        return doc.Descendants()
            .Where(e => e.Name.LocalName == "Item" && e.Parent?.Name.LocalName == "InitDatas")
            .Select(e => e.Elements().FirstOrDefault(c => c.Name.LocalName == "Name")?.Value.Trim() ?? "")
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    static IEnumerable<string> Values(string path, string element)
    {
        var doc = Load(path);
        if (doc?.Root == null) return Array.Empty<string>();
        return doc.Descendants().Where(e => e.Name.LocalName == element).Select(e => e.Value.Trim()).Where(v => v.Length > 0).ToList();
    }

    static string? XmlRoot(string path)
    {
        try
        {
            using var r = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true });
            while (r.Read()) if (r.NodeType == XmlNodeType.Element) return r.LocalName;
        }
        catch { }
        return null;
    }

    static string SafeRead(string path)
    {
        try { return File.ReadAllText(path); } catch { return ""; }
    }

    // ---------------------------------------------------------------- paths (forward slashes, "" = staging root)

    static string Dir(string rel) => rel.Contains('/') ? rel[..rel.LastIndexOf('/')] : "";

    static string Ancestor(string dir, int levels)
    {
        for (int i = 0; i < levels && dir.Length > 0; i++) dir = Dir(dir);
        return dir;
    }

    static bool IsUnder(string dir, string root) =>
        root.Length == 0 || dir.Equals(root, StringComparison.OrdinalIgnoreCase) || dir.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);

    static string CommonDir(IEnumerable<string> dirs)
    {
        string? common = null;
        foreach (var d in dirs)
        {
            if (common == null) { common = d; continue; }
            while (!IsUnder(d, common)) common = Dir(common);
            if (common.Length == 0) break;
        }
        return common ?? "";
    }
}
