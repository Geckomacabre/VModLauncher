using CodeWalker.GameFiles;
using VModLauncher.Core;
using VModLauncher.Core.Install;
using VModLauncher.Core.Library;
using VModLauncher.Core.Tools;
using VModLauncher.Tests.Features;
using Xunit;

namespace VModLauncher.Tests.Tools;

public class TextureTests
{
    const TextureFormat DXT1 = TextureFormat.D3DFMT_DXT1, DXT5 = TextureFormat.D3DFMT_DXT5, BC7 = TextureFormat.D3DFMT_BC7, ARGB = TextureFormat.D3DFMT_A8R8G8B8;

    /// <summary>
    /// A texture laid out the way CodeWalker reads a legacy ytd (level i is Stride*Height/4^i bytes).
    /// Every byte of level i is i + 1, so the surviving top level is easy to tell.
    /// </summary>
    static Texture Make(string name, TextureFormat f, int w, int h, int levels = -1)
    {
        if (levels < 0) levels = (int)Math.Log2(Math.Max(w, h)) + 1;
        var stride = TextureMips.Stride(f, w, h);
        var data = new List<byte>();
        for (int i = 0; i < levels; i++)
            data.AddRange(Enumerable.Repeat((byte)(i + 1), (int)(((long)stride * h) >> (2 * i))));
        return new Texture
        {
            Name = name, NameHash = JenkHash.GenHash(name.ToLowerInvariant()),
            Width = (ushort)w, Height = (ushort)h, Depth = 1, Stride = (ushort)stride, Format = f, Levels = (byte)levels,
            Data = new TextureData { FullData = data.ToArray() },
        };
    }

    static byte[] Ytd(params Texture[] textures)
    {
        var dict = new TextureDictionary();
        dict.BuildFromTextureList(textures.ToList());
        return ResourceBuilder.Build(dict, 13, true, false);
    }

    // ---------------------------------------------------------------- mip math

    [Theory]
    [InlineData(DXT1, 256, 256, 32768, 128)]
    [InlineData(DXT5, 256, 256, 65536, 256)]
    [InlineData(BC7, 256, 128, 32768, 256)]
    [InlineData(ARGB, 256, 256, 262144, 1024)]
    public void Level_size_and_stride_match_the_game(TextureFormat f, int w, int h, long size, int stride)
    {
        Assert.Equal(size, TextureMips.LevelSize(f, w, h));
        Assert.Equal(stride, TextureMips.Stride(f, w, h));
        // the next level is a quarter of it
        Assert.Equal(size + size / 4, TextureMips.LevelOffset(f, w, h, 2));
    }

    [Fact]
    public void Compressed_levels_are_never_smaller_than_one_block()
    {
        Assert.Equal(8, TextureMips.LevelSize(DXT1, 2, 2));
        Assert.Equal(16, TextureMips.LevelSize(DXT5, 1, 1));
        Assert.Equal(32, TextureMips.LevelSize(BC7, 8, 2));
        Assert.Equal(4, TextureMips.LevelSize(ARGB, 1, 1));
    }

    [Theory]
    [InlineData(DXT1)]
    [InlineData(DXT5)]
    [InlineData(BC7)]
    [InlineData(ARGB)]
    public void Strip_keeps_the_mip_that_fits(TextureFormat f)
    {
        var t = Make("t", f, 2048, 1024);
        var original = t.Data.FullData;
        var levels = t.Levels;

        var (drop, reason) = TextureMips.Plan(t, 512);
        Assert.Null(reason);
        Assert.Equal(2, drop);
        var expected = TextureMips.SavedBytes(t, drop);
        Assert.Equal(TextureMips.LevelOffset(f, 2048, 1024, 2), expected);

        Assert.Equal(expected, TextureMips.Strip(t, drop));
        Assert.Equal(512, t.Width);
        Assert.Equal(256, t.Height);
        Assert.Equal(levels - 2, t.Levels);
        Assert.Equal(TextureMips.Stride(f, 512, 256), t.Stride);
        // exactly the old level 2 onwards, untouched
        Assert.Equal(original.AsSpan((int)expected).ToArray(), t.Data.FullData);
        Assert.Equal(3, t.Data.FullData[0]);
        Assert.Equal((0, (string?)null), TextureMips.Plan(t, 512));
    }

    [Fact]
    public void Plan_leaves_what_it_cant_shrink_and_says_why()
    {
        Assert.Equal((0, (string?)null), TextureMips.Plan(Make("small", DXT5, 512, 512), 1024));
        Assert.Contains("power of two", TextureMips.Plan(Make("odd", DXT5, 3000, 3000), 1024).Reason);
        Assert.Contains("mip level", TextureMips.Plan(Make("flat", DXT5, 2048, 2048, levels: 1), 1024).Reason);
        Assert.Contains("4x4 block", TextureMips.Plan(Make("strip", DXT1, 8192, 8), 1024).Reason);

        var padded = Make("padded", DXT5, 2048, 2048);
        padded.Stride += 16;
        Assert.Contains("row layout", TextureMips.Plan(padded, 1024).Reason);

        var unknown = Make("unknown", DXT5, 2048, 2048);
        unknown.Format = (TextureFormat)999;
        Assert.Contains("isn't handled", TextureMips.Plan(unknown, 1024).Reason);
    }

    // ---------------------------------------------------------------- whole packs

    /// <summary>
    /// One add-on pack "texpack" the game loads, with a nested archive holding big textures and a
    /// loose ytd that's already small. Returns the pack's game-relative path.
    /// </summary>
    static string MakeGame(Sandbox s, bool onigiri)
    {
        Sandbox.MakeUpdateRpf(Path.Combine(s.GameRoot, "mods", "update"), "update.rpf", FeatureFixtures.DlcListText("dlcpacks:/mpbeach/", "dlcpacks:/texpack/"));
        if (onigiri)
        {
            File.WriteAllText(Path.Combine(s.GameRoot, "onigiri.asi"), "x");
            FeatureFixtures.Write(s.GameRoot, "onigiri/common/data/dlclist.xml", FeatureFixtures.DlcListText("dlcpacks:/mpbeach/", "dlcpacks:/texpack/"));
        }
        // Rockstar's own pack (with a mods copy) is never offered
        Directory.CreateDirectory(Path.Combine(s.GameRoot, "update", "x64", "dlcpacks", "mpbeach"));
        Sandbox.MakeDlcRpf(Path.Combine(s.GameRoot, "mods", "update", "x64", "dlcpacks", "mpbeach", "dlc.rpf"), "mpbeach");

        var rel = onigiri ? @"onigiri\dlcpacks\texpack\dlc.rpf" : @"mods\update\x64\dlcpacks\texpack\dlc.rpf";
        var path = Path.Combine(s.GameRoot, rel);
        Sandbox.MakeDlcRpf(path, "texpack");
        var rpf = new RpfFile(path, Path.GetFileName(path));
        rpf.ScanStructure(null, null);
        RpfFile.CreateFile(rpf.Root, "hud.ytd", Ytd(Make("hud_icon", ARGB, 128, 128)), true);
        var nested = RpfFile.CreateNew(RpfFile.CreateDirectory(rpf.Root, "x64"), "vehicles.rpf", RpfEncryption.OPEN);
        RpfFile.CreateFile(nested.Root, "car.ytd", Ytd(
            Make("car_badge", DXT1, 4096, 2048),
            Make("car_livery", DXT5, 2048, 2048),
            Make("car_glass", ARGB, 256, 256),
            Make("car_odd", DXT5, 1500, 1500)), true);
        return rel;
    }

    const string CarYtd = "x64/vehicles.rpf/car.ytd", HudYtd = "hud.ytd";

    static Dictionary<string, Texture> Read(Sandbox s, string rel, string inner)
    {
        var ytd = new YtdFile();
        ytd.Load(new RpfService(s.Game).TryRead(Path.Combine(s.GameRoot, rel), inner)!);
        return ytd.TextureDict.Textures.data_items.ToDictionary(t => t.Name);
    }

    static string Hash(Sandbox s, string rel, string inner) =>
        RpfService.ContentHash(new RpfService(s.Game).TryRead(Path.Combine(s.GameRoot, rel), inner)!);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Optimize_shrinks_a_pack_and_Uninstall_puts_it_back(bool onigiri)
    {
        using var s = new Sandbox(withModsRpf: false);
        var rel = MakeGame(s, onigiri);
        var game = s.Game;
        var car = Hash(s, rel, CarYtd);
        var hud = Hash(s, rel, HudYtd);

        // scan: only the add-on pack, with what 1024 px would save
        var scan = TextureOptimizer.Scan(game, s.Paths, s.Library, 1024);
        var pack = Assert.Single(scan.Packs);
        Assert.Equal(rel, pack.Id);
        Assert.Equal("texpack", pack.Name);
        Assert.Null(pack.Skipped);
        Assert.Null(pack.Owner);
        Assert.Null(pack.AlreadyOptimized);
        Assert.Equal(5, pack.Textures);
        Assert.Equal(3, pack.Oversized); // badge, livery, and the odd-sized one that has to stay
        long at1024 = TextureMips.LevelOffset(DXT1, 4096, 2048, 2) + TextureMips.LevelOffset(DXT5, 2048, 2048, 1);
        Assert.Equal(at1024, pack.SavableBytes);
        Assert.Equal(at1024, scan.SavableBytes);
        Assert.Equal("car_badge", pack.Worst[0].Texture);
        Assert.Equal(CarYtd, pack.Worst[0].Ytd);
        Assert.Equal("DXT1", pack.Worst[0].Format);

        // optimize
        var result = TextureOptimizer.Optimize(game, s.Paths, s.Library, new[] { rel }, 1024);
        Assert.Empty(result.Problems);
        var done = Assert.Single(result.Optimized);
        Assert.Equal(2, done.Textures);
        Assert.Equal(at1024, done.SavedBytes);

        var tex = Read(s, rel, CarYtd);
        Assert.Equal((1024, 512, 3), (tex["car_badge"].Width, tex["car_badge"].Height, (int)tex["car_badge"].Data.FullData[0]));
        Assert.Equal((1024, 1024, 2), (tex["car_livery"].Width, tex["car_livery"].Height, (int)tex["car_livery"].Data.FullData[0]));
        Assert.Equal(256, tex["car_glass"].Width);
        Assert.Equal(1500, tex["car_odd"].Width);
        Assert.Equal(hud, Hash(s, rel, HudYtd)); // nothing to do there, so not rewritten

        var mod = Assert.Single(s.Library.All(), m => m.Source == "tool");
        Assert.Equal(done.ModId, mod.Id);
        Assert.Equal("Smaller textures: texpack", mod.Name);
        Assert.Equal("Texture optimization (max 1024 px)", mod.Summary);
        var entry = Assert.Single(mod.Entries);
        Assert.Equal((JournalEntry.RpfEntry, rel, CarYtd), (entry.Type, entry.Path, entry.Inner));

        // a rescan knows, and has nothing left to save at that size
        pack = Assert.Single(TextureOptimizer.Scan(game, s.Paths, s.Library, 1024).Packs);
        Assert.Equal(new TexAlready(mod.Id, 1024), pack.AlreadyOptimized);
        Assert.Equal(0, pack.SavableBytes);

        // a smaller size starts again from the original textures, under one record
        result = TextureOptimizer.Optimize(game, s.Paths, s.Library, new[] { rel }, 512);
        Assert.Empty(result.Problems);
        done = Assert.Single(result.Optimized);
        Assert.Equal(TextureMips.LevelOffset(DXT1, 4096, 2048, 3) + TextureMips.LevelOffset(DXT5, 2048, 2048, 2), done.SavedBytes);
        tex = Read(s, rel, CarYtd);
        Assert.Equal((512, 256, 4), (tex["car_badge"].Width, tex["car_badge"].Height, (int)tex["car_badge"].Data.FullData[0]));
        Assert.Equal((512, 512, 3), (tex["car_livery"].Width, tex["car_livery"].Height, (int)tex["car_livery"].Data.FullData[0]));
        mod = Assert.Single(s.Library.All(), m => m.Source == "tool");
        Assert.Equal("512 px", mod.Version);

        // uninstall puts every byte back
        Assert.Empty(new ModService(s.Paths, s.Library).Uninstall(game, mod));
        Assert.Equal(car, Hash(s, rel, CarYtd));
        Assert.Equal(hud, Hash(s, rel, HudYtd));
        Assert.DoesNotContain(s.Library.All(), m => m.Source == "tool");
    }

    [Fact]
    public void Scan_names_the_mod_that_installed_a_pack_and_skips_empty_ones()
    {
        using var s = new Sandbox(withModsRpf: false);
        Sandbox.MakeUpdateRpf(Path.Combine(s.GameRoot, "mods", "update"), "update.rpf", FeatureFixtures.DlcListText("dlcpacks:/carpack/", "dlcpacks:/missing/"));
        var rel = @"mods\update\x64\dlcpacks\carpack\dlc.rpf";
        Sandbox.MakeDlcRpf(Path.Combine(s.GameRoot, rel), "carpack");
        var owner = new InstalledMod { Name = "Car Pack [Add-On]", GameId = s.Game.Id, Entries = { new JournalEntry { Type = JournalEntry.File, Path = rel } } };
        s.Library.Upsert(owner);

        var pack = Assert.Single(TextureOptimizer.Scan(s.Game, s.Paths, s.Library, 2048).Packs);
        Assert.Equal(new TexOwner(owner.Id, owner.Name), pack.Owner);
        Assert.Equal("No textures in this pack.", pack.Skipped);

        // and the optimizer refuses anything that isn't an active add-on pack
        var result = TextureOptimizer.Optimize(s.Game, s.Paths, s.Library, new[] { @"update\update.rpf", rel }, 2048);
        Assert.Empty(result.Optimized);
        Assert.Equal(2, result.Problems.Count);
        Assert.Contains("not an active add-on pack", result.Problems[0]);
        Assert.Contains("nothing to shrink", result.Problems[1]);
        Assert.DoesNotContain(s.Library.All(), m => m.Source == "tool");
    }

    [Fact]
    public void Sizes_outside_the_choices_are_refused()
    {
        using var s = new Sandbox();
        Assert.Throws<ArgumentException>(() => TextureOptimizer.Scan(s.Game, s.Paths, s.Library, 300));
    }
}
