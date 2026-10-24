using System.Diagnostics;

namespace VModLauncher.Core.Game;

public enum PrereqState { Ok, Missing, Wrong }

public sealed record PrereqStatus(string Id, string Name, PrereqState State, string Detail, string? GetUrl);

/// <summary>Checks the frameworks most mods need, per edition.</summary>
public static class Prerequisites
{
    public const string ScriptHookV = "shv";
    public const string AsiLoader = "asiloader";
    public const string Shvdn = "shvdn";
    public const string ModsFolder = "modsfolder";
    public const string RagePluginHook = "rph";
    public const string Lspdfr = "lspdfr";
    public const string Els = "els";
    public const string Menyoo = "menyoo";

    public static List<PrereqStatus> Check(GameInstall g)
    {
        var list = new List<PrereqStatus>();
        bool Has(string rel) => File.Exists(Path.Combine(g.Root, rel));

        list.Add(Has("ScriptHookV.dll")
            ? new(ScriptHookV, "Script Hook V", PrereqState.Ok, "ScriptHookV.dll found", null)
            : new(ScriptHookV, "Script Hook V", PrereqState.Missing, "Needed by .asi and .NET script mods", "http://www.dev-c.com/gtav/scripthookv/"));

        list.Add(Has(g.AsiLoaderName)
            ? new(AsiLoader, "ASI loader", PrereqState.Ok, g.AsiLoaderName + " found", null)
            : new(AsiLoader, "ASI loader", PrereqState.Missing, $"{g.AsiLoaderName} (ships with Script Hook V) loads .asi plugins", "http://www.dev-c.com/gtav/scripthookv/"));

        list.Add(CheckShvdn(g));

        bool openIv = Has("OpenIV.asi"), openRpf = Has("OpenRPF.asi");
        if (g.Edition == GameEdition.Enhanced)
        {
            list.Add(openRpf
                ? new(ModsFolder, "mods folder (OpenRPF)", PrereqState.Ok, "OpenRPF.asi found", null)
                : new(ModsFolder, "mods folder (OpenRPF)", openIv ? PrereqState.Wrong : PrereqState.Missing,
                    openIv ? "OpenIV.asi doesn't work on Enhanced; use OpenRPF.asi" : "Needed for add-on DLC packs and replace mods",
                    "https://www.gta5-mods.com/tools/openrpf-openiv-asi-for-gta-v-enhanced"));
        }
        else
        {
            list.Add(openIv || openRpf
                ? new(ModsFolder, "mods folder (OpenIV.asi)", PrereqState.Ok, (openIv ? "OpenIV.asi" : "OpenRPF.asi") + " found", null)
                : new(ModsFolder, "mods folder (OpenIV.asi)", PrereqState.Missing, "Needed for add-on DLC packs and replace mods", "https://openiv.com/"));
        }

        list.Add(Has("RAGEPluginHook.exe")
            ? new(RagePluginHook, "RAGE Plugin Hook", PrereqState.Ok, "RAGEPluginHook.exe found", null)
            : new(RagePluginHook, "RAGE Plugin Hook", PrereqState.Missing, "Ships with LSPDFR; runs RPH plugins", "https://www.lcpdfr.com/downloads/gta5mods/g17media/7792-lspd-first-response/"));

        list.Add(Has(Path.Combine("plugins", "LSPD First Response.dll"))
            ? new(Lspdfr, "LSPDFR", PrereqState.Ok, "plugins\\LSPD First Response.dll found", null)
            : new(Lspdfr, "LSPDFR", PrereqState.Missing, "Needed by LSPDFR callouts and plugins", "https://www.lcpdfr.com/downloads/gta5mods/g17media/7792-lspd-first-response/"));

        list.Add(Has("ELS.asi")
            ? new(Els, "ELS", PrereqState.Ok, "ELS.asi found", null)
            : new(Els, "ELS", PrereqState.Missing, "Only needed by ELS emergency vehicles", "https://www.lcpdfr.com/downloads/gta5mods/scripts/13865-emergency-lighting-system/"));

        list.Add(Has("Menyoo.asi")
            ? new(Menyoo, "Menyoo", PrereqState.Ok, "Menyoo.asi found", null)
            : new(Menyoo, "Menyoo", PrereqState.Missing, "Only needed by Menyoo maps and outfits", "https://www.gta5-mods.com/scripts/menyoo-pc-sp"));

        return list;
    }

    static PrereqStatus CheckShvdn(GameInstall g)
    {
        var asi = Path.Combine(g.Root, "ScriptHookVDotNet.asi");
        var name = g.Edition == GameEdition.Enhanced ? "Script Hook V .NET Enhanced" : "Script Hook V .NET";
        var url = g.Edition == GameEdition.Enhanced
            ? "https://github.com/Chiheb-Bacha/ScriptHookVDotNetEnhanced/releases"
            : "https://github.com/scripthookvdotnet/scripthookvdotnet-nightly/releases";
        if (!File.Exists(asi))
            return new(Shvdn, name, PrereqState.Missing, "Needed by .NET scripts in the scripts folder", url);

        // SHVDNE's asi carries ProductName "Script Hook V .Net Enhanced"; the normal SHVDN crashes Enhanced.
        bool isEnhancedBuild = false;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(asi);
            isEnhancedBuild = (info.ProductName ?? "").Contains("Enhanced", StringComparison.OrdinalIgnoreCase);
        }
        catch { }

        if (g.Edition == GameEdition.Enhanced && !isEnhancedBuild)
            return new(Shvdn, name, PrereqState.Wrong, "The Legacy SHVDN is installed; Enhanced needs SHVDNE", url);
        if (g.Edition == GameEdition.Legacy && isEnhancedBuild)
            return new(Shvdn, name, PrereqState.Wrong, "SHVDNE (the Enhanced build) is installed on Legacy", url);
        return new(Shvdn, name, PrereqState.Ok, "ScriptHookVDotNet.asi found", null);
    }
}
