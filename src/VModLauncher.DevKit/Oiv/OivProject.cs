using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VModLauncher.DevKit.Oiv;

/// <summary>
/// An OIV package described as JSON (<c>*.oiv.json</c>): metadata, which folders go where, nested archive edits,
/// and generated steps for the two edits almost every add-on needs (packs in dlclist.xml, raised gameconfig.xml
/// limits). <see cref="OivBuilder"/> turns it into a .oiv. Paths are relative to the project file; <c>${name}</c>
/// expands from <see cref="Vars"/>, from overrides given at build time, or <c>${env:NAME}</c> from the environment.
/// See docs/devkit.md for the format.
/// </summary>
public sealed class OivProject
{
    public string? Name { get; set; }
    /// <summary>"1.2.0": major and minor go in &lt;version&gt;, the whole string in the tag unless <see cref="Tag"/> is set.</summary>
    public string? Version { get; set; }
    public string? Tag { get; set; }
    /// <summary>Legacy, Enhanced or Any. Picks the resource versions checked and, with <see cref="GameVersionElement"/>, the &lt;gameversion&gt;.</summary>
    public string? Edition { get; set; }
    /// <summary>
    /// Writes &lt;gameversion&gt; into the metadata. CodeWalker's OIV Package Installer then picks the right game by
    /// itself; without it, it asks for the game folder every time. Other installers may not know the element.
    /// </summary>
    public bool GameVersionElement { get; set; }
    public string? Author { get; set; }
    public string? AuthorLink { get; set; }
    /// <summary>Text, or "@file.txt" to read it from a file.</summary>
    public string? Description { get; set; }
    public string? LargeDescription { get; set; }
    public string? Licence { get; set; }
    public string? Icon { get; set; }
    /// <summary>The package id (a GUID in braces). Left out, one is made from the name, so it stays the same between versions.</summary>
    public string? Id { get; set; }
    public OivColors? Colors { get; set; }
    /// <summary>
    /// An existing assembly.xml to build from instead of generating the metadata: the generated steps go where it
    /// has <c>&lt;!-- vml:content --&gt;</c>, or at the end of its &lt;content&gt;. Its own steps stay.
    /// </summary>
    public string? AssemblyTemplate { get; set; }
    /// <summary>Output file; {name}, {version}, {edition} and {tag} are filled in. Default build/{name}-{version}.oiv.</summary>
    public string? Output { get; set; }
    public Dictionary<string, string>? Vars { get; set; }
    /// <summary>Loose files: each goes to the game folder (scripts, plugins, .asi) or, for game data, to mods\.</summary>
    public List<OivFiles> Files { get; set; } = new();
    /// <summary>Steps inside archives (update\update.rpf and archives nested in it).</summary>
    public List<OivArchive> Archives { get; set; } = new();
    /// <summary>dlcpacks:/name/ items to add to update.rpf's dlclist.xml, each once, however often the package is installed.</summary>
    public List<string>? RegisterPacks { get; set; }
    /// <summary>
    /// gameconfig.xml values to raise and never lower: a pool by its PoolName ("TxdStore": 150000), or a value
    /// under Config ("ConfigModelInfo/MaxExtraPedModelInfos": 600), or an absolute path ("/fwAllConfigs/.../Value").
    /// </summary>
    public Dictionary<string, int>? Raise { get; set; }
    /// <summary>The placeholder element the generated steps use (they remove it again). Default VmlScratch.</summary>
    public string? ScratchTag { get; set; }
    /// <summary>Raw OIV step XML (text or "@file.xml") appended to &lt;content&gt;.</summary>
    public List<string>? Steps { get; set; }
    /// <summary>Extensions packed without compression. Default: already-compressed formats and archives.</summary>
    public List<string>? Store { get; set; }
    /// <summary>
    /// Resource versions to enforce by extension (".ytd": 13). Default from the edition: .ytd 13 on Legacy and 5
    /// (gen9) on Enhanced, since a resource in the other edition's format crashes the game.
    /// </summary>
    public Dictionary<string, int>? ResourceVersions { get; set; }

    [JsonIgnore] public string ProjectFile { get; private set; } = "";
    [JsonIgnore] public string BaseDir => Path.GetDirectoryName(ProjectFile) ?? Directory.GetCurrentDirectory();
    readonly Dictionary<string, string> overrides = new(StringComparer.OrdinalIgnoreCase);

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static OivProject Load(string file, IReadOnlyDictionary<string, string>? set = null)
    {
        var full = Path.GetFullPath(file);
        var project = JsonSerializer.Deserialize<OivProject>(File.ReadAllText(full), Json)
                      ?? throw new InvalidDataException(file + " is empty");
        project.ProjectFile = full;
        if (set != null)
            foreach (var kv in set) project.overrides[kv.Key] = kv.Value;
        return project;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public void Save(string file)
    {
        ProjectFile = Path.GetFullPath(file);
        Directory.CreateDirectory(BaseDir);
        File.WriteAllText(ProjectFile, ToJson());
    }

    // ---------------------------------------------------------------- variables and paths

    static readonly Regex VarPattern = new(@"\$\{([^}]+)\}", RegexOptions.Compiled);

    /// <summary>Fills in ${name} and ${env:NAME}; an unknown name is an error, never an empty string.</summary>
    public string Expand(string text) => VarPattern.Replace(text, m =>
    {
        var key = m.Groups[1].Value.Trim();
        if (key.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
            return Environment.GetEnvironmentVariable(key[4..])
                   ?? throw new InvalidDataException($"Environment variable {key[4..]} isn't set (used as ${{{key}}})");
        if (overrides.TryGetValue(key, out var o)) return o;
        if (Vars != null && Vars.TryGetValue(key, out var v)) return v;
        throw new InvalidDataException($"Unknown variable ${{{key}}}: give it in \"vars\" or with --set {key}=...");
    });

    /// <summary>A path from the project, expanded and made absolute against the project's folder.</summary>
    public string ResolvePath(string path)
    {
        var p = Expand(path).Trim();
        return Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(BaseDir, p));
    }

    /// <summary>Text from the project: "@file" reads the file (relative to the project), anything else is literal.</summary>
    public string? ResolveText(string? value)
    {
        if (value == null) return null;
        if (value.StartsWith('@')) return File.ReadAllText(ResolvePath(value[1..])).Replace("\r\n", "\n");
        return Expand(value);
    }

    public string EditionName => (Edition ?? "Any").Trim() switch
    {
        var e when e.Equals("legacy", StringComparison.OrdinalIgnoreCase) => "Legacy",
        var e when e.Equals("enhanced", StringComparison.OrdinalIgnoreCase) => "Enhanced",
        var e when e.Length == 0 || e.Equals("any", StringComparison.OrdinalIgnoreCase) => "Any",
        var e => throw new InvalidDataException($"edition must be Legacy, Enhanced or Any, not \"{e}\""),
    };

    /// <summary>Resource versions to enforce: the project's own, else the edition's default for .ytd.</summary>
    public IReadOnlyDictionary<string, int> EffectiveResourceVersions()
    {
        if (ResourceVersions != null)
            return ResourceVersions.ToDictionary(kv => kv.Key.StartsWith('.') ? kv.Key.ToLowerInvariant() : "." + kv.Key.ToLowerInvariant(), kv => kv.Value);
        return EditionName switch
        {
            "Legacy" => new Dictionary<string, int> { [".ytd"] = 13 },
            "Enhanced" => new Dictionary<string, int> { [".ytd"] = 5 },
            _ => new Dictionary<string, int>(),
        };
    }

    /// <summary>A starting project for a script mod, written by <c>vml oiv init</c>.</summary>
    public static OivProject Example(string name, string edition) => new()
    {
        Name = name,
        Version = "1.0.0",
        Edition = edition,
        Author = Environment.UserName,
        Description = "What the mod does, how to use it, and what it needs (Script Hook V, Script Hook V .NET, ...).",
        Output = "build/{name}-{version}-{edition}.oiv",
        Files = new() { new OivFiles { From = "scripts", To = "scripts" } },
    };
}

public sealed class OivFiles
{
    /// <summary>A file or a folder (everything in it, recursively).</summary>
    public string From { get; set; } = "";
    /// <summary>Install path relative to the game folder ("scripts", "update/x64/dlcpacks/mypack/dlc.rpf"). Empty = the game folder.</summary>
    public string To { get; set; } = "";
    /// <summary>Where the files sit inside the package's content\ folder. Default: the same as <see cref="To"/>.</summary>
    public string? Content { get; set; }
    /// <summary>Glob patterns (relative to <see cref="From"/>, '/' separated) to leave out: "**/Save/**", "*.pdb".</summary>
    public List<string>? Exclude { get; set; }
    /// <summary>Skip quietly when <see cref="From"/> doesn't exist instead of failing the build.</summary>
    public bool Optional { get; set; }
}

public sealed class OivArchive
{
    /// <summary>The archive's path: from the game folder for a top-level one ("update/update.rpf"), else inside its parent.</summary>
    public string Path { get; set; } = "";
    /// <summary>createIfNotExist: make the archive when the game has none.</summary>
    public bool Create { get; set; }
    public string Type { get; set; } = "RPF7";
    /// <summary>Files added inside the archive; <see cref="OivFiles.To"/> is the path inside it.</summary>
    public List<OivFiles> Files { get; set; } = new();
    public List<OivArchive> Archives { get; set; } = new();
    /// <summary>Raw steps (text or "@file.xml") run inside this archive, e.g. &lt;xml path="..."&gt; edits.</summary>
    public List<string>? Steps { get; set; }
}

public sealed class OivColors
{
    /// <summary>OpenIV's "$AARRGGBB" colour of the installer's header.</summary>
    public string? HeaderBackground { get; set; }
    public bool HeaderBlackText { get; set; }
    public string? IconBackground { get; set; }
}
