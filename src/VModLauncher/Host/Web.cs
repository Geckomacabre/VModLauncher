using System.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace VModLauncher.Host;

/// <summary>One shared WebView2 profile, so a login or a passed browser check counts everywhere in the launcher.</summary>
public static class Web
{
    static Task<CoreWebView2Environment>? env;

    public static Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        // VML_DEVPORT opens Chrome DevTools Protocol for automated UI tests; never set for players
        var devPort = Environment.GetEnvironmentVariable("VML_DEVPORT");
        var args = "--disable-features=msSmartScreenProtection";
        if (int.TryParse(devPort, out var port)) args += $" --remote-debugging-port={port}";
        return env ??= CoreWebView2Environment.CreateAsync(null, App.Paths.WebProfile, new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = args,
        });
    }

    public static void OpenExternal(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http")) return;
        Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
    }

    public static bool IsChallengeTitle(string? title) =>
        title != null && (title.Contains("Checking your browser", StringComparison.OrdinalIgnoreCase)
                          || title.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
                          || title.Contains("Attention Required", StringComparison.OrdinalIgnoreCase));
}
