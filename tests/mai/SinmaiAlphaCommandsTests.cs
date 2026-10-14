using MuConvert.mai;

namespace MuConvert.Tests.mai;

public class SinmaiAlphaCommandsTests
{
    private static string Convert(string inote)
    {
        var (chart, _) = new SimaiParser().Parse(inote);
        return new MA2Generator().Generate(chart).Item1;
    }
    [Fact]
    public void EditorOnlyCommandsDoNotChangeNotesOrTiming()
    {
        var plain = "(120){4}1,2,3,4,E";
        var withCommands = "(120){4}<ShowJudgeInfo*(True,0)>1,<ShowComboInfo*(False,8:1)>2,<OuterBrightness*(0.5,0.1)>3,<ShowJudgeInfo*(False,0.2)><ShowComboInfo*(True,0)><OuterBrightness*(Default,0)>4,E";
        Assert.Equal(Convert(plain), Convert(withCommands));
    }
    [Theory]
    [InlineData("None",0)] [InlineData("Off",0)] [InlineData("Combo",1)]
    [InlineData("Score",2)] [InlineData("Achievement",3)] [InlineData("Acc",3)]
    [InlineData("AccDown",4)] [InlineData("DxAcc",11)] [InlineData("DxAccDown",12)]
    [InlineData("DxScore",13)] [InlineData("CScore",101)] [InlineData("CScoreDedxDown",102)]
    [InlineData("Default",-1)]
    public void NativeDisplayModeAndBeatDurationAreEncoded(string mode,int code)
    {
        Assert.Contains($"COMBODISPLAY\t0\t0\t{code},0.25,0,0,0",Convert($"(120){{4}}<ComboDisplay*({mode},8:1)>1,E"));
    }
}
