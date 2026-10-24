using System.Text;
using System.Text.RegularExpressions;
using VModLauncher.Core.Game;

namespace VModLauncher.Core.Install;

/// <summary>
/// Edits dlclist.xml as text so the rest of the file stays byte-for-byte as it was.
/// The list lives in mods\update\update.rpf (common\data\dlclist.xml), or loose in
/// onigiri\common\data\dlclist.xml when NaturalVision's onigiri.asi is installed.
/// </summary>
public static class DlcList
{
    public const string InnerPath = "common/data/dlclist.xml";

    public static string ItemFor(string packName) => $"dlcpacks:/{packName}/";

    /// <summary>The list without &lt;!-- comments --&gt;: a commented-out pack isn't registered.</summary>
    static string Live(string xml) => Regex.Replace(xml, "<!--.*?-->", "", RegexOptions.Singleline);

    public static bool Contains(string xml, string item) =>
        Regex.IsMatch(Live(xml), @"<Item>\s*" + Regex.Escape(item) + @"\s*</Item>", RegexOptions.IgnoreCase);

    public static List<string> Items(string xml) =>
        Regex.Matches(Live(xml), @"<Item>\s*([^<]+?)\s*</Item>", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value).ToList();

    /// <summary>The live item right before <paramref name="item"/>, or null when it's first or not listed.</summary>
    public static string? ItemBefore(string xml, string item)
    {
        var items = Items(xml);
        var i = items.FindIndex(x => string.Equals(x, item, StringComparison.OrdinalIgnoreCase));
        return i > 0 ? items[i - 1] : null;
    }

    /// <summary>
    /// Adds <paramref name="item"/> on the line after <paramref name="after"/> so load order comes back as it was;
    /// at the end when there's no anchor or it's gone.
    /// </summary>
    public static string Add(string xml, string item, string? after)
    {
        if (after == null || Contains(xml, item)) return Add(xml, item);
        var comments = Regex.Matches(xml, "<!--.*?-->", RegexOptions.Singleline);
        var anchor = Regex.Matches(xml, @"([ \t]*)<Item>\s*" + Regex.Escape(after) + @"\s*</Item>", RegexOptions.IgnoreCase)
            .FirstOrDefault(m => !comments.Any(c => m.Index >= c.Index && m.Index < c.Index + c.Length));
        if (anchor == null) return Add(xml, item);

        var newline = xml.Contains("\r\n") ? "\r\n" : "\n";
        var eol = xml.IndexOf('\n', anchor.Index + anchor.Length);
        var line = anchor.Groups[1].Value + "<Item>" + item + "</Item>";
        return eol < 0
            ? xml + newline + line
            : xml.Substring(0, eol + 1) + line + newline + xml.Substring(eol + 1);
    }

    public static string Add(string xml, string item)
    {
        if (Contains(xml, item)) return xml;
        var close = Regex.Match(xml, @"([ \t]*)</Paths>", RegexOptions.IgnoreCase | RegexOptions.RightToLeft);
        if (!close.Success) throw new InvalidDataException("dlclist.xml has no </Paths>");

        // indent like the last existing item; fall back to the closing tag's indent + a tab
        var last = Regex.Match(xml.Substring(0, close.Index), @"(?:^|\n)([ \t]*)<Item>", RegexOptions.IgnoreCase | RegexOptions.RightToLeft);
        var indent = last.Success ? last.Groups[1].Value : close.Groups[1].Value + "\t";
        var newline = xml.Contains("\r\n") ? "\r\n" : "\n";

        // put it on its own line right before </Paths>
        var lineStart = close.Index;
        var before = xml.Substring(0, lineStart);
        if (!before.EndsWith("\n")) before += newline;
        return before + indent + "<Item>" + item + "</Item>" + newline + xml.Substring(lineStart);
    }

    public static string Remove(string xml, string item)
    {
        var pattern = @"[ \t]*<Item>\s*" + Regex.Escape(item) + @"\s*</Item>[ \t]*\r?\n?";
        return Regex.Replace(xml, pattern, "", RegexOptions.IgnoreCase);
    }

    // ---------------------------------------------------------------- byte helpers

    public static string Decode(byte[] data, out bool bom)
    {
        bom = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF;
        return Encoding.UTF8.GetString(data, bom ? 3 : 0, data.Length - (bom ? 3 : 0));
    }

    public static byte[] Encode(string text, bool bom)
    {
        var body = Encoding.UTF8.GetBytes(text);
        if (!bom) return body;
        var outp = new byte[body.Length + 3];
        outp[0] = 0xEF; outp[1] = 0xBB; outp[2] = 0xBF;
        Buffer.BlockCopy(body, 0, outp, 3, body.Length);
        return outp;
    }
}

/// <summary>Where this game's active dlclist lives.</summary>
public sealed record DlcListTarget(string Kind, string Path)
{
    public const string Onigiri = "onigiri";
    public const string ModsUpdateRpf = "mods-update-rpf";

    /// <summary>Game-relative folder the packs for this list go in.</summary>
    public string PacksFolder => Kind == Onigiri ? @"onigiri\dlcpacks" : @"mods\update\x64\dlcpacks";

    public static DlcListTarget For(GameInstall g) =>
        g.HasOnigiri ? new DlcListTarget(Onigiri, g.OnigiriDlcList) : new DlcListTarget(ModsUpdateRpf, g.ModsUpdateRpf);
}
