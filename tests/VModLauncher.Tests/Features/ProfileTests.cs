using VModLauncher.Core;
using VModLauncher.Core.Features;
using Xunit;
using static VModLauncher.Tests.Features.FeatureFixtures;

namespace VModLauncher.Tests.Features;

public class ProfileTests
{
    [Fact]
    public void Save_apply_and_share_a_profile()
    {
        using var s = new Sandbox();
        var a = InstallZip(s, "Mod A", d => Write(d, "A/A.asi", "a"), "https://www.gta5-mods.com/scripts/mod-a");
        var b = InstallZip(s, "Mod B", d => Write(d, "B/B.asi", "b"), "https://www.gta5-mods.com/scripts/mod-b");
        var service = new ModService(s.Paths, s.Library);
        var store = new ProfileStore(s.Paths.ProfilesFile);

        var both = store.SaveCurrent(s.Library, s.Game, "Both");
        Assert.Equal(2, both.Mods.Count);
        Assert.True(ProfileStore.IsActive(both, s.Library));

        service.Disable(s.Game, s.Library.Find(b.Id)!);
        var onlyA = store.SaveCurrent(s.Library, s.Game, "Only A");
        Assert.Single(onlyA.Mods);
        Assert.False(ProfileStore.IsActive(both, s.Library));

        var r = ProfileStore.Apply(both, service, s.Game);
        Assert.Empty(r.Problems);
        Assert.Contains(b.Id, r.Enabled);
        Assert.True(File.Exists(Path.Combine(s.GameRoot, "B.asi")));
        Assert.True(ProfileStore.IsActive(both, s.Library));

        r = ProfileStore.Apply(onlyA, service, s.Game);
        Assert.Contains(b.Id, r.Disabled);
        Assert.False(File.Exists(Path.Combine(s.GameRoot, "B.asi")));
        Assert.True(File.Exists(Path.Combine(s.GameRoot, "A.asi")));

        // share code round trip, on "another PC" where Mod B isn't installed
        var code = ProfileStore.Export(both, s.Game.Edition);
        Assert.StartsWith("VML1:", code);
        new ModService(s.Paths, s.Library).Uninstall(s.Game, s.Library.Find(b.Id)!);
        var (imported, missing, edition) = store.Import(code, s.Library, s.Game);
        Assert.Equal("Both", imported.Name);
        Assert.Equal("Legacy", edition);
        var m = Assert.Single(missing);
        Assert.Equal("https://www.gta5-mods.com/scripts/mod-b", m.PageUrl);
        Assert.Equal(a.Id, imported.Mods.Single(x => x.Name == "Mod A").Id);

        Assert.Throws<FormatException>(() => store.Import("not a code", s.Library, s.Game));
        Assert.Throws<FormatException>(() => store.Import("VML1:!!!", s.Library, s.Game));

        // persisted
        Assert.Equal(3, new ProfileStore(s.Paths.ProfilesFile).ForGame(s.Game.Id).Count());
    }
}
