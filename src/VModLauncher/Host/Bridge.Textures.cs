using System.Text.Json;
using VModLauncher.Core.Tools;

namespace VModLauncher.Host;

/// <summary>Texture optimizer (tex.*) — see docs/bridge.md, "Wave 3".</summary>
public sealed partial class Bridge
{
    private partial async Task<(bool Handled, object? Result)> HandleTexturesAsync(string cmd, JsonElement args)
    {
        switch (cmd)
        {
            case "tex.scan":
            {
                var g = Game;
                var maxSize = Int(args, "maxSize", 2048);
                try
                {
                    var result = await Task.Run(() => TextureOptimizer.Scan(g, App.Paths, library, maxSize, (text, fraction) =>
                        Emit("install.progress", new { planId = "textures", stage = "Reading your add-on packs", fraction, detail = text })));
                    return (true, result);
                }
                catch (ArgumentException ex) { throw new BridgeException("ERROR", ex.Message); }
            }

            case "tex.optimize":
            {
                var g = Game;
                var maxSize = Int(args, "maxSize", 2048);
                var packs = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("packs", out var p) && p.ValueKind == JsonValueKind.Array
                    ? p.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                    : new List<string>();
                if (packs.Count == 0) throw new BridgeException("ERROR", "Pick at least one pack.");
                TexOptimizeResult result;
                try
                {
                    result = await Busy(() => Task.Run(() => TextureOptimizer.Optimize(g, App.Paths, library, packs, maxSize, (text, fraction) =>
                        Emit("install.progress", new { planId = "textures", stage = text, fraction, detail = (string?)null }))));
                }
                catch (ArgumentException ex) { throw new BridgeException("ERROR", ex.Message); }
                foreach (var problem in result.Problems) App.Log("textures", problem);
                Emit("library.changed", new { gameId = g.Id });
                Emit("game.changed", Status(g));
                return (true, result);
            }
        }
        return (false, null);
    }
}
