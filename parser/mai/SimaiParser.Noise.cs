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
        // Create the event first; after parsing this stream, ApplyNoiseControls
        // attaches its 0.6.0 SV/HS and blockarea visual timelines.
        chart.Commands.Add((now - extendedFalseEach, "noisezone", new NoiseZoneSpec(sensor, duration, 1).Encode()));
        return true;
    }
    private void ApplyNoiseControls()
    {
        var source = chart.Commands.Where(c => c.Kind is "sv" or "hs" || VisualState.IsKind(c.Kind)).OrderBy(c => c.Time).ToArray();
        for (var i = 0; i < chart.Commands.Count; i++)
        {
            var command = chart.Commands[i];
            if (command.Kind != "noisezone" || !NoiseZoneSpec.TryDecode(command.Value, out var zone)) continue;
            var controls = new NoiseControls();
            foreach (var c in source)
            {
                string text;
                if (c.Kind is "sv" or "hs")
                {
                    var parts = c.Value.Split(',').Where(p => !p.Contains('=') || NoiseControls.IsTarget(p[..p.IndexOf('=')].Trim()));
                    text = string.Join(",", parts);
                    if (text.Length == 0) continue;
                }
                else
                {
                    if (!VisualState.TryParse(c.Kind, c.Value, 0, out var changes)) continue;
                    var parts = changes.Where(x => x.Type == "blockarea").Select(x => c.Kind.TrimEnd('v') switch
                    {
                        "color" => "blockarea=" + (x.Value.Color?.ToString("X6") ?? "NULL"),
                        "alpha" => "blockarea=" + (x.Value.Alpha?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "NULL"),
                        _ => "blockarea=" + (x.Value.X.HasValue ? "(" + x.Value.X.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," + x.Value.Y.GetValueOrDefault(1).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ")" : "NULL")
                    });
                    text = string.Join(",", parts); if (text.Length == 0) continue;
                }
                controls.Entries.Add(new NoiseControls.Entry { Time = (double)(chart.ToSecond(c.Time) - chart.ToSecond(command.Time)), Kind = c.Kind, Value = text });
            }
            if (!NoiseControls.Decode(controls.Encode(), out var validated)) throw new ArgumentException("Invalid noise control");
            chart.Commands[i] = (command.Time, command.Kind, new NoiseZoneSpec(zone.Sensor, zone.Duration, validated.Speed("hs", 0), validated).Encode());
        }
    }
}
