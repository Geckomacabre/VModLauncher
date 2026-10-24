using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodeWalker.GameFiles;
using CodeWalker.Utils;
using VModLauncher.Core.Game;
using VModLauncher.Core.Install;

namespace VModLauncher.Host;

/// <summary>
/// Real GTA V artwork for the launcher's backgrounds, read from the player's own installed game
/// (the loading-screen paintings, newswire backgrounds and character-select art) and cached as
/// JPEG/PNG under the launcher's data. Nothing of Rockstar's ships with the launcher.
/// </summary>
public sealed class ArtService
{
    /// <summary>Hero = full-bleed newswire character art; Panels = the tilted-panel loading-screen paintings.</summary>
    public sealed record ArtSet(List<string> Hero, List<string> Panels, string Status);

    sealed record Index(int V, string Source, List<string> Hero, List<string> Panels);

    public string Folder { get; }
    public ArtSet Current { get; private set; }
    public event Action<ArtSet>? Ready;
    Task? job;

    static readonly Regex HeroName = new(@"^loadingnewsscreenbg\d+\.ytd$", RegexOptions.IgnoreCase);
    static readonly Regex PanelName = new(@"^loadingscreen\d+\.ytd$", RegexOptions.IgnoreCase);
    static readonly string[] ScaleformArchives = { "scaleform_platform_pc.rpf", "scaleform_frontend.rpf", "scaleform_generic.rpf" };

    public ArtService(string cacheRoot)
    {
        Folder = Path.Combine(cacheRoot, "art");
        Directory.CreateDirectory(Folder);
        Current = LoadIndex() is { } i ? Set(i, "ready") : new ArtSet(new(), new(), "unavailable");
    }

    static ArtSet Set(Index i, string status) => new(
        i.Hero.Select(f => "https://art.vml/" + f).ToList(),
        i.Panels.Select(f => "https://art.vml/" + f).ToList(),
        status);

    string IndexFile => Path.Combine(Folder, "index.json");

    Index? LoadIndex()
    {
        try
        {
            if (!File.Exists(IndexFile)) return null;
            var i = JsonSerializer.Deserialize<Index>(File.ReadAllText(IndexFile));
            if (i == null || i.V != 2 || i.Hero.Count + i.Panels.Count == 0 || !i.Hero.Concat(i.Panels).All(f => File.Exists(Path.Combine(Folder, f)))) return null;
            return i;
        }
        catch { return null; }
    }

    /// <summary>Starts extraction once (background) when there's no cached set yet.</summary>
    public ArtSet Ensure(IEnumerable<GameInstall> games)
    {
        if (Current.Status == "ready" || job != null) return Current;
        var candidates = games
            .Where(g => File.Exists(g.GameUpdateRpf) && new FileInfo(g.ExePath).Length > 1_000_000) // real installs only
            .OrderBy(g => g.Edition == GameEdition.Legacy ? 0 : 1)  // Legacy textures decode everywhere (no BC7)
            .ToList();
        if (candidates.Count == 0) return Current;
        Current = Current with { Status = "extracting" };
        job = Task.Run(() =>
        {
            foreach (var g in candidates)
            {
                try
                {
                    var index = Extract(g);
                    if (index.Hero.Count + index.Panels.Count == 0) continue;
                    File.WriteAllText(IndexFile, JsonSerializer.Serialize(index));
                    Current = Set(index, "ready");
                    Ready?.Invoke(Current);
                    return;
                }
                catch (Exception ex)
                {
                    App.Log("art", $"{g.Name}: {ex.Message}");
                }
                finally
                {
                    // the art pass may have loaded another edition's keys; the next archive edit reloads its own
                    RpfService.ForgetKeys();
                }
            }
            Current = Current with { Status = "unavailable" };
            Ready?.Invoke(Current);
        });
        return Current;
    }

    Index Extract(GameInstall g)
    {
        using var session = RpfService.Session(g);
        var rpf = new RpfService(g);
        rpf.EnsureKeys();
        var hero = new List<string>();
        var panels = new List<string>();
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // update.rpf holds the newest copies; the base archives fill in the rest
        var archives = new[] { @"update\update.rpf", @"update\update2.rpf" }
            .Concat(Directory.EnumerateFiles(g.Root, "x64*.rpf").Select(f => Path.GetFileName(f)!))
            .Where(a => File.Exists(g.Resolve(a)));

        foreach (var rel in archives)
        {
            RpfFile top;
            try { top = rpf.Open(g.Resolve(rel)); }
            catch { continue; }
            foreach (var archive in Walk(top).Where(a => ScaleformArchives.Contains(a.Name, StringComparer.OrdinalIgnoreCase)))
            {
                foreach (var entry in archive.AllEntries.OfType<RpfResourceFileEntry>())
                {
                    bool isHero = HeroName.IsMatch(entry.Name), isPanel = PanelName.IsMatch(entry.Name);
                    if ((!isHero && !isPanel) || !done.Add(entry.Name)) continue;
                    try
                    {
                        var data = archive.ExtractFile(entry);
                        if (data == null) continue;
                        var ytd = new YtdFile();
                        ytd.Load(data, entry);
                        var textures = ytd.TextureDict?.Textures?.data_items ?? Array.Empty<Texture>();
                        var stem = Path.GetFileNameWithoutExtension(entry.Name).ToLowerInvariant();
                        // one painting per file: the biggest texture
                        var tex = textures.Where(t => t.Width >= 640).OrderByDescending(t => t.Width * t.Height).FirstOrDefault();
                        if (tex != null && Save(tex, stem + ".jpg", jpeg: true)) (isHero ? hero : panels).Add(stem + ".jpg");
                    }
                    catch (Exception ex)
                    {
                        App.Log("art", $"{entry.Name}: {ex.Message}");
                    }
                }
            }
            rpf.Forget(g.Resolve(rel));
        }

        int Num(string f) => int.TryParse(Regex.Match(f, @"\d+").Value, out var n) ? n : 0;
        return new Index(2, g.Id, hero.OrderBy(Num).ToList(), panels.OrderBy(Num).ToList());
    }

    static IEnumerable<RpfFile> Walk(RpfFile f)
    {
        yield return f;
        if (f.Children == null) yield break;
        foreach (var c in f.Children)
            foreach (var x in Walk(c)) yield return x;
    }

    static string Safe(string? s) => Regex.Replace((s ?? "tex").ToLowerInvariant(), "[^a-z0-9_]", "_");

    bool Save(Texture tex, string fileName, bool jpeg)
    {
        var px = DDSIO.GetPixels(tex, 0); // BGRA
        if (px == null || px.Length < tex.Width * tex.Height * 4) return false;
        int w = tex.Width, h = tex.Height;
        if (jpeg)
        {
            // loading screens have no meaningful alpha; flatten onto black
            for (int i = 3; i < px.Length; i += 4) px[i] = 255;
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        BitmapEncoder enc = jpeg ? new JpegBitmapEncoder { QualityLevel = 88 } : new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        var path = Path.Combine(Folder, fileName);
        using (var fs = File.Create(path + ".tmp")) enc.Save(fs);
        File.Move(path + ".tmp", path, overwrite: true);
        return true;
    }
}
