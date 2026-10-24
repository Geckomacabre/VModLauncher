using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VModLauncher.Core.Game;
using VModLauncher.Core.Library;

namespace VModLauncher.Core.Features;

public sealed class ProfileMod
{
    /// <summary>Library id on this PC; null for a profile imported from someone else.</summary>
    public string? Id { get; set; }
    public string Name { get; set; } = "";
    public string Source { get; set; } = "local";
    public string? PageUrl { get; set; }
    public string? Version { get; set; }
}

public sealed class Profile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "";
    public string GameId { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<ProfileMod> Mods { get; set; } = new();
}

public sealed record MissingMod(string Name, string Source, string? PageUrl, string? Version);

public sealed record ProfileApplyResult(List<string> Enabled, List<string> Disabled, List<string> Problems, List<MissingMod> Missing);

/// <summary>
/// Named sets of enabled mods (like ReSkate's profiles). Applying one turns its mods on and every
/// other mod off; disabled mods keep their download, so switching back is offline and exact.
/// </summary>
public sealed class ProfileStore
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    static readonly JsonSerializerOptions CodeJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    const string CodePrefix = "VML1:";

    readonly string file;
    public List<Profile> All { get; private set; } = new();

    public ProfileStore(string file)
    {
        this.file = file;
        if (File.Exists(file))
        {
            try { All = JsonSerializer.Deserialize<List<Profile>>(File.ReadAllText(file), Json) ?? new(); }
            catch { All = new(); }
        }
    }

    public void Save()
    {
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(All, Json));
        File.Move(tmp, file, overwrite: true);
    }

    public IEnumerable<Profile> ForGame(string gameId) =>
        All.Where(p => p.GameId == gameId).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase);

    public Profile? Find(string id) => All.FirstOrDefault(p => p.Id == id);

    /// <summary>Snapshot of the mods enabled right now (a new profile, or overwrite <paramref name="id"/>).</summary>
    public Profile SaveCurrent(ModLibrary library, GameInstall game, string name, string? id = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Give the profile a name.");
        var profile = id != null ? Find(id) ?? throw new ArgumentException("No such profile") : new Profile { GameId = game.Id };
        profile.Name = name.Trim();
        profile.UpdatedAt = DateTime.UtcNow;
        profile.Mods = library.ForGame(game.Id).Where(m => m.Enabled).OrderBy(m => m.InstalledAt).Select(ToProfileMod).ToList();
        if (id == null) All.Add(profile);
        Save();
        return profile;
    }

    public void Delete(string id)
    {
        All.RemoveAll(p => p.Id == id);
        Save();
    }

    static ProfileMod ToProfileMod(InstalledMod m) =>
        new() { Id = m.Id, Name = m.Name, Source = m.Source, PageUrl = m.PageUrl, Version = m.Version };

    static string Norm(string? url) => (url ?? "").Trim().TrimEnd('/').ToLowerInvariant();

    /// <summary>The library mod a profile entry means: same id, else same mod page, else same local name.</summary>
    public static InstalledMod? Match(ProfileMod pm, IEnumerable<InstalledMod> mods)
    {
        var list = mods.ToList();
        return (pm.Id != null ? list.FirstOrDefault(m => m.Id == pm.Id) : null)
               ?? (pm.PageUrl != null ? list.FirstOrDefault(m => m.PageUrl != null && Norm(m.PageUrl) == Norm(pm.PageUrl)) : null)
               ?? list.FirstOrDefault(m => m.PageUrl == null && pm.PageUrl == null && m.Source == pm.Source
                                           && string.Equals(m.Name, pm.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when exactly the profile's mods are the ones enabled.</summary>
    public static bool IsActive(Profile p, ModLibrary library)
    {
        var mods = library.ForGame(p.GameId).ToList();
        var wanted = p.Mods.Select(pm => Match(pm, mods)).ToList();
        if (wanted.Any(w => w == null)) return false;
        var wantedIds = wanted.Select(w => w!.Id).ToHashSet();
        return mods.All(m => m.Enabled == wantedIds.Contains(m.Id));
    }

    public static List<MissingMod> MissingFor(Profile p, ModLibrary library)
    {
        var mods = library.ForGame(p.GameId).ToList();
        return p.Mods.Where(pm => Match(pm, mods) == null)
            .Select(pm => new MissingMod(pm.Name, pm.Source, pm.PageUrl, pm.Version)).ToList();
    }

    /// <summary>Turns the profile's mods on and every other mod of this game off (disables first, so layers stack cleanly).</summary>
    public static ProfileApplyResult Apply(Profile p, ModService service, GameInstall game, Action<string, double?>? progress = null)
    {
        var library = service.Library;
        var mods = library.ForGame(game.Id).ToList();
        var wanted = new List<InstalledMod>();
        foreach (var pm in p.Mods)
        {
            var m = Match(pm, mods);
            if (m != null && !wanted.Contains(m)) wanted.Add(m);
        }
        var wantedIds = wanted.Select(m => m.Id).ToHashSet();
        var toDisable = mods.Where(m => m.Enabled && !wantedIds.Contains(m.Id)).OrderByDescending(m => m.InstalledAt).ToList();
        var toEnable = wanted.Where(m => !m.Enabled).ToList();

        var enabled = new List<string>();
        var disabled = new List<string>();
        var problems = new List<string>();
        int total = Math.Max(1, toDisable.Count + toEnable.Count), done = 0;

        foreach (var m in toDisable)
        {
            progress?.Invoke($"Turning off {m.Name}", (double)done++ / total);
            try
            {
                var issues = service.Disable(game, m);
                disabled.Add(m.Id);
                problems.AddRange(issues.Select(i => $"{m.Name}: {i}"));
            }
            catch (Exception ex) { problems.Add($"{m.Name}: {ex.Message}"); }
        }
        foreach (var m in toEnable)
        {
            progress?.Invoke($"Turning on {m.Name}", (double)done++ / total);
            try
            {
                service.Enable(game, library.Find(m.Id) ?? m);
                enabled.Add(m.Id);
            }
            catch (Exception ex) { problems.Add($"{m.Name}: {ex.Message}"); }
        }
        progress?.Invoke("Done", 1);
        return new ProfileApplyResult(enabled, disabled, problems, MissingFor(p, library));
    }

    // ---------------------------------------------------------------- share codes

    sealed class CodeBody
    {
        public int V { get; set; } = 1;
        public string Name { get; set; } = "";
        public string? Edition { get; set; }
        public List<CodeMod> Mods { get; set; } = new();
    }

    sealed class CodeMod
    {
        public string Name { get; set; } = "";
        public string Source { get; set; } = "local";
        public string? PageUrl { get; set; }
        public string? Version { get; set; }
    }

    public static string Export(Profile p, GameEdition edition)
    {
        var body = new CodeBody
        {
            Name = p.Name,
            Edition = edition.ToString(),
            Mods = p.Mods.Select(m => new CodeMod { Name = m.Name, Source = m.Source, PageUrl = m.PageUrl, Version = m.Version }).ToList(),
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(body, CodeJson);
        return CodePrefix + Convert.ToBase64String(json).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Reads a share code into a new profile for <paramref name="game"/>; returns what's not installed here.</summary>
    public (Profile Profile, List<MissingMod> Missing, string? Edition) Import(string code, ModLibrary library, GameInstall game)
    {
        code = (code ?? "").Trim();
        if (!code.StartsWith(CodePrefix, StringComparison.OrdinalIgnoreCase))
            throw new FormatException("That isn't a V Mod Launcher profile code (they start with VML1:).");
        var b64 = code.Substring(CodePrefix.Length).Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        CodeBody body;
        try { body = JsonSerializer.Deserialize<CodeBody>(Convert.FromBase64String(b64), CodeJson) ?? throw new FormatException(); }
        catch (Exception ex) when (ex is not FormatException) { throw new FormatException("The profile code is damaged (copy the whole code)."); }

        var mods = library.ForGame(game.Id).ToList();
        var profile = new Profile
        {
            Name = string.IsNullOrWhiteSpace(body.Name) ? "Imported profile" : body.Name.Trim(),
            GameId = game.Id,
            Mods = body.Mods.Select(cm =>
            {
                var pm = new ProfileMod { Name = cm.Name, Source = cm.Source, PageUrl = cm.PageUrl, Version = cm.Version };
                pm.Id = Match(pm, mods)?.Id;
                return pm;
            }).ToList(),
        };
        All.Add(profile);
        Save();
        return (profile, MissingFor(profile, library), body.Edition);
    }
}
