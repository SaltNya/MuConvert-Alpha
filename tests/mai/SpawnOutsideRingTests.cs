using AquaMai.ChartVisuals;
using MuConvert.mai;
namespace MuConvert.Tests.mai;
public class SpawnOutsideRingTests
{
 [Theory] [InlineData("5.2",5.2f)] [InlineData("-5.2",-5.2f)] [InlineData("20",20f)]
 public void OutsideRadiusSurvivesResolution(string value,float expected)
 {
  Assert.True(RingState.TryParse("spawn",value,0,out var changes));
  Assert.Equal(expected,RingState.Resolve(changes,1,new VisualNote {Family="hold"}).Spawn);
 }
 [Theory] [InlineData("NaN")] [InlineData("Infinity")] [InlineData("20.1")]
 public void RejectInvalidRadius(string value) => Assert.False(RingState.TryParse("spawn",value,0,out _));
 [Theory] [InlineData("2h~[bones.png]","2~[bones.png]h")] [InlineData("Ch~[bones.png][4:1]","C~[bones.png]h[4:1]")]
 public void SkinAfterHoldKeepsSameNote(string actual,string expected)
 {
  string Convert(string note) {var (c,_) = new SimaiParser(strictLevel:SimaiParser.StrictLevelEnum.Strict).Parse("(120){4}"+note+",1,E");return new MA2Generator().Generate(c).Item1;}
  Assert.Equal(Convert(expected),Convert(actual));
 }
 [Fact] public void ConversionKeepsOutsideSpawn()
 {
  var (chart,_) = new SimaiParser().Parse("(120){4}<SPAWN*5.2><FAKE*True>2h,1,E");
  var ma2 = new MA2Generator().Generate(chart).Item1;
  Assert.Contains("SPAWN\t0\t0\t5.2",ma2);
  var (roundtrip,_) = new MA2Parser().Parse(ma2);
  Assert.Contains("SPAWN\t0\t0\t5.2",new MA2Generator().Generate(roundtrip).Item1);
 }
}
