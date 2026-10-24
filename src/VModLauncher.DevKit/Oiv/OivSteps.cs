using System.Text;

namespace VModLauncher.DevKit.Oiv;

/// <summary>
/// Generates the update.rpf edits most add-ons need, written so they work on every install, the first and every
/// update after it, in OpenIV and in CodeWalker's OIV Package Installer:
/// <list type="bullet">
/// <item>a pack goes into dlclist.xml once. A plain &lt;add&gt; lists it again on every update; an add guarded by
/// <c>Paths[not(Item[...])]</c> matches nothing when it's listed, and OpenIV calls that FAILED ("Unable to find XML
/// Node"). So each add targets the guarded Paths <i>or</i> a scratch element, and the scratch (with whatever landed
/// in it) is removed at the end.</item>
/// <item>a gameconfig.xml value is raised to what the mod needs and never lowered (another mod may need more): the
/// replace finds the value when it's lower and the scratch when it isn't, and the remove takes whichever is left
/// over, the scratch or the second copy the replace put in its place.</item>
/// </list>
/// Every step finds its node, so no installer reports a failure. All adds come before the removes, which
/// CodeWalker's installer needs (it runs a file's adds first).
/// </summary>
public static class OivSteps
{
    public const string DefaultScratch = "VmlScratch";
    const string Config = "/fwAllConfigs/ConfigArray/Item/Config";

    /// <summary>(xpath of the parent, element name) for a <see cref="OivProject.Raise"/> key.</summary>
    public static (string Parent, string Element) RaiseTarget(string key)
    {
        key = key.Trim();
        if (key.StartsWith('/'))
        {
            var cut = key.LastIndexOf('/');
            if (cut <= 0) throw new InvalidDataException($"raise: \"{key}\" needs a parent and an element");
            return (key[..cut], key[(cut + 1)..]);
        }
        if (key.Contains('/'))
        {
            var cut = key.LastIndexOf('/');
            return ($"{Config}/{key[..cut]}", key[(cut + 1)..]);
        }
        return ($"{Config}/PoolSizes/Entries/Item[PoolName='{key}']", "PoolSize");
    }

    /// <summary>The &lt;xml path="common\data\gameconfig.xml"&gt; step raising every value, indented by <paramref name="indent"/>.</summary>
    public static string RaiseGameConfig(IEnumerable<KeyValuePair<string, int>> values, string scratch, string indent)
    {
        var sb = new StringBuilder();
        sb.Append(indent).Append("<xml path=\"common\\data\\gameconfig.xml\">\n");
        var i2 = indent + "  ";
        var i3 = i2 + "  ";
        foreach (var (key, value) in values)
        {
            if (value < 0) throw new InvalidDataException($"raise: {key} = {value}, a limit can't be negative");
            var (parent, element) = RaiseTarget(key);
            var p = X(parent);
            sb.Append(i2).Append($"<add xpath=\"{p}\" append=\"Last\">\n");
            sb.Append(i3).Append($"<{scratch} />\n");
            sb.Append(i2).Append("</add>\n");
            sb.Append(i2).Append($"<replace xpath=\"{p}/{element}[@value &lt; {value}] | {p}/{scratch}\">\n");
            sb.Append(i3).Append($"<{element} value=\"{value}\" />\n");
            sb.Append(i2).Append("</replace>\n");
            sb.Append(i2).Append($"<remove xpath=\"{p}/{scratch} | {p}/{element}[2]\" />\n");
        }
        sb.Append(indent).Append("</xml>\n");
        return sb.ToString();
    }

    /// <summary>The &lt;xml path="common\data\dlclist.xml"&gt; step adding each pack once.</summary>
    public static string RegisterPacks(IEnumerable<string> packs, string scratch, string indent)
    {
        var sb = new StringBuilder();
        var i2 = indent + "  ";
        var i3 = i2 + "  ";
        sb.Append(indent).Append("<xml path=\"common\\data\\dlclist.xml\">\n");
        sb.Append(i2).Append("<add xpath=\"/SMandatoryPacksData\" append=\"Last\">\n");
        sb.Append(i3).Append($"<{scratch} />\n");
        sb.Append(i2).Append("</add>\n");
        foreach (var raw in packs)
        {
            var item = PackItem(raw);
            sb.Append(i2).Append($"<add xpath=\"/SMandatoryPacksData/Paths[not(Item[.='{X(item)}'])] | /SMandatoryPacksData/{scratch}\" append=\"Last\">\n");
            sb.Append(i3).Append($"<Item>{X(item)}</Item>\n");
            sb.Append(i2).Append("</add>\n");
        }
        sb.Append(i2).Append($"<remove xpath=\"/SMandatoryPacksData/{scratch}\" />\n");
        sb.Append(indent).Append("</xml>\n");
        return sb.ToString();
    }

    /// <summary>"mypack" or "dlcpacks:/mypack/" → "dlcpacks:/mypack/".</summary>
    public static string PackItem(string pack)
    {
        var p = pack.Trim();
        if (p.Contains(":/")) return p.EndsWith('/') ? p : p + "/";
        if (p.Length == 0 || p.IndexOfAny(new[] { '/', '\\', '\'', '"', '<', '>' }) >= 0)
            throw new InvalidDataException($"registerPacks: \"{pack}\" isn't a pack name");
        return $"dlcpacks:/{p}/";
    }

    /// <summary>
    /// Both edits in one &lt;archive path="update\update.rpf"&gt; block (gameconfig first, then dlclist), or "" when
    /// there's nothing to register.
    /// </summary>
    public static string Registration(IReadOnlyCollection<KeyValuePair<string, int>> raise, IReadOnlyCollection<string> packs, string scratch, string indent)
    {
        if (raise.Count == 0 && packs.Count == 0) return "";
        ValidateTag(scratch);
        var sb = new StringBuilder();
        sb.Append(indent).Append("<archive path=\"update\\update.rpf\" createIfNotExist=\"True\" type=\"RPF7\">\n");
        if (raise.Count > 0) sb.Append(RaiseGameConfig(raise, scratch, indent + "  "));
        if (packs.Count > 0) sb.Append(RegisterPacks(packs, scratch, indent + "  "));
        sb.Append(indent).Append("</archive>\n");
        return sb.ToString();
    }

    static void ValidateTag(string tag)
    {
        if (tag.Length == 0 || !(char.IsLetter(tag[0]) || tag[0] == '_') || tag.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-' or '.')))
            throw new InvalidDataException($"scratchTag \"{tag}\" isn't an XML element name");
    }

    /// <summary>Escapes a double-quoted attribute or element value; single quotes (XPath strings) stay as they are.</summary>
    internal static string X(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
