using AquaMai.ChartVisuals;
using MuConvert.mai;

namespace MuConvert.Tests.mai;

public class Alpha060Tests
{
    static PresentationChange Parse(string kind, string body, double time = 0)
    {
        Assert.True(PresentationCommands.TryParse(kind, body, 120, out var c));
        Assert.True(PresentationCommands.Decode(kind, c.Encode(), time, out var decoded));
        return decoded;
    }
    static NoiseZoneSpec[] Noise(string text)
    {
        var (chart, _) = new SimaiParser().Parse(text.Replace(",E", ",1,E"));
        var encoded = new MA2Generator().Generate(chart).Item1;
        var (restored, _) = new MA2Parser().Parse(encoded);
        var again = new MA2Generator().Generate(restored).Item1;
        var rows = encoded.Split('\n').Where(l => l.StartsWith("NZONE\t")).ToArray();
        Assert.NotEmpty(rows);
        Assert.Equal(rows, again.Split('\n').Where(l => l.StartsWith("NZONE\t")).ToArray());
        return rows.Select(r => { Assert.True(NoiseZoneSpec.TryDecode(r.Trim().Split('\t')[3], out var n)); return n; }).ToArray();
    }
    [Fact] public void RotateKeepsLegacyPVAndNewOptIn()
    {
        var noPV = Parse("rotate", "(True,90,1)");
        var pv = Parse("rotate", "(True,90,1,True)");
        Assert.False(noPV.RotatePV); Assert.True(pv.RotatePV);
        Assert.True(PresentationCommands.Decode("rotate", "EF1|90,1,0,0,0,1,0,0,0", 0, out var old));
        Assert.True(old.RotatePV);
        Assert.Equal(45, new PresentationCommands.Track(new[]{noPV}, "rotate").Evaluate(500));
        Assert.Equal(0, new PresentationCommands.Track(new[]{noPV}, "rotate").EvaluatePVRotation(500));
        Assert.Equal(45, new PresentationCommands.Track(new[]{pv}, "rotate").EvaluatePVRotation(500));
    }
    [Theory] [InlineData(-90)] [InlineData(0)] [InlineData(270)]
    public void KaleidoscopeAngleIsIndependentOfVisibility(int angle)
    {
        var c = Parse("kaleidoscope", $"(True,{angle},0)");
        var track = new PresentationCommands.Track(new[]{c}, "kaleidoscope");
        Assert.Equal(1, track.Evaluate(100));
        Assert.Equal(angle, track.EvaluateKaleidoscopeAngle(100));
        Assert.True(c.AngleMode);
        Assert.True(PresentationCommands.Decode("kaleidoscope", "EF1|0.5,0,0,0,0,1,0,0,0", 0, out var old));
        Assert.Equal(.5f, new PresentationCommands.Track(new[]{old}, "kaleidoscope").Evaluate(100));
    }
    [Fact] public void ExpansionSupportsOffAndRewind()
    {
        var on = Parse("judgelineexpand", "(True,1,CCW)");
        var off = Parse("judgelineexpand", "(False)", 1500);
        var track = new JudgeLineExpansionCommands.Track(new[]{on,off});
        Assert.True(track.Evaluate(500, out var angle)); Assert.Equal(11.25f,angle);
        Assert.False(track.Evaluate(1600, out _));
        Assert.True(track.Evaluate(1000, out angle)); Assert.Equal(22.5f,angle);
        Assert.Equal(-22.5f,Parse("judgelineexpand","(True)").Target);
        Assert.Equal(-22.5f,Parse("judgelineexpand","(1,CW)").Target);
    }
    [Theory] [InlineData("showjudgetext")] [InlineData("showjudgeinfo")]
    [InlineData("showcomboinfo")] [InlineData("innerbrightness")]
    public void DefaultRestoresPerPlayerValue(string kind)
    {
        var start=Parse(kind,kind.StartsWith("show")?"(False)":"(0)");
        var reset=Parse(kind,"(Default,1)",1000);
        var track=new PresentationCommands.Track(new[]{start,reset},kind);
        Assert.Equal(.3f,track.EvaluatePlayerBrightness(1500,.6f),5);
        Assert.Equal(.6f,track.EvaluatePlayerBrightness(2000,.6f),5);
        Assert.False(track.IsPlayerBrightnessActive(2000));
        Assert.Equal(.1f,track.EvaluatePlayerBrightness(1500,.2f),5);
    }
    [Fact] public void NoiseRetainsTypedControlsAndExcludesGlobalColor()
    {
        var n=Assert.Single(Noise("(120){4}<SV*2><HS*3><COLOR*#00FF00><COLOR*noise=#FF0000><SIZE*blockarea=(2,0.5)><ALPHA*blockarea=0.4>XA1[4:1],E"));
        Assert.NotNull(n.Controls); Assert.Equal(2,n.Controls.Speed("sv",0)); Assert.Equal(3,n.Controls.Speed("hs",0));
        var style=n.Controls.Style(0); Assert.Equal(0xFF0000,style.Color); Assert.Equal(2,style.X); Assert.Equal(.5f,style.Y); Assert.Equal(.4f,style.Alpha);
        var global=Assert.Single(Noise("(120){4}<COLOR*#00FF00>XA1[4:1],E"));
        Assert.Null(global.Controls.Style(0).Color);
    }
    [Fact] public void NoiseIndependentStreamDoesNotBorrowMainState()
    {
        var zones=Noise("(120){4}<HS*noise=2><COLOR*noise=#FF0000>@*{4}<HS*noise=4><COLOR*noise=#0000FF>XB1[4:1],*@XA1[4:1],E");
        Assert.Equal(2,zones.Length);
        Assert.Equal(2,zones.Single(z=>z.Sensor==0).Controls.Speed("hs",0));
        Assert.Equal(4,zones.Single(z=>z.Sensor==8).Controls.Speed("hs",0));
        Assert.Equal(0x0000FF,zones.Single(z=>z.Sensor==8).Controls.Style(0).Color);
    }
    [Fact] public void SpeedResetSignedSVAndSameTimeOrderAreDeterministic()
    {
        var controls=new NoiseControls();
        controls.Entries.Add(new(){Time=-2,Kind="sv",Value="-2"});
        controls.Entries.Add(new(){Time=-1,Kind="sv",Value="2"});
        controls.Entries.Add(new(){Time=-2,Kind="hs",Value="3"});
        for(int i=0;i<30;i++) controls.Entries.Add(new(){Time=0,Kind="hs",Value="noise="+(i+1)});
        controls.Entries.Add(new(){Time=0,Kind="hs",Value="noise=NULL"});
        Assert.True(NoiseControls.Decode(controls.Encode(),out var decoded));
        Assert.Equal(3,decoded.Speed("hs",0));
        Assert.Equal(0,decoded.ScrollDistance(-2,0));
        Assert.Equal(1,decoded.WarningProgress(-2,1));
    }
    [Theory] [InlineData("1-2[4:1]")] [InlineData("1-8[4:1]")]
    public void CustomAdjacentStraightRemainsAvailable(string note)
    {
        var (chart,alerts)=new SimaiParser().Parse("(120){4}"+note+",E");
        Assert.NotEmpty(chart.Notes);
        var ma2=new MA2Generator().Generate(chart).Item1;
        Assert.Contains("SSS",ma2);
    }
}
