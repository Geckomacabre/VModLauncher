using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using VModLauncher.Host;

namespace VModLauncher;

/// <summary>What a site window is for, and where its downloads go.</summary>
public sealed class DownloadRequest
{
    /// <summary>Mod page URL, or null when the window is just for browsing / signing in.</summary>
    public string? Key { get; init; }
    public string Title { get; init; } = "";
    public string Site { get; init; } = "";
    public string? Version { get; init; }
    public string? Author { get; init; }
    public string? Image { get; init; }
    /// <summary>Set when the file came from somewhere other than the mod site (shown as a warning in the plan).</summary>
    public string? SourceHost { get; init; }
}

/// <summary>
/// A real browser window on the mod site. The player goes through the site's own download page
/// (countdown, sign-in, terms) and clicks its button; the launcher only catches the finished file.
/// Passwords typed here go to the site, never to the launcher.
/// </summary>
public partial class SiteBrowserWindow : Window
{
    readonly string startUrl;
    readonly DownloadRequest request;
    int activeDownloads;
    bool gotDownload;

    public event Action<DownloadRequest, string, long, long?, string>? DownloadProgress; // request, file, received, total, state
    public event Action<DownloadRequest, string>? DownloadFinished;                       // request, path
    public event Action<DownloadRequest, string>? DownloadFailed;                         // request, reason

    /// <summary>Sites whose pop-ups may open (sign-in, file hosts); anything else is an ad and is blocked.</summary>
    static readonly Regex TrustedPopupHost = new(
        @"(^|\.)(gta5-mods\.com|lcpdfr\.com|lcpdfrusercontent\.com|discord\.com|google\.com|github\.com|githubusercontent\.com|dev-c\.com|openiv\.com|steamcommunity\.com)$",
        RegexOptions.IgnoreCase);

    public SiteBrowserWindow(string url, DownloadRequest request, string status)
    {
        InitializeComponent();
        startUrl = url;
        this.request = request;
        Heading.Text = request.Key != null ? request.Title : "Mod site";
        Title = request.Key != null ? $"Download – {request.Title}" : "Mod site";
        Status.Text = status;
        Loaded += async (_, _) =>
        {
            try { await StartAsync(); }
            catch (Exception ex)
            {
                App.Log("site-window", ex.ToString());
                Status.Text = "The site window couldn't start: " + ex.Message;
            }
        };
    }

    /// <summary>Hosts a mod's own download may come from; anything else (an ad, a redirect elsewhere) isn't labelled as the mod.</summary>
    static readonly Regex ModFileHost = new(
        @"(^|\.)(gta5-mods\.com|lcpdfr\.com|lcpdfrusercontent\.com|github\.com|githubusercontent\.com|dev-c\.com)$",
        RegexOptions.IgnoreCase);

    async Task StartAsync()
    {
        var env = await Web.GetEnvironmentAsync();
        await View.EnsureCoreWebView2Async(env);
        var core = View.CoreWebView2;
        core.Settings.IsStatusBarEnabled = true;
        core.Settings.AreDevToolsEnabled = false;

        core.SourceChanged += (_, _) => Address.Text = core.Source;
        core.HistoryChanged += (_, _) => BackButton.IsEnabled = core.CanGoBack;
        core.DocumentTitleChanged += (_, _) =>
        {
            if (request.Key == null) Heading.Text = core.DocumentTitle;
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) && TrustedPopupHost.IsMatch(u.Host) && e.IsUserInitiated)
                core.Navigate(e.Uri);
        };
        core.DownloadStarting += OnDownloadStarting;
        AdBlock.Attach(core);
        // GTA5-Mods sends a download page opened without a referrer back to the mod's page:
        // go on from there once, the way the page's own button would
        bool firstLoad = true;
        core.NavigationCompleted += async (_, e) =>
        {
            if (!firstLoad) return;
            firstLoad = false;
            if (!e.IsSuccess || request.Key == null || SamePage(core.Source, startUrl) || !SamePage(core.Source, request.Key)) return;
            try { await core.ExecuteScriptAsync($"location.href = {JsonSerializer.Serialize(startUrl)};"); }
            catch (Exception ex) { App.Log("site-window", ex.Message); }
        };
        core.Navigate(startUrl);
    }

    static bool SamePage(string a, string b) =>
        string.Equals(a.Split('#')[0].TrimEnd('/'), b.Split('#')[0].TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        // keep Edge's own download bubble out of the way; the launcher shows progress itself
        e.Handled = true;
        var name = SafeFileName(Path.GetFileName(e.ResultFilePath));
        var target = Path.Combine(App.Paths.Downloads, $"{DateTime.Now:yyyyMMdd-HHmmss}-{name}");
        e.ResultFilePath = target;

        var op = e.DownloadOperation;
        var host = Uri.TryCreate(op.Uri, UriKind.Absolute, out var du) ? du.Host : "";
        bool fromModSite = ModFileHost.IsMatch(host);
        // only a file from the site itself (or its file hosts) counts as the mod this window was opened for
        var req = request.Key != null && fromModSite
            ? request
            : new DownloadRequest
            {
                Title = Path.GetFileNameWithoutExtension(name),
                Site = SiteOf(op.Uri),
                SourceHost = fromModSite ? null : host,
            };
        activeDownloads++;
        gotDownload = true;
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = op.TotalBytesToReceive == null;
        Status.Text = $"Downloading {name}…";
        DownloadProgress?.Invoke(req, name, 0, (long?)op.TotalBytesToReceive, "downloading");

        op.BytesReceivedChanged += (_, _) =>
        {
            var total = (long?)op.TotalBytesToReceive;
            if (total is > 0) Progress.Value = 100.0 * op.BytesReceived / total.Value;
            DownloadProgress?.Invoke(req, name, op.BytesReceived, total, "downloading");
        };
        op.StateChanged += (_, _) =>
        {
            if (op.State == CoreWebView2DownloadState.InProgress) return;
            activeDownloads--;
            if (op.State == CoreWebView2DownloadState.Completed)
            {
                DownloadProgress?.Invoke(req, name, op.BytesReceived, op.BytesReceived, "done");
                DownloadFinished?.Invoke(req, target);
                Status.Text = $"Got {name}. The launcher is checking where everything goes.";
                // a window opened for one mod closes itself once its file is in
                if (request.Key != null && req == request && activeDownloads == 0) Close();
            }
            else
            {
                var reason = op.InterruptReason.ToString();
                DownloadProgress?.Invoke(req, name, op.BytesReceived, (long?)op.TotalBytesToReceive, "failed");
                DownloadFailed?.Invoke(req, reason);
                Status.Text = $"The download stopped ({reason}). Try the site's button again.";
                Progress.Visibility = Visibility.Collapsed;
            }
        };
    }

    public bool ReceivedDownload => gotDownload;

    static string SiteOf(string url) =>
        url.Contains("lcpdfr", StringComparison.OrdinalIgnoreCase) ? "lcpdfr"
        : url.Contains("gta5-mods", StringComparison.OrdinalIgnoreCase) ? "gta5mods" : "local";

    static string SafeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) name = "download.zip";
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Length > 120 ? name[^120..] : name;
    }

    void Back_Click(object sender, RoutedEventArgs e)
    {
        if (View.CoreWebView2?.CanGoBack == true) View.CoreWebView2.GoBack();
    }

    void Reload_Click(object sender, RoutedEventArgs e) => View.CoreWebView2?.Reload();

    void External_Click(object sender, RoutedEventArgs e)
    {
        if (View.CoreWebView2 != null) Web.OpenExternal(View.CoreWebView2.Source);
    }
}
