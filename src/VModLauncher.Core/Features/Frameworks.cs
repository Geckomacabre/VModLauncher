using System.Diagnostics;
using System.Text.Json;
using VModLauncher.Core.Game;

namespace VModLauncher.Core.Features;

public sealed record FrameworkInfo(
    string Id, string Name, string? InstalledVersion, string? LatestVersion, string ReleaseUrl,
    string ForEdition, bool CanInstall, string? AssetName, string? AssetUrl, string Repo)
{
    /// <summary>Stable page for this framework (the library uses it to treat a reinstall as an update).</summary>
    public string ReleasesPage => $"https://github.com/{Repo}/releases";
}

/// <summary>
/// The mod frameworks that publish official releases on GitHub. The launcher fetches the release zip
/// itself and then installs it through the normal plan, so it's journaled and undoable like any mod.
/// Script Hook V has no GitHub (dev-c.com only), so it isn't here.
/// </summary>
public sealed class Frameworks
{
    sealed record Def(string Id, string Name, string Repo, string ForEdition, bool NewestNotLatest);

    static readonly Def[] Defs =
    {
        new("shvdn", "Script Hook V .NET (nightly)", "scripthookvdotnet/scripthookvdotnet-nightly", "Legacy", true),
        new("shvdne", "Script Hook V .NET Enhanced", "Chiheb-Bacha/ScriptHookVDotNetEnhanced", "Enhanced", false),
        new("lemonui", "LemonUI", "LemonUIbyLemon/LemonUI", "Both", false),
        new("ragenativeui", "RAGENativeUI", "alexguirre/RAGENativeUI", "Both", false),
    };

    readonly HttpClient http;
    readonly Dictionary<string, (DateTime At, JsonElement Release)> cache = new();

    public Frameworks(HttpClient http)
    {
        this.http = http;
        if (!http.DefaultRequestHeaders.UserAgent.Any()) http.DefaultRequestHeaders.UserAgent.ParseAdd("VModLauncher");
    }

    public async Task<List<FrameworkInfo>> ListAsync(GameInstall game)
    {
        var list = new List<FrameworkInfo>();
        foreach (var d in Defs)
        {
            JsonElement? release = null;
            try { release = await LatestAsync(d); } catch { /* offline or rate-limited: still list it */ }
            string? latest = release?.GetProperty("tag_name").GetString();
            string releaseUrl = release?.GetProperty("html_url").GetString() ?? $"https://github.com/{d.Repo}/releases";
            var (assetName, assetUrl) = release is { } r ? PickAsset(r) : (null, null);
            bool editionOk = d.ForEdition == "Both" || d.ForEdition == game.Edition.ToString();
            list.Add(new FrameworkInfo(d.Id, d.Name, InstalledVersion(game, d.Id), Clean(latest), releaseUrl, d.ForEdition,
                editionOk && assetUrl != null, assetName, assetUrl, d.Repo));
        }
        return list;
    }

    /// <summary>Downloads the release zip into <paramref name="downloadsDir"/> and returns its path.</summary>
    public async Task<string> DownloadAsync(GameInstall game, string id, string downloadsDir, Action<long, long?>? progress = null, CancellationToken ct = default)
    {
        var info = (await ListAsync(game)).FirstOrDefault(f => f.Id == id) ?? throw new ArgumentException("Unknown framework " + id);
        if (!info.CanInstall || info.AssetUrl == null)
            throw new InvalidOperationException(info.ForEdition != "Both" && info.ForEdition != game.Edition.ToString()
                ? $"{info.Name} is for GTA V {info.ForEdition}."
                : $"{info.Name}: no release download found. Open {info.ReleaseUrl} instead.");

        using var resp = await http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength;
        var target = Path.Combine(downloadsDir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{info.AssetName}");
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(target))
        {
            var buf = new byte[81920];
            long done = 0;
            int n;
            var lastReport = DateTime.MinValue;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 150) { progress?.Invoke(done, total); lastReport = DateTime.UtcNow; }
            }
            progress?.Invoke(done, total ?? done);
        }
        return target;
    }

    async Task<JsonElement> LatestAsync(Def d)
    {
        if (cache.TryGetValue(d.Id, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(15)) return hit.Release;
        // the nightly repo publishes every build as a release: take the newest, not GitHub's "latest" pick
        var url = d.NewestNotLatest
            ? $"https://api.github.com/repos/{d.Repo}/releases?per_page=1"
            : $"https://api.github.com/repos/{d.Repo}/releases/latest";
        var json = await http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);
        var release = (doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement[0] : doc.RootElement).Clone();
        cache[d.Id] = (DateTime.UtcNow, release);
        return release;
    }

    static (string? Name, string? Url) PickAsset(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets)) return (null, null);
        var zips = assets.EnumerateArray()
            .Select(a => (Name: a.GetProperty("name").GetString() ?? "", Url: a.GetProperty("browser_download_url").GetString()))
            .Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .ToList();
        // skip symbol / source-only packages when a real one exists
        var best = zips.FirstOrDefault(a => !a.Name.Contains("symbol", StringComparison.OrdinalIgnoreCase) && !a.Name.Contains("source", StringComparison.OrdinalIgnoreCase));
        if (best.Url == null && zips.Count > 0) best = zips[0];
        return (best.Url == null ? null : best.Name, best.Url);
    }

    /// <summary>"v3.7.0-nightly.194" -> "3.7.0.194", "v2.2" -> "2.2".</summary>
    public static string? Clean(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var t = tag.Trim().TrimStart('v', 'V');
        return t.Replace("-nightly.", ".", StringComparison.OrdinalIgnoreCase);
    }

    static string? FileVersion(string path)
    {
        try { return File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).FileVersion : null; } catch { return null; }
    }

    static string? InstalledVersion(GameInstall game, string id)
    {
        var asi = Path.Combine(game.Root, "ScriptHookVDotNet.asi");
        bool enhancedBuild = false;
        try { enhancedBuild = File.Exists(asi) && (FileVersionInfo.GetVersionInfo(asi).ProductName ?? "").Contains("Enhanced", StringComparison.OrdinalIgnoreCase); } catch { }
        switch (id)
        {
            case "shvdn":
                return File.Exists(asi) && !enhancedBuild ? FileVersion(Path.Combine(game.Root, "ScriptHookVDotNet3.dll")) : null;
            case "shvdne":
                // SHVDNE's files carry their own numbering (asi 3.9.0.x for release 1.1.0.x)
                return enhancedBuild ? FileVersion(asi) : null;
            case "lemonui":
                return TrimZeros(FileVersion(Path.Combine(game.Root, "scripts", "LemonUI.SHVDN3.dll")));
            case "ragenativeui":
                return TrimZeros(FileVersion(Path.Combine(game.Root, "RAGENativeUI.dll")));
        }
        return null;
    }

    static string? TrimZeros(string? v)
    {
        if (v == null) return null;
        var parts = v.Split('.').ToList();
        while (parts.Count > 2 && parts[^1] == "0") parts.RemoveAt(parts.Count - 1);
        return string.Join('.', parts);
    }
}
