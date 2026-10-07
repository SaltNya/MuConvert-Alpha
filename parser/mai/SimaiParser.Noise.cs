using AquaMai.ChartVisuals;
using AquaMai.Alpha053.Core;
using static MuConvert.utils.Alert.LEVEL;
using P = MuConvert.Antlr.SimaiParser;

namespace MuConvert.mai;

public partial class SimaiParser
{
    public sealed override object VisitNoiseZone(P.NoiseZoneContext context)
    {
        currContext = context;
        if (SubtreeHasException(context)) return false;
        var zone = context.NOISE_ZONE().GetText();
        var sensor = NoiseZoneTimeline.SensorIndex(zone[1], zone.Length == 3 ? zone[2] - '0' : 1);
        var rawDuration = context.slideDuration()?.GetText();
        double duration = 1d / 60;
        if (sensor < 0 || rawDuration != null && (!SlideSyntaxValidator.TryGetLengthSeconds(rawDuration, (double)chart.BpmList.Find(now).Bpm, out duration) ||
                duration < 0 || double.IsNaN(duration) || double.IsInfinity(duration)))
        { AddAlert(Warning, "Invalid noise-region duration: " + context.GetText()); return false; }
        // Alpha resolves noise through SimaiChart.CommaTimings. The packaged
        // MajSimai parser stores HS=1 for these timing points; normal-note HS
        // and typed Touch overrides belong to the separate note timeline.
        chart.Commands.Add((now - extendedFalseEach, "noisezone", new NoiseZoneSpec(sensor, duration, 1).Encode()));
        return true;
    }
}
