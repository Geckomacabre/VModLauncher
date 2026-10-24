namespace VModLauncher.DevKit.Resources;

/// <summary>
/// The RSC7 header every GTA V resource (.ytd, .ydr, .ydd, .yft, .ybn, .ymap, ...) starts with: magic, resource
/// version, and the system / graphics page flags. The version tells Legacy and Enhanced (gen9) files apart: a
/// texture dictionary is version 13 on Legacy and 5 on Enhanced, and the wrong one crashes the game.
/// </summary>
public sealed record ResourceInfo(string Path, bool IsResource, int Version, uint SystemFlags, uint GraphicsFlags, long Size)
{
    const uint Magic = 0x37435352; // "RSC7"

    /// <summary>Known texture dictionary versions per edition.</summary>
    public static readonly IReadOnlyDictionary<int, string> YtdVersions = new Dictionary<int, string> { [13] = "Legacy", [5] = "Enhanced (gen9)" };

    public static ResourceInfo Read(string path)
    {
        var size = new FileInfo(path).Length;
        Span<byte> head = stackalloc byte[16];
        using (var s = File.OpenRead(path))
        {
            if (s.Read(head) < 16) return new ResourceInfo(path, false, 0, 0, 0, size);
        }
        return FromHeader(path, head, size);
    }

    public static ResourceInfo FromHeader(string name, ReadOnlySpan<byte> head, long size)
    {
        if (head.Length < 16 || BitConverter.ToUInt32(head) != Magic) return new ResourceInfo(name, false, 0, 0, 0, size);
        return new ResourceInfo(name, true, BitConverter.ToInt32(head[4..]), BitConverter.ToUInt32(head[8..]), BitConverter.ToUInt32(head[12..]), size);
    }

    /// <summary>"Legacy" / "Enhanced (gen9)" for a texture dictionary's version, null when it's not one we know.</summary>
    public string? YtdEdition => IsResource && Path.EndsWith(".ytd", StringComparison.OrdinalIgnoreCase) && YtdVersions.TryGetValue(Version, out var e) ? e : null;

    public override string ToString() => IsResource
        ? $"{System.IO.Path.GetFileName(Path)}: RSC7 version {Version}{(YtdEdition != null ? " (" + YtdEdition + ")" : "")}, system 0x{SystemFlags:X8}, graphics 0x{GraphicsFlags:X8}, {Size:N0} bytes"
        : $"{System.IO.Path.GetFileName(Path)}: not a resource (no RSC7 header), {Size:N0} bytes";
}
