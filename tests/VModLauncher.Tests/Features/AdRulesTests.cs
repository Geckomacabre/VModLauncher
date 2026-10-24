using VModLauncher.Core.Features;
using Xunit;

namespace VModLauncher.Tests.Features;

public class AdRulesTests
{
    [Theory]
    // the mod sites and their files
    [InlineData("https://www.gta5-mods.com/scripts/no-permanent-parachute/download/203265", false, false)]
    [InlineData("https://files.gta5-mods.com/uploads/no-permanent-parachute/6b0102-NoParachute.rar", false, false)]
    [InlineData("https://objects.githubusercontent.com/release-asset", false, false)]
    // LCPDFR's own tag script: its pages freeze without it
    [InlineData("https://ssgtm.lcpdfr.com/sstag.js?id=GTM-56VBM4D", false, false)]
    // ad networks, whatever the request
    [InlineData("https://cdn.intergient.com/pageos/V.20261020.2/iframe/iframe.html", false, true)]
    [InlineData("https://securepubads.g.doubleclick.net/pixel.gif", true, true)]
    // ad-blocker recovery loaders on throwaway domains: code from a site that isn't allowed
    [InlineData("https://upwardlywell.com/loader.js", false, true)]
    [InlineData("https://html-load.cc/script.js", false, true)]
    // pictures from anywhere still show in mod descriptions
    [InlineData("https://i.imgur.com/abc.png", true, false)]
    // reCAPTCHA's path only, not the rest of google.com
    [InlineData("https://www.google.com/recaptcha/api.js", false, false)]
    [InlineData("https://www.google.com/pagead/conversion.js", false, true)]
    // a look-alike host isn't the site
    [InlineData("https://evilgta5-mods.com/payload.js", false, true)]
    [InlineData("data:image/png;base64,AAAA", false, false)]
    public void Blocks_ads_and_foreign_code_but_not_the_sites(string url, bool passive, bool blocked)
    {
        Assert.Equal(blocked, AdRules.Blocks(url, passive));
    }
}
