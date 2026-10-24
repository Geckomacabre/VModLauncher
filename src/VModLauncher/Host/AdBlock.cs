using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using VModLauncher.Core.Features;

namespace VModLauncher.Host;

/// <summary>
/// Puts <see cref="AdRules"/> on a site window: blocked requests get an empty answer, ad clicks go nowhere,
/// links to other sites open in the player's own browser, and the empty ad slots are hidden.
/// </summary>
public static class AdBlock
{
    public static void Attach(CoreWebView2 core)
    {
        var env = core.Environment;
        // requests from frames and the sites' service workers too, not just the page
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += (_, e) =>
        {
            bool passive = e.ResourceContext is CoreWebView2WebResourceContext.Image or CoreWebView2WebResourceContext.Stylesheet
                or CoreWebView2WebResourceContext.Font or CoreWebView2WebResourceContext.Media;
            if (AdRules.Blocks(e.Request.Uri, passive))
                e.Response = env.CreateWebResourceResponse(null, 204, "No Content", "");
        };

        core.NavigationStarting += (_, e) =>
        {
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return;
            if (AdRules.IsAdHost(u.Host)) { e.Cancel = true; return; }
            if (AdRules.IsAllowed(u)) return;
            e.Cancel = true;
            // only a click: an ad script redirecting the page mustn't open browser tabs
            if (e.IsUserInitiated) Web.OpenExternal(e.Uri);
        };

        _ = core.AddScriptToExecuteOnDocumentCreatedAsync(
            "(() => { if (window.top !== window) return;"
            + " const add = () => { const s = document.createElement('style'); s.textContent = " + JsonSerializer.Serialize(AdRules.HideCss) + ";"
            + " (document.head || document.documentElement).appendChild(s); };"
            + " if (document.documentElement) add(); else document.addEventListener('readystatechange', add, { once: true }); })();");
    }
}
