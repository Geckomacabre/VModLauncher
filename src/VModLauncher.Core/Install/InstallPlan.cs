namespace VModLauncher.Core.Install;

public enum OpKind
{
    /// <summary>Copy a loose file to a game-relative path (scripts\, plugins\, root, ...).</summary>
    CopyFile,
    /// <summary>Copy a dlc.rpf into the dlcpacks folder and register it in dlclist.xml.</summary>
    DlcPack,
    /// <summary>Put a file inside a game archive (copied into mods\ first).</summary>
    RpfFile,
    /// <summary>Run an OpenIV package (.oiv).</summary>
    Oiv,
    /// <summary>Copy a save game into the Rockstar profile folder.</summary>
    SaveGame,
    /// <summary>Shown to the player, not installed (readme, screenshots, source code, unknown files).</summary>
    Skip,
}

public sealed class InstallOp
{
    public OpKind Kind { get; set; }
    /// <summary>Absolute path in the staging folder.</summary>
    public string Source { get; set; } = "";
    /// <summary>Path inside the downloaded archive, for display.</summary>
    public string SourceRel { get; set; } = "";
    /// <summary>
    /// CopyFile: game-relative file path. DlcPack: pack name. RpfFile: "game\rel.rpf|inner/path".
    /// SaveGame: file name. Oiv: package name. Skip: "".
    /// </summary>
    public string Target { get; set; } = "";
    /// <summary>Why the analyzer chose this target, shown in the plan.</summary>
    public string Reason { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>"groupId:optionId" when this op belongs to one choice of a variant group ('|'-joined when nested).</summary>
    public string? Variant { get; set; }
    /// <summary>Other targets the player can pick (replace mods found in several archives).</summary>
    public List<string> Alternatives { get; set; } = new();

    public override string ToString() => $"{Kind} {SourceRel} -> {Target} ({Reason})";
}

public sealed class VariantOption
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Recommended { get; set; }
}

public sealed class VariantGroup
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public List<VariantOption> Options { get; set; } = new();
    public string Selected { get; set; } = "";
}

public enum WarningLevel { Info, Warn, Block }

public sealed record PlanWarning(WarningLevel Level, string Message);

public sealed class InstallPlan
{
    public string ModName { get; set; } = "";
    public string ArchivePath { get; set; } = "";
    public string StagingDir { get; set; } = "";
    /// <summary>Short description such as "Script (.NET)", "Add-on vehicle", "LSPDFR plugin".</summary>
    public string Summary { get; set; } = "";
    /// <summary>From an .oiv's assembly.xml, when the download is a package.</summary>
    public string? PackageName { get; set; }
    public string? PackageVersion { get; set; }
    public string? PackageAuthor { get; set; }
    public List<InstallOp> Ops { get; set; } = new();
    public List<VariantGroup> Variants { get; set; } = new();
    public List<string> Readmes { get; set; } = new();
    /// <summary>Prerequisite ids (see Game.Prerequisites) the mod needs.</summary>
    public List<string> Requires { get; set; } = new();
    public List<PlanWarning> Warnings { get; set; } = new();

    public IEnumerable<InstallOp> ActiveOps =>
        Ops.Where(o => o.Enabled && o.Kind != OpKind.Skip && IsVariantSelected(o.Variant));

    /// <summary>True when every "group:option" tag (joined with '|') is the selected option of its group.</summary>
    public bool IsVariantSelected(string? variant)
    {
        if (string.IsNullOrEmpty(variant)) return true;
        foreach (var tag in variant.Split('|'))
        {
            var i = tag.IndexOf(':');
            if (i < 0) continue;
            var group = Variants.FirstOrDefault(g => g.Id == tag.Substring(0, i));
            if (group != null && group.Selected != tag.Substring(i + 1)) return false;
        }
        return true;
    }

    /// <summary>
    /// The player's own choice: copy this file into a game folder. With <paramref name="wholeFolder"/>, the other
    /// unplaced files from the same archive folder go along, keeping their sub-folders.
    /// </summary>
    public void SetManualTarget(int index, string gameFolder, bool wholeFolder)
    {
        if (index < 0 || index >= Ops.Count) throw new ArgumentOutOfRangeException(nameof(index));
        var folder = (gameFolder ?? "").Replace('/', '\\').Trim().Trim('\\');
        if (folder.Split('\\').Any(p => p == ".." || p.Contains(':')) || Path.IsPathRooted(folder))
            throw new ArgumentException("Pick a folder inside the game folder.");

        var op = Ops[index];
        string Dir(InstallOp o) => Path.GetDirectoryName(o.SourceRel.Replace('/', '\\')) ?? "";
        var baseDir = Dir(op);
        var chosen = new List<InstallOp> { op };
        if (wholeFolder)
            chosen.AddRange(Ops.Where(o => o != op && o.Kind == OpKind.Skip
                && (o.Reason.StartsWith("Not recognised") || o.Reason.StartsWith("Game file"))
                && (Dir(o).Equals(baseDir, StringComparison.OrdinalIgnoreCase) || Dir(o).StartsWith(baseDir + "\\", StringComparison.OrdinalIgnoreCase))));

        foreach (var o in chosen)
        {
            var sub = Path.GetRelativePath(baseDir.Length == 0 ? "." : baseDir, o.SourceRel.Replace('/', '\\'));
            if (o == op) sub = Path.GetFileName(o.SourceRel);
            o.Kind = OpKind.CopyFile;
            o.Target = folder.Length == 0 ? sub : folder + "\\" + sub;
            o.Enabled = true;
            o.Variant = null;
            o.Reason = "Placed by you";
        }

        // the "nothing could be placed" verdict no longer holds once the player placed something
        if (ActiveOps.Any())
        {
            Warnings.RemoveAll(w => w.Level == WarningLevel.Block && w.Message.StartsWith("Nothing in this download"));
            if (Summary == "Nothing to install") Summary = "Game files";
        }
    }

    public void Select(string groupId, string optionId)
    {
        var g = Variants.FirstOrDefault(v => v.Id == groupId) ?? throw new ArgumentException("No variant group " + groupId);
        if (g.Options.All(o => o.Id != optionId)) throw new ArgumentException("No option " + optionId);
        g.Selected = optionId;
    }
}
