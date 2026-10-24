using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;

namespace VModLauncher.Core.Install.Oiv;

/// <summary>
/// The &lt;xml&gt; and &lt;text&gt; steps of an OIV, applied to a file's bytes.
/// Semantics follow OpenIV: steps run in document order and each acts on the first match.
/// Unlike OpenIV, an XPath that matches nothing is a warning and the rest still runs
/// (CodeWalker's Enhanced installer does the same, and packages written for it rely on that).
/// </summary>
public static class OivEdits
{
    // ================================================================ XML

    public static byte[] ApplyXml(byte[] input, XElement step, Action<string> warn)
    {
        var text = OivText.Decode(input, out var encoding, out var bom);
        var newline = OivText.NewLine(text);

        var doc = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        using (var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore }))
            doc.Load(reader);
        if (doc.DocumentElement == null) { warn("XML file has no root element"); return input; }

        bool changed = false;
        foreach (var op in step.Elements())
        {
            var kind = op.Name.LocalName.ToLowerInvariant();
            var xpath = OivPackage.Attr(op, "xpath");
            if (kind is not ("add" or "replace" or "remove"))
            {
                warn($"Unknown xml step <{op.Name.LocalName}>, skipped");
                continue;
            }
            if (string.IsNullOrWhiteSpace(xpath)) { warn($"<{kind}> without xpath, skipped"); continue; }

            XmlNode? target;
            try { target = doc.SelectSingleNode(xpath); }
            catch (XPathException ex) { warn($"Bad XPath \"{xpath}\": {ex.Message}"); continue; }
            if (target == null)
            {
                warn($"Unable to find XML node \"{xpath}\" ({kind} skipped)");
                continue;
            }

            switch (kind)
            {
                case "add":
                    changed |= Add(doc, target, op, OivPackage.Attr(op, "append") ?? "Last", warn);
                    break;
                case "replace":
                    changed |= Replace(doc, target, op, warn);
                    break;
                case "remove":
                    Remove(target);
                    changed = true;
                    break;
            }
        }
        if (!changed) return input;
        return OivText.Encode(Serialize(doc, text, newline), encoding, bom);
    }

    static List<XmlNode> ImportContent(XmlDocument doc, XElement op)
    {
        var nodes = new List<XmlNode>();
        foreach (var n in op.Nodes())
        {
            switch (n)
            {
                case XElement e:
                    var frag = doc.CreateDocumentFragment();
                    frag.InnerXml = e.ToString(SaveOptions.DisableFormatting);
                    nodes.AddRange(frag.ChildNodes.Cast<XmlNode>());
                    break;
                case XText t when !string.IsNullOrWhiteSpace(t.Value):
                    nodes.Add(doc.CreateTextNode(t.Value.Trim()));
                    break;
                case XComment c:
                    nodes.Add(doc.CreateComment(c.Value));
                    break;
            }
        }
        return nodes;
    }

    static bool Add(XmlDocument doc, XmlNode target, XElement op, string append, Action<string> warn)
    {
        var nodes = ImportContent(doc, op);
        if (nodes.Count == 0) { warn("<add> with nothing to add"); return false; }
        if (target is XmlAttribute || target is XmlDocument)
        {
            warn("<add> target must be an element");
            return false;
        }

        switch (append.Trim().ToLowerInvariant())
        {
            case "first":
            {
                var indent = ChildIndent(target);
                XmlNode? anchor = target.FirstChild is XmlWhitespace or XmlSignificantWhitespace ? target.FirstChild : null;
                foreach (var n in nodes)
                {
                    if (anchor == null)
                    {
                        target.PrependChild(n);
                        anchor = n;
                        if (indent != null) target.InsertBefore(doc.CreateWhitespace(indent), n);
                    }
                    else
                    {
                        target.InsertAfter(n, anchor);
                        if (indent != null) target.InsertAfter(doc.CreateWhitespace(indent), n);
                        anchor = indent != null ? n.NextSibling! : n;
                    }
                }
                if (indent != null && !(target.FirstChild is XmlWhitespace)) target.PrependChild(doc.CreateWhitespace(indent));
                return true;
            }
            case "before":
            case "after":
            {
                var parent = target.ParentNode ?? throw new InvalidOperationException("target has no parent");
                var indent = OwnIndent(target);
                var anchor = target;
                foreach (var n in nodes)
                {
                    if (append.Equals("before", StringComparison.OrdinalIgnoreCase))
                    {
                        parent.InsertBefore(n, target);
                        if (indent != null) parent.InsertBefore(doc.CreateWhitespace(indent), target);
                    }
                    else
                    {
                        if (indent != null)
                        {
                            var ws = doc.CreateWhitespace(indent);
                            parent.InsertAfter(ws, anchor);
                            anchor = ws;
                        }
                        parent.InsertAfter(n, anchor);
                        anchor = n;
                    }
                }
                return true;
            }
            default: // "last"
            {
                if (!append.Equals("last", StringComparison.OrdinalIgnoreCase))
                    warn($"Unknown append=\"{append}\", treated as Last");
                var indent = ChildIndent(target);
                var closing = target.LastChild is XmlWhitespace or XmlSignificantWhitespace ? target.LastChild : null;
                string? closingIndent = closing?.Value;
                if (closing == null && indent != null)
                {
                    // element had no inner whitespace (<Paths></Paths> or <Paths/>): build "\n  " for the close tag
                    closingIndent = OwnIndent(target) ?? "\n";
                }
                foreach (var n in nodes)
                {
                    if (closing != null)
                    {
                        if (indent != null) target.InsertBefore(doc.CreateWhitespace(indent), closing);
                        target.InsertBefore(n, closing);
                    }
                    else
                    {
                        if (indent != null) target.AppendChild(doc.CreateWhitespace(indent));
                        target.AppendChild(n);
                    }
                }
                if (closing == null && closingIndent != null && indent != null)
                    target.AppendChild(doc.CreateWhitespace(closingIndent));
                return true;
            }
        }
    }

    static bool Replace(XmlDocument doc, XmlNode target, XElement op, Action<string> warn)
    {
        if (target is XmlAttribute attr)
        {
            attr.Value = op.Value.Trim();
            return true;
        }
        if (target is XmlText or XmlCDataSection)
        {
            target.Value = op.Value.Trim();
            return true;
        }
        var parent = target.ParentNode;
        if (parent == null || target is XmlDocument) { warn("<replace> can't replace the document"); return false; }

        var nodes = ImportContent(doc, op);
        if (nodes.Count == 0)
        {
            // no new node given: empty the target (OpenIV replaces with nothing)
            Remove(target);
            return true;
        }
        var indent = OwnIndent(target);
        XmlNode anchor = target;
        for (int i = 0; i < nodes.Count; i++)
        {
            if (i > 0 && indent != null)
            {
                var ws = doc.CreateWhitespace(indent);
                parent.InsertAfter(ws, anchor);
                anchor = ws;
            }
            parent.InsertAfter(nodes[i], anchor);
            anchor = nodes[i];
        }
        parent.RemoveChild(target);
        return true;
    }

    static void Remove(XmlNode target)
    {
        if (target is XmlAttribute a)
        {
            a.OwnerElement?.Attributes.Remove(a);
            return;
        }
        var parent = target.ParentNode;
        if (parent == null) return;
        if (target.PreviousSibling is XmlWhitespace or XmlSignificantWhitespace) parent.RemoveChild(target.PreviousSibling!);
        parent.RemoveChild(target);
    }

    /// <summary>The whitespace that precedes this node's element children ("\n    "), if the file is indented.</summary>
    static string? ChildIndent(XmlNode parent)
    {
        foreach (XmlNode c in parent.ChildNodes)
        {
            if (c is XmlElement && c.PreviousSibling is XmlWhitespace or XmlSignificantWhitespace)
                return c.PreviousSibling!.Value;
        }
        var own = OwnIndent(parent);
        if (own == null) return null;
        var unit = DetectIndentUnit(parent.OwnerDocument ?? (XmlDocument)parent);
        return own + unit;
    }

    /// <summary>The whitespace right before this node ("\n  "), if any.</summary>
    static string? OwnIndent(XmlNode node) =>
        node.PreviousSibling is XmlWhitespace or XmlSignificantWhitespace ? node.PreviousSibling!.Value : null;

    static string DetectIndentUnit(XmlDocument doc)
    {
        var root = doc.DocumentElement;
        if (root == null) return "  ";
        foreach (XmlNode c in root.ChildNodes)
        {
            if (c is XmlWhitespace ws && ws.Value != null)
            {
                var v = ws.Value;
                var nl = v.LastIndexOf('\n');
                var unit = nl >= 0 ? v.Substring(nl + 1) : v;
                if (unit.Length > 0) return unit;
            }
        }
        return "  ";
    }

    /// <summary>
    /// Writes the document back, keeping the original prolog text (declaration, comments) byte for byte
    /// and the original line endings.
    /// </summary>
    static string Serialize(XmlDocument doc, string original, string newline)
    {
        var rootStart = FindRootStart(original);
        var prolog = rootStart > 0 ? original.Substring(0, rootStart) : "";
        var sb = new StringBuilder();
        bool afterRoot = false;
        foreach (XmlNode n in doc.ChildNodes)
        {
            if (n == doc.DocumentElement) { sb.Append(n.OuterXml); afterRoot = true; continue; }
            if (afterRoot) sb.Append(n.OuterXml);
        }
        var body = sb.ToString().Replace("\r\n", "\n");
        if (newline == "\r\n") body = body.Replace("\n", "\r\n");
        return prolog + body;
    }

    static int FindRootStart(string text)
    {
        int i = 0;
        while (true)
        {
            i = text.IndexOf('<', i);
            if (i < 0 || i + 1 >= text.Length) return 0;
            var c = text[i + 1];
            if (c == '?')
            {
                var end = text.IndexOf("?>", i, StringComparison.Ordinal);
                if (end < 0) return 0;
                i = end + 2;
            }
            else if (c == '!')
            {
                if (string.CompareOrdinal(text, i, "<!--", 0, 4) == 0)
                {
                    var end = text.IndexOf("-->", i, StringComparison.Ordinal);
                    if (end < 0) return 0;
                    i = end + 3;
                }
                else
                {
                    var end = text.IndexOf('>', i);
                    if (end < 0) return 0;
                    i = end + 1;
                }
            }
            else
            {
                return i;
            }
        }
    }

    // ================================================================ text

    public static byte[] ApplyText(byte[] input, XElement step, Action<string> warn)
    {
        var text = OivText.Decode(input, out var encoding, out var bom);
        var newline = OivText.NewLine(text);
        bool endsWithNewline = text.EndsWith("\n");
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (endsWithNewline && lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        bool changed = false;
        foreach (var op in step.Elements())
        {
            var kind = op.Name.LocalName.ToLowerInvariant();
            switch (kind)
            {
                case "insert":
                {
                    var where = (OivPackage.Attr(op, "where") ?? "After").Trim().ToLowerInvariant();
                    var content = ContentLines(op.Value);
                    if (where is "top" or "start" or "first") { lines.InsertRange(0, content); changed = true; break; }
                    if (where is "bottom" or "end" or "last") { lines.AddRange(content); changed = true; break; }
                    var anchor = OivPackage.Attr(op, "line");
                    if (anchor == null) { warn("<insert> without line, skipped"); break; }
                    var at = FindLine(lines, anchor);
                    if (at < 0) { warn($"Unable to find line \"{anchor}\" (insert skipped)"); break; }
                    lines.InsertRange(where == "before" ? at : at + 1, content);
                    changed = true;
                    break;
                }
                case "replace":
                {
                    var anchor = OivPackage.Attr(op, "line");
                    if (anchor == null) { warn("<replace> without line, skipped"); break; }
                    var at = FindLine(lines, anchor);
                    if (at < 0) { warn($"Unable to find line \"{anchor}\" (replace skipped)"); break; }
                    lines.RemoveAt(at);
                    lines.InsertRange(at, ContentLines(op.Value));
                    changed = true;
                    break;
                }
                case "delete":
                {
                    var anchor = OivPackage.Attr(op, "line") ?? op.Value.Trim();
                    var at = FindLine(lines, anchor);
                    if (at < 0) { warn($"Unable to find line \"{anchor}\" (delete skipped)"); break; }
                    lines.RemoveAt(at);
                    changed = true;
                    break;
                }
                default:
                    warn($"Unknown text step <{op.Name.LocalName}>, skipped");
                    break;
            }
        }
        if (!changed) return input;
        var result = string.Join(newline, lines) + (endsWithNewline ? newline : "");
        return OivText.Encode(result, encoding, bom);
    }

    static int FindLine(List<string> lines, string anchor)
    {
        var a = anchor.Trim();
        for (int i = 0; i < lines.Count; i++) if (lines[i] == anchor) return i;
        for (int i = 0; i < lines.Count; i++) if (lines[i].Trim() == a) return i;
        for (int i = 0; i < lines.Count; i++) if (lines[i].Trim().Equals(a, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>Step text as lines, without the blank lines the XML formatting put around it.</summary>
    static List<string> ContentLines(string value)
    {
        var lines = value.Replace("\r\n", "\n").Split('\n').ToList();
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0])) lines.RemoveAt(0);
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
        // drop the indentation the assembly.xml layout added (common leading whitespace)
        var indent = lines.Where(l => l.Trim().Length > 0)
            .Select(l => l.Length - l.TrimStart(' ', '\t').Length).DefaultIfEmpty(0).Min();
        return lines.Select(l => l.Length >= indent ? l.Substring(indent) : l.TrimStart()).ToList();
    }
}
