using System.Text.Json;
using System.Text.Json.Serialization;

namespace VModLauncher.Core.Library;

/// <summary>
/// The installed-mods list, saved as JSON next to the launcher's other data. Installs write to it from
/// background threads while the UI reads it, so every read hands out a snapshot and every change
/// (including journal entries, see Journal.Record) happens under <see cref="Gate"/>.
/// </summary>
public sealed class ModLibrary
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    readonly string file;
    readonly object gate = new();

    /// <summary>Lock for the list and for every mod's Entries. Re-entrant (Monitor).</summary>
    public object Gate => gate;

    /// <summary>The live list: enumerate it only while holding <see cref="Gate"/>; prefer All / ForGame.</summary>
    public List<InstalledMod> Mods { get; private set; } = new();

    public ModLibrary(LauncherPaths paths) : this(paths.LibraryFile) { }

    public ModLibrary(string file)
    {
        this.file = file;
        if (File.Exists(file))
        {
            try { Mods = JsonSerializer.Deserialize<List<InstalledMod>>(File.ReadAllText(file), Json) ?? new(); }
            catch
            {
                File.Copy(file, file + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true);
                Mods = new();
            }
        }
    }

    public void Save()
    {
        lock (gate)
        {
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Mods, Json));
            File.Move(tmp, file, overwrite: true);
        }
    }

    public List<InstalledMod> All()
    {
        lock (gate) return Mods.ToList();
    }

    public InstalledMod? Find(string id)
    {
        lock (gate) return Mods.FirstOrDefault(m => m.Id == id);
    }

    /// <summary>Snapshot of one game's mods, newest first.</summary>
    public List<InstalledMod> ForGame(string gameId)
    {
        lock (gate) return Mods.Where(m => m.GameId == gameId).OrderByDescending(m => m.InstalledAt).ToList();
    }

    public void Upsert(InstalledMod mod)
    {
        lock (gate)
        {
            var i = Mods.FindIndex(m => m.Id == mod.Id);
            if (i >= 0) Mods[i] = mod; else Mods.Add(mod);
            Save();
        }
    }

    public void Remove(InstalledMod mod)
    {
        lock (gate)
        {
            Mods.RemoveAll(m => m.Id == mod.Id);
            Save();
        }
    }

    /// <summary>The next enabled mod (same game) installed after ours with a matching journal entry: the layer on top.</summary>
    public (InstalledMod Mod, JournalEntry Entry)? LaterOwner(InstalledMod mod, Func<JournalEntry, bool> match)
    {
        lock (gate)
        {
            return Mods
                .Where(m => m.Id != mod.Id && m.GameId == mod.GameId && m.Enabled && m.InstalledAt >= mod.InstalledAt)
                .OrderBy(m => m.InstalledAt)
                .SelectMany(m => m.Entries.Where(match).Select(e => (m, e)))
                .Select(t => ((InstalledMod, JournalEntry)?)t)
                .FirstOrDefault();
        }
    }

    public bool AnyOtherUses(InstalledMod mod, Func<JournalEntry, bool> match)
    {
        lock (gate) return Mods.Any(m => m.Id != mod.Id && m.GameId == mod.GameId && m.Enabled && m.Entries.Any(match));
    }
}
