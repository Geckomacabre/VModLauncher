using System.Text.RegularExpressions;

namespace VModLauncher.Core.Features;

/// <summary>
/// What the site window blocks, so fake "Download" ads never show next to the real button. Code, frames and
/// data requests only load from the mod sites, their file hosts and what sign-in and the sites' browser checks
/// need; pictures, styles, fonts and media load from anywhere (mod descriptions show images from all over)
/// except ad and tracking hosts, which are refused whatever the request is.
/// </summary>
public static class AdRules
{
    /// <summary>Sites whose code may run in the window.</summary>
    static readonly Regex Allowed = new(
        @"(^|\.)("
        // the mod sites and where their files come from
        + @"gta5-mods\.com|lcpdfr\.com|lcpdfrusercontent\.com|github\.com|githubusercontent\.com|dev-c\.com"
        // sign-in and browser checks
        + @"|challenges\.cloudflare\.com|hcaptcha\.com|recaptcha\.net|discord\.com|discordapp\.com|steamcommunity\.com|steampowered\.com|accounts\.google\.com"
        // page furniture
        + @"|fonts\.googleapis\.com|fonts\.gstatic\.com|cdnjs\.cloudflare\.com|cdn\.jsdelivr\.net|ajax\.googleapis\.com|code\.jquery\.com|unpkg\.com|fontawesome\.com"
        // trailers embedded in mod pages
        + @"|youtube\.com|youtube-nocookie\.com|ytimg\.com|googlevideo\.com"
        + @")$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Ad networks, ad exchanges, trackers and consent pop-ups for them.</summary>
    static readonly Regex AdHost = new(
        @"(^|\.)("
        + @"doubleclick\.net|googlesyndication\.com|googleadservices\.com|google-analytics\.com|googletagmanager\.com|googletagservices\.com"
        // (not LCPDFR's own ssgtm.lcpdfr.com tag script: its pages wait on it and freeze without it)
        + @"|adservice\.google\.[a-z.]+|imasdk\.googleapis\.com"
        + @"|intergient\.com|playwire\.com|openx\.net|rubiconproject\.com|criteo\.com|criteo\.net|sonobi\.com|3lift\.com|adsafeprotected\.com"
        + @"|amazon-adsystem\.com|pubmatic\.com|adnxs\.com|casalemedia\.com|indexexchange\.com|taboola\.com|outbrain\.com|quantserve\.com"
        + @"|quantcount\.com|scorecardresearch\.com|moatads\.com|adsrvr\.org|teads\.tv|sharethrough\.com|smartadserver\.com|yieldmo\.com"
        + @"|gumgum\.com|media\.net|lijit\.com|sovrn\.com|33across\.com|bidswitch\.net|onetag-sys\.com|seedtag\.com|adform\.net|smaato\.net"
        + @"|connatix\.com|primis\.tech|aniview\.com|vidazoo\.com|liadm\.com|id5-sync\.com|crwdcntrl\.net|bluekai\.com|everesttech\.net"
        + @"|demdex\.net|rlcdn\.com|agkn\.com|zemanta\.com|mgid\.com|revcontent\.com|propellerads\.com|popads\.net|popcash\.net|adsterra\.com"
        + @"|hilltopads\.net|exoclick\.com|juicyads\.com|clickadu\.com|monetag\.com|a-ads\.com|coinzilla\.com|bounceexchange\.com|hotjar\.com"
        + @"|clarity\.ms|facebook\.net|ads-twitter\.com|analytics\.twitter\.com|nr-data\.net|onesignal\.com|pubcid\.org|sharedid\.org"
        + @"|lngtd\.com|adlightning\.com|confiant-integrations\.net|kargo\.com|undertone\.com|triplelift\.com|adkernel\.com|e-planning\.net"
        + @"|ads\.yahoo\.com|analytics\.yahoo\.com|adtelligent\.com|minutemedia-prebid\.com|unrulymedia\.com|richaudience\.com|nextmillmedia\.com|mediago\.io"
        + @"|html-load\.cc|cookielaw\.org|privacymanager\.io|quantcast\.com|cmp\.inmobi\.com|consensu\.org|sentry-cdn\.com"
        + @")$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Empty ad slots and the space the sites keep for them.</summary>
    public const string HideCss = """
        ins.adsbygoogle, [id^="div-gpt-ad"], [id^="google_ads_iframe"], [id^="pw-oop"], [id^="pw-video"], .pw-tag,
        [data-pw-desk], [data-pw-mobi], .ipsAdvertisement, .ipsAd, [data-role="advertisement"],
        .ad-container, .ad-slot, .ad-wrapper, .adsbox, .advert, .advertisement, .sponsored, .sponsored-link,
        [id*="taboola"], [class*="taboola"], [id*="outbrain"], [class*="outbrain"],
        #file-download div:has(> .ad-container),
        [class*="playwire"], [class^="clsReduction"], .uBlockBrokeOurSiteIpsPad:has(> [class^="clsReduction"])
        { display: none !important; }
        """;

    /// <summary>
    /// True when a request shouldn't load. <paramref name="passive"/>: a picture, style, font or media file
    /// (anything else is code, a frame or a data request).
    /// </summary>
    public static bool Blocks(string url, bool passive)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return false; // data:, blob:
        if (IsAdHost(u.Host)) return true;
        return !passive && !IsAllowed(u);
    }

    public static bool IsAdHost(string host) => AdHost.IsMatch(host);

    /// <summary>A mod site, a file host, or something sign-in, the browser checks or the pages need.</summary>
    public static bool IsAllowed(Uri u) =>
        Allowed.IsMatch(u.Host)
        // Google's reCAPTCHA lives on hosts that also serve ads, so only its own path
        || ((u.Host.Equals("www.google.com", StringComparison.OrdinalIgnoreCase) || u.Host.Equals("www.gstatic.com", StringComparison.OrdinalIgnoreCase))
            && u.AbsolutePath.StartsWith("/recaptcha/", StringComparison.OrdinalIgnoreCase));
}
