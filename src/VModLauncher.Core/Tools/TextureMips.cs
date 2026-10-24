using CodeWalker.GameFiles;

namespace VModLauncher.Core.Tools;

/// <summary>
/// Mip-level arithmetic for GTA V (legacy, version 13) textures. A texture's data is its mip levels
/// one after another, biggest first, each sized like a D3D11 surface (block-compressed levels never
/// smaller than one 4x4 block). Dropping the top k levels is therefore a slice of the data plus new
/// header values: no pixel is decoded or re-encoded.
/// </summary>
public static class TextureMips
{
    /// <summary>Bytes per 4x4 block (compressed) or per pixel (uncompressed); null for formats we don't touch.</summary>
    public static (bool Compressed, int Bytes)? Layout(TextureFormat f) => f switch
    {
        TextureFormat.D3DFMT_DXT1 or TextureFormat.D3DFMT_ATI1 => (true, 8),
        TextureFormat.D3DFMT_DXT3 or TextureFormat.D3DFMT_DXT5 or TextureFormat.D3DFMT_ATI2 or TextureFormat.D3DFMT_BC7 => (true, 16),
        TextureFormat.D3DFMT_A8R8G8B8 or TextureFormat.D3DFMT_X8R8G8B8 or TextureFormat.D3DFMT_A8B8G8R8 => (false, 4),
        TextureFormat.D3DFMT_A1R5G5B5 => (false, 2),
        TextureFormat.D3DFMT_A8 or TextureFormat.D3DFMT_L8 => (false, 1),
        _ => null,
    };

    /// <summary>Size in bytes of one level that is <paramref name="w"/> x <paramref name="h"/>.</summary>
    public static long LevelSize(TextureFormat f, int w, int h)
    {
        var l = Layout(f) ?? throw new NotSupportedException("Texture format " + f);
        w = Math.Max(1, w);
        h = Math.Max(1, h);
        return l.Compressed
            ? (long)Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * l.Bytes
            : (long)w * h * l.Bytes;
    }

    /// <summary>
    /// The "Stride" CodeWalker and the game store: level-0 bytes divided by the height
    /// (w/2 for DXT1, w for DXT5/BC7, w*4 for A8R8G8B8).
    /// </summary>
    public static int Stride(TextureFormat f, int w, int h) => (int)(LevelSize(f, w, h) / Math.Max(1, h));

    /// <summary>Byte offset of level <paramref name="level"/> inside the texture's data.</summary>
    public static long LevelOffset(TextureFormat f, int w, int h, int level)
    {
        long offset = 0;
        for (int i = 0; i < level; i++) offset += LevelSize(f, w >> i, h >> i);
        return offset;
    }

    static bool IsPow2(int v) => v > 0 && (v & (v - 1)) == 0;

    /// <summary>
    /// How many top levels to drop so the texture is at most <paramref name="maxSize"/> on its long side,
    /// or why it can't be done. 0 means it's already small enough.
    /// </summary>
    public static (int Drop, string? Reason) Plan(Texture t, int maxSize)
    {
        int w = t.Width, h = t.Height;
        if (Math.Max(w, h) <= maxSize) return (0, null);
        if (Layout(t.Format) == null) return (0, $"format {t.Format} isn't handled");
        if (t.Depth > 1) return (0, "volume texture");
        if (!IsPow2(w) || !IsPow2(h)) return (0, "size isn't a power of two");
        // the level offsets below assume the standard packed layout the game's own files use
        if (t.Stride != Stride(t.Format, w, h)) return (0, "unusual row layout");
        int k = 0;
        while (Math.Max(w >> k, h >> k) > maxSize) k++;
        if (t.Levels <= k) return (0, $"only {t.Levels} mip level{(t.Levels == 1 ? "" : "s")}, nothing smaller to keep");
        // block-compressed levels below 4 px are padded: keep the new top level at least one full block
        if (Layout(t.Format)!.Value.Compressed && Math.Min(w >> k, h >> k) < 4) return (0, "would get smaller than one 4x4 block");
        var data = t.Data?.FullData;
        if (data == null) return (0, "no pixel data");
        if (LevelOffset(t.Format, w, h, k) >= data.Length) return (0, "data is shorter than its mip levels");
        return (k, null);
    }

    /// <summary>Bytes the top <paramref name="drop"/> levels take.</summary>
    public static long SavedBytes(Texture t, int drop) =>
        drop <= 0 ? 0 : Math.Min(t.Data?.FullData?.LongLength ?? 0, LevelOffset(t.Format, t.Width, t.Height, drop));

    /// <summary>Drops the top <paramref name="drop"/> levels in place. Returns the bytes removed.</summary>
    public static long Strip(Texture t, int drop)
    {
        if (drop <= 0) return 0;
        var data = t.Data.FullData;
        var offset = LevelOffset(t.Format, t.Width, t.Height, drop);
        if (offset <= 0 || offset >= data.Length) throw new InvalidOperationException($"Texture {t.Name}: can't drop {drop} level(s)");
        var rest = new byte[data.Length - offset];
        Buffer.BlockCopy(data, (int)offset, rest, 0, rest.Length);
        t.Data.FullData = rest;
        t.Width = (ushort)(t.Width >> drop);
        t.Height = (ushort)(t.Height >> drop);
        t.Levels = (byte)(t.Levels - drop);
        t.Stride = (ushort)Stride(t.Format, t.Width, t.Height);
        return offset;
    }
}
