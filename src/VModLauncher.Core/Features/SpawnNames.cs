using System.Text;
using System.Text.RegularExpressions;
using CodeWalker.GameFiles;
using VModLauncher.Core.Game;
using VModLauncher.Core.Install;
using VModLauncher.Core.Library;

namespace VModLauncher.Core.Features;

/// <summary>
/// The names trainers spawn things by: vehicles.meta modelName, peds.meta Name, weapons.meta WEAPON_ names.
/// Read from the mod's own dlc packs and archive changes.
/// </summary>
public static class SpawnNames
{
    static readonly Regex VehicleName = new(@"<modelName>\s*([^<\s]+)\s*</modelName>", RegexOptions.IgnoreCase);
    static readonly Regex PedName = new(@"<Item>\s*<Name>\s*([^<\s]+)\s*</Name>", RegexOptions.IgnoreCase);
    static readonly Regex WeaponName = new(@"<Name>\s*(WEAPON_[A-Z0-9_]+)\s*</Name>", RegexOptions.IgnoreCase);

    public sealed record Result(List<string> Names, string Kind);

    /// <summary>Fills mod.SpawnNames / mod.Kind (cached on the mod). Returns true when it changed them.</summary>
    public static bool Ensure(GameInstall game, InstalledMod mod)
    {
        if (mod.SpawnNames != null && mod.Kind != null) return false;
        var r = Compute(game, mod);
        mod.SpawnNames = r.Names;
        mod.Kind = r.Kind;
        return true;
    }

    public static Result Compute(GameInstall game, InstalledMod mod)
    {
        var vehicles = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var peds = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var weapons = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        using var session = RpfService.Session(game);
        var rpf = new RpfService(game);

        void Scan(string metaName, byte[]? data)
        {
            if (data == null || data.Length == 0) return;
            var text = Encoding.UTF8.GetString(data);
            if (!text.TrimStart('﻿', ' ', '\r', '\n', '\t').StartsWith("<")) return; // binary meta, not XML
            var n = metaName.ToLowerInvariant();
            if (n == "vehicles.meta") foreach (Match m in VehicleName.Matches(text)) vehicles.Add(m.Groups[1].Value);
            else if (n == "peds.meta") foreach (Match m in PedName.Matches(text)) peds.Add(m.Groups[1].Value);
            else if (n.StartsWith("weapon") && n.EndsWith(".meta")) foreach (Match m in WeaponName.Matches(text)) weapons.Add(m.Groups[1].Value.ToUpperInvariant());
        }

        static bool IsMeta(string name)
        {
            var n = name.ToLowerInvariant();
            return n == "vehicles.meta" || n == "peds.meta" || (n.StartsWith("weapon") && n.EndsWith(".meta"));
        }

        foreach (var e in mod.Entries)
        {
            try
            {
                if (e.Type == JournalEntry.File && e.Path.EndsWith("dlc.rpf", StringComparison.OrdinalIgnoreCase))
                {
                    var full = game.Resolve(e.Path);
                    if (!File.Exists(full)) continue;
                    var top = rpf.Open(full);
                    foreach (var archive in Walk(top))
                        foreach (var fe in archive.AllEntries.OfType<RpfFileEntry>().Where(f => IsMeta(f.Name)))
                            Scan(fe.Name, archive.ExtractFile(fe));
                    rpf.Forget(full);
                }
                else if (e.Type == JournalEntry.RpfEntry && e.Inner != null && IsMeta(Path.GetFileName(e.Inner)))
                {
                    var full = game.Resolve(e.Path);
                    if (File.Exists(full)) Scan(Path.GetFileName(e.Inner), rpf.TryRead(full, e.Inner));
                }
                else if (e.Type == JournalEntry.File && IsMeta(Path.GetFileName(e.Path)))
                {
                    var full = game.Resolve(e.Path);
                    if (File.Exists(full)) Scan(Path.GetFileName(e.Path), File.ReadAllBytes(full));
                }
            }
            catch { /* unreadable pack: no names, not an error */ }
        }

        var names = vehicles.Concat(peds).Concat(weapons).ToList();
        string kind = vehicles.Count > 0 ? "vehicle"
            : peds.Count > 0 ? "ped"
            : weapons.Count > 0 ? "weapon"
            : GuessKind(mod);
        return new Result(names, kind);
    }

    /// <summary>Kind from the plan's summary and the paths alone (no archive reads), for before Compute has run.</summary>
    public static string GuessKind(InstalledMod mod)
    {
        var s = (mod.Summary ?? "").ToLowerInvariant();
        if (s.Contains("menyoo") || s.Contains("map")) return "map";
        if (s.Contains("script") || s.Contains("plugin") || s.Contains("asi") || s.Contains("lspdfr") || s.Contains("script hook") || s.Contains("rage")) return "script";
        if (mod.Entries.Any(e => e.Path.StartsWith(@"menyooStuff\", StringComparison.OrdinalIgnoreCase) || e.Path.Contains(@"\AutoloadMaps\", StringComparison.OrdinalIgnoreCase))) return "map";
        if (mod.Entries.Any(e => e.Path.StartsWith(@"scripts\", StringComparison.OrdinalIgnoreCase) || e.Path.StartsWith(@"plugins\", StringComparison.OrdinalIgnoreCase) || e.Path.EndsWith(".asi", StringComparison.OrdinalIgnoreCase))) return "script";
        return "other";
    }

    static IEnumerable<RpfFile> Walk(RpfFile f)
    {
        yield return f;
        if (f.Children == null) yield break;
        foreach (var c in f.Children)
            foreach (var x in Walk(c)) yield return x;
    }
}
