using CodeWalker.GameFiles;

namespace VModLauncher.DevKit.Resources;

/// <summary>
/// Converts Legacy texture dictionaries (.ytd, resource version 13) to the gen9 format GTA V Enhanced reads
/// (version 5). CodeWalker's own converter leaves the header at version 13, so the dictionary is converted
/// and rebuilt here with the gen9 version.
/// </summary>
public static class Gen9
{
    static readonly object Lock = new();

    public sealed record Converted(string From, string To, int OldVersion, int NewVersion, int Textures);

    /// <summary>Converts one .ytd, or every .ytd in a folder (other files are copied as they are).</summary>
    public static List<Converted> ConvertYtd(string input, string output)
    {
        var results = new List<Converted>();
        if (Directory.Exists(input))
        {
            Directory.CreateDirectory(output);
            foreach (var f in Directory.GetFiles(input).Order(StringComparer.OrdinalIgnoreCase))
            {
                var to = Path.Combine(output, Path.GetFileName(f));
                if (f.EndsWith(".ytd", StringComparison.OrdinalIgnoreCase)) results.Add(ConvertOne(f, to));
                else File.Copy(f, to, true);
            }
            return results;
        }
        if (Directory.Exists(output) || output.EndsWith('\\') || output.EndsWith('/'))
            output = Path.Combine(output, Path.GetFileName(input));
        results.Add(ConvertOne(input, output));
        return results;
    }

    static Converted ConvertOne(string from, string to)
    {
        var data = File.ReadAllBytes(from);
        var info = ResourceInfo.FromHeader(from, data, data.Length);
        if (!info.IsResource) throw new InvalidDataException($"{from} isn't a resource (no RSC7 header)");
        if (info.Version == 5)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(to))!);
            File.WriteAllBytes(to, data);
            return new Converted(from, to, 5, 5, -1);
        }
        if (info.Version != 13) throw new InvalidDataException($"{from} is resource version {info.Version}; only Legacy texture dictionaries (13) convert");
        byte[] built;
        int textures;
        lock (Lock)
        {
            var was = RpfManager.IsGen9;
            try
            {
                RpfManager.IsGen9 = false; // read it as Legacy
                var ytd = new YtdFile();
                ytd.Load(data);
                if (ytd.TextureDict == null) throw new InvalidDataException($"{from}: no texture dictionary in it");
                textures = ytd.TextureDict.Textures?.data_items?.Length ?? 0;
                ytd.TextureDict.EnsureGen9();
                built = ResourceBuilder.Build(ytd.TextureDict, ytd.GetVersion(true), true, true);
            }
            finally { RpfManager.IsGen9 = was; }
        }
        var outInfo = ResourceInfo.FromHeader(to, built, built.Length);
        if (outInfo.Version != 5) throw new InvalidDataException($"{from}: the converted file came out as version {outInfo.Version}, not 5");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(to))!);
        File.WriteAllBytes(to, built);
        return new Converted(from, to, info.Version, outInfo.Version, textures);
    }
}
