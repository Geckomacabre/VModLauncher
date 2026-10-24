using System.Text.Json;

namespace VModLauncher.Host;

/// <summary>Add-on pack builder (packbuild.*) — see docs/bridge.md, "Wave 3".</summary>
public sealed partial class Bridge
{
    private partial Task<(bool Handled, object? Result)> HandlePackBuilderAsync(string cmd, JsonElement args)
    {
        if (cmd != "packbuild.fromFolder") return Task.FromResult<(bool, object?)>((false, null));

        // the analyser builds the dlc.rpf from the loose files; the folder goes through the usual plan.ready flow
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Pick a folder of loose add-on car or ped files" };
        if (dlg.ShowDialog(window) != true) return Task.FromResult<(bool, object?)>((true, false));
        var folder = dlg.FolderName;
        var name = StrOrNull(args, "name");
        _ = string.IsNullOrWhiteSpace(name)
            ? PrepareLocalAsync(folder)
            : PrepareAsync(folder, new DownloadRequest { Key = null, Title = name.Trim(), Site = "local" });
        return Task.FromResult<(bool, object?)>((true, true));
    }
}
