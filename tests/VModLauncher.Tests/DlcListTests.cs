using VModLauncher.Core.Install;
using Xunit;

namespace VModLauncher.Tests;

public class DlcListTests
{
    [Fact]
    public void Add_puts_one_line_before_Paths_with_matching_indent()
    {
        var xml = DlcList.Add(Sandbox.OriginalDlcList, "dlcpacks:/mycar/");
        Assert.Contains("\t\t<Item>dlcpacks:/mpbeach/</Item>\r\n\t\t<Item>dlcpacks:/mycar/</Item>\r\n\t</Paths>", xml);
        Assert.Equal(xml, DlcList.Add(xml, "dlcpacks:/mycar/"));
        Assert.Equal(xml, DlcList.Add(xml, "DLCPACKS:/MyCar/"));
    }

    [Fact]
    public void Remove_restores_the_original_text()
    {
        var xml = DlcList.Add(Sandbox.OriginalDlcList, "dlcpacks:/mycar/");
        Assert.Equal(Sandbox.OriginalDlcList, DlcList.Remove(xml, "dlcpacks:/mycar/"));
    }

    [Fact]
    public void Add_after_an_anchor_puts_it_back_in_place()
    {
        var xml = "<Paths>\r\n\t<!--<Item>dlcpacks:/a/</Item>-->\r\n\t<Item>dlcpacks:/a/</Item>\r\n\t<Item>dlcpacks:/c/</Item>\r\n</Paths>";
        Assert.Equal("dlcpacks:/a/", DlcList.ItemBefore(xml, "dlcpacks:/c/"));
        Assert.Null(DlcList.ItemBefore(xml, "dlcpacks:/a/"));

        var added = DlcList.Add(xml, "dlcpacks:/b/", "dlcpacks:/a/");
        Assert.Equal(new[] { "dlcpacks:/a/", "dlcpacks:/b/", "dlcpacks:/c/" }, DlcList.Items(added));
        Assert.Contains("-->\r\n\t<Item>dlcpacks:/a/</Item>\r\n\t<Item>dlcpacks:/b/</Item>\r\n", added); // not after the commented copy
        Assert.Equal(xml, DlcList.Remove(added, "dlcpacks:/b/"));
        // anchor gone: the end, as before
        Assert.Equal(DlcList.Add(xml, "dlcpacks:/b/"), DlcList.Add(xml, "dlcpacks:/b/", "dlcpacks:/gone/"));
    }

    [Fact]
    public void Works_with_unix_newlines_and_no_items()
    {
        var xml = "<SMandatoryPacksData>\n  <Paths>\n  </Paths>\n</SMandatoryPacksData>";
        var added = DlcList.Add(xml, "dlcpacks:/a/");
        Assert.Contains("<Item>dlcpacks:/a/</Item>\n  </Paths>", added);
        Assert.Single(DlcList.Items(added));
    }

    [Fact]
    public void Bom_round_trips()
    {
        var bytes = DlcList.Encode("abc", bom: true);
        Assert.Equal("abc", DlcList.Decode(bytes, out var bom));
        Assert.True(bom);
    }
}
