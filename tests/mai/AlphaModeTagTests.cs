using MuConvert.mai;
namespace MuConvert.Tests.mai;
public class AlphaModeTagTests
{
    [Fact]
    public void NativeOnlyChartStillOptsIntoCosmeticMode()
    {
        var (chart, _) = new SimaiParser().Parse("(120){4}1,2,3,4,E");
        var text = new MA2Generator().Generate(chart).Item1;
        Assert.Equal(1, text.Split('\n').Count(line => line.TrimEnd('\r') == "// SINMAI_ALPHA_MODE\tAlpha"));
        Assert.True(text.IndexOf("// SINMAI_ALPHA_MODE\tAlpha", StringComparison.Ordinal) < text.IndexOf("BPM\t", StringComparison.Ordinal));
    }
    [Fact]
    public void CosmeticMarkerDoesNotChangeMa2Reparse()
    {
        var (chart, _) = new SimaiParser().Parse("(120){4}1,2h[4:1],3,4,E");
        var text = new MA2Generator().Generate(chart).Item1;
        var (tagged, taggedAlerts) = new MA2Parser().Parse(text);
        var (native, nativeAlerts) = new MA2Parser().Parse(text.Replace("// SINMAI_ALPHA_MODE\tAlpha", ""));
        Assert.Equal(native.Notes.Count, tagged.Notes.Count);
        Assert.Equal(nativeAlerts.Count, taggedAlerts.Count);
        Assert.Equal(new MA2Generator().Generate(native).Item1, new MA2Generator().Generate(tagged).Item1);
    }
}
