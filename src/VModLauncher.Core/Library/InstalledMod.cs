namespace VModLauncher.Core.Library;

/// <summary>One change made to the game, with what's needed to undo it.</summary>
public sealed class JournalEntry
{
    public const string File = "file";           // Path = game-relative file; Backup = copy of what was there (null = new file)
    public const string Dir = "dir";             // Path = game-relative folder we created
    public const string DlcItem = "dlc-item";    // Path = list file (game-relative); Item = dlcpacks:/x/; Added = we put it there
    public const string DlcRemoved = "dlc-removed"; // Path = list file; Item = an item a package took out (undo adds it back)
    public const string RpfEntry = "rpf-entry";  // Path = game-relative archive; Inner = path inside; Backup = previous bytes (null = new)
    public const string RpfCopy = "rpf-copy";    // Path = game-relative mods\ archive we copied from the game
    public const string RpfNew = "rpf-new";      // Path = game-relative archive we created from nothing
    public const string External = "external";   // Path = absolute path outside the game (save games)

    public string Type { get; set; } = "";
    public string Path { get; set; } = "";
    public string? Inner { get; set; }
    public string? Item { get; set; }
    public string? Backup { get; set; }
    /// <summary>SHA-256 of what we wrote, so uninstall can tell if someone changed it since.</summary>
    public string? Hash { get; set; }
    /// <summary>DlcItem only: false when the item was already listed (by the player, another tool or another mod).</summary>
    public bool? Added { get; set; }
    /// <summary>DlcRemoved only: the item it came after, so putting it back keeps the load order.</summary>
    public string? After { get; set; }

    public override string ToString() => $"{Type} {Path}{(Inner != null ? "|" + Inner : "")}{(Item != null ? " " + Item : "")}";
}

public sealed class InstalledMod
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    /// <summary>gta5mods, lcpdfr or local.</summary>
    public string Source { get; set; } = "local";
    public string? PageUrl { get; set; }
    public string? Version { get; set; }
    public string? Author { get; set; }
    public string? ImageUrl { get; set; }
    public string Summary { get; set; } = "";
    public string GameId { get; set; } = "";
    public DateTime InstalledAt { get; set; } = DateTime.UtcNow;
    public bool Enabled { get; set; } = true;
    /// <summary>Kept copy of the downloaded archive, so the mod can be re-enabled or reinstalled offline.</summary>
    public string? ArchivePath { get; set; }
    /// <summary>Variant choices made at install ("groupId" -> "optionId").</summary>
    public Dictionary<string, string> Choices { get; set; } = new();
    /// <summary>Ops the player switched off in the plan (by SourceRel).</summary>
    public List<string> DisabledOps { get; set; } = new();
    /// <summary>
    /// Exactly what was installed: SourceRel -> "Kind|Target". Re-enabling replays these, so replace files
    /// placed with the game scan, hand-placed files and picked alternatives come back the same.
    /// </summary>
    public Dictionary<string, string> OpTargets { get; set; } = new();
    /// <summary>An uninstall or disable couldn't undo everything; the remaining changes are still in Entries.</summary>
    public bool NeedsCleanup { get; set; }
    public List<JournalEntry> Entries { get; set; } = new();
    /// <summary>Model names from the mod's vehicles/peds/weapons meta files (what trainers spawn by name).</summary>
    public List<string>? SpawnNames { get; set; }
    /// <summary>vehicle, ped, weapon, map, script or other.</summary>
    public string? Kind { get; set; }

    public override string ToString() => $"{Name} ({Source}, {Entries.Count} changes)";
}
