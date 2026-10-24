using System.IO;
using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace VModLauncher.Host;

/// <summary>The site showed its browser check and it didn't clear by itself: the player has to see the page.</summary>
public sealed class SiteChallengeException(string url) : Exception("CHALLENGE: " + url)
{
    public string Url { get; } = url;
}

/// <summary>
/// A hidden browser tab parked on one mod site. The site extractor script (SiteScripts\*.js) runs
/// inside it and reads pages with same-origin fetch, so the site sees an ordinary browser with the
/// player's cookies, at no more than one request a second.
/// </summary>
public sealed class SiteSession : IDisposable
{
    public string Site { get; }
    public string Home { get; }
    readonly IntPtr parentHwnd;
    readonly string script;
    CoreWebView2Controller? controller;
    CoreWebView2? web;
    Task? ready;
    readonly Dictionary<string, TaskCompletionSource<JsonElement>> pending = new();

    static readonly HashSet<string> AllowedCalls = new() { "categories", "sorts", "list", "search", "detail", "searchPageUrl" };

    public SiteSession(string site, IntPtr parentHwnd)
    {
        Site = site;
        Home = site == "lcpdfr" ? "https://www.lcpdfr.com/downloads/gta5mods/" : "https://www.gta5-mods.com/";
        this.parentHwnd = parentHwnd;
        script = ReadScript(site == "lcpdfr" ? "lcpdfr.js" : "gta5mods.js");
    }

    static string ReadScript(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("SiteScripts." + name)
                      ?? throw new InvalidOperationException("Missing site script " + name);
        return new StreamReader(s).ReadToEnd();
    }

    public async Task<JsonElement> CallAsync(string fn, object? arg)
    {
        if (!AllowedCalls.Contains(fn)) throw new ArgumentException("Unknown site call " + fn);
        await EnsureReadyAsync();
        try
        {
            return await InvokeAsync(fn, arg);
        }
        catch (SiteChallengeException)
        {
            // the clearance cookie ran out: park on the site again, let its check run once, retry
            ready = null;
            await EnsureReadyAsync();
            return await InvokeAsync(fn, arg);
        }
    }

    /// <summary>Forget the parked page (after the player cleared a check in the visible window).</summary>
    public void Reset() => ready = null;

    Task EnsureReadyAsync()
    {
        if (ready == null || ready.IsFaulted || ready.IsCanceled) ready = InitAsync();
        return ready;
    }

    async Task InitAsync()
    {
        if (controller == null)
        {
            var env = await Web.GetEnvironmentAsync();
            controller = await env.CreateCoreWebView2ControllerAsync(parentHwnd);
            controller.IsVisible = false;
            controller.Bounds = new System.Drawing.Rectangle(0, 0, 1, 1);
            web = controller.CoreWebView2;
            web.Settings.AreDefaultScriptDialogsEnabled = false;
            web.Settings.IsStatusBarEnabled = false;
            web.WebMessageReceived += OnMessage;
            web.NewWindowRequested += (_, e) => e.Handled = true; // no pop-ups from a page nobody sees
            // Nobody sees this tab: it only needs the site's own HTML and scripts. Ads, trackers,
            // pictures, video and fonts are answered empty, so it never makes invisible ad views.
            web.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            web.WebResourceRequested += (_, e) =>
            {
                bool sameSite = Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var u)
                                && (u.Host.EndsWith(Domain, StringComparison.OrdinalIgnoreCase));
                bool heavy = e.ResourceContext is CoreWebView2WebResourceContext.Image or CoreWebView2WebResourceContext.Media
                             or CoreWebView2WebResourceContext.Font;
                if (!sameSite || heavy) e.Response = env.CreateWebResourceResponse(null, 204, "No Content", "");
            };
        }

        if (Site == "lcpdfr")
        {
            // LCPDFR checks new browsers with a proof-of-work page that clears and reloads by itself
            await NavigateAsync(Home);
            // the check usually clears in 5-10 s; slow PCs and busy times need longer
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (await OnChallengePageAsync())
            {
                if (DateTime.UtcNow > deadline) throw new SiteChallengeException(Home);
                await Task.Delay(500);
            }
        }

        // park on a tiny same-origin page: fetch() and DOMParser work there, and no site scripts keep running
        await NavigateAsync(Origin + "/robots.txt");
        if (await OnChallengePageAsync()) throw new SiteChallengeException(Home);
    }

    string Origin => Site == "lcpdfr" ? "https://www.lcpdfr.com" : "https://www.gta5-mods.com";
    string Domain => Site == "lcpdfr" ? "lcpdfr.com" : "gta5-mods.com";

    async Task<bool> OnChallengePageAsync()
    {
        if (Web.IsChallengeTitle(web!.DocumentTitle)) return true;
        var r = await web.ExecuteScriptAsync("!!document.querySelector('.h-captcha,.g-recaptcha,.powstatus,iframe[src*=\"challenges.cloudflare.com\"]')");
        return r == "true";
    }

    Task NavigateAsync(string url)
    {
        var tcs = new TaskCompletionSource();
        void Done(object? s, CoreWebView2NavigationCompletedEventArgs e)
        {
            web!.NavigationCompleted -= Done;
            tcs.TrySetResult();
        }
        web!.NavigationCompleted += Done;
        web.Navigate(url);
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }

    async Task<JsonElement> InvokeAsync(string fn, object? arg)
    {
        // the page may have reloaded since the last call: (re)inject the extractor when it's missing
        if (await web!.ExecuteScriptAsync("typeof window.__vml") != "\"object\"")
            await web.ExecuteScriptAsync(script);

        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = tcs;
        var argJson = arg == null ? "" : JsonSerializer.Serialize(arg);
        var js = $$"""
            (async () => {
              try {
                const r = await window.__vml.{{fn}}({{argJson}});
                chrome.webview.postMessage(JSON.stringify({ id: "{{id}}", ok: true, result: r === undefined ? null : r }));
              } catch (e) {
                chrome.webview.postMessage(JSON.stringify({ id: "{{id}}", ok: false, error: String((e && e.message) || e) }));
              }
            })();
            """;
        await web.ExecuteScriptAsync(js);
        try
        {
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(90));
        }
        finally
        {
            pending.Remove(id);
        }
    }

    void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string text;
        try { text = e.TryGetWebMessageAsString(); } catch { return; }
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (!root.TryGetProperty("id", out var idEl) || !pending.TryGetValue(idEl.GetString() ?? "", out var tcs)) return;
        if (root.GetProperty("ok").GetBoolean())
        {
            tcs.TrySetResult(root.GetProperty("result").Clone());
        }
        else
        {
            var err = root.GetProperty("error").GetString() ?? "Unknown error";
            tcs.TrySetException(err.StartsWith("CHALLENGE:")
                ? new SiteChallengeException(err.Substring(10).Trim() is { Length: > 0 } u ? u : Home)
                : new InvalidOperationException(err));
        }
    }

    public void Dispose()
    {
        controller?.Close();
        controller = null;
    }
}
