using AquaMai.ChartVisuals;
using AquaMai.Alpha053.Core;
using AquaMai.Alpha053.Geometry;
using AquaMai.Alpha053.FloatMath;
using P = MuConvert.Antlr.SimaiParser;
namespace MuConvert.mai;

public partial class SimaiParser
{
    public sealed override object VisitBorrowedNote(P.BorrowedNoteContext context)
    {
        currContext = context;
        extraModifiers.Clear();
        return BuildBorrowedNote(context.GetText());
    }
    internal static string WithoutSVModifier(string source, out bool ignoreSV)
    {
        // The vendored geometry parser predates c. Remove only carrier-level
        // c modifiers; bracket contents (skins, route and duration) stay intact.
        var geometrySource = new System.Text.StringBuilder(source.Length);
        var depth = 0;
        ignoreSV = false;
        foreach (var character in source)
        {
            if (character == '[') depth++;
            if (character == 'c' && depth == 0) { ignoreSV = true; continue; }
            geometrySource.Append(character);
            if (character == ']') depth--;
        }
        return geometrySource.ToString();
    }
    private BorrowedNote BuildBorrowedNote(string source)
    {
        if (!NoteExpressionParser.TryParse(WithoutSVModifier(source, out var ignoreSV), out var parsed, out var error) || parsed.trajectory == null)
            throw new ArgumentException("Invalid borrowed trajectory: " + error);
        var points = new List<Vector3>();
        // The reference accepts some degenerate paths (e.g. same-point Q9),
        // then disables their carrier in Configure. Preserve that no-picture
        // result, instead of failing conversion or displaying a normal Tap.
        if (!TrajectoryPathGeometry.TryBuild(new BorrowedRouteProvider(), parsed.trajectory.segments, points)) points.Clear();
        double seconds = 0;
        var bpm = (double)chart.BpmList.Find(now).Bpm;
        foreach (var segment in parsed.trajectory.segments)
        {
            if (string.IsNullOrEmpty(segment.duration)) continue;
            if (!SlideSyntaxValidator.TryGetLengthSeconds(segment.duration, bpm, out var duration))
                throw new ArgumentException("Invalid borrowed trajectory duration: " + segment.duration);
            seconds += duration;
        }
        var family = parsed.modifiers.HasHead(NoteModifierFlags.ForceStar) ? "star" : parsed.kind switch
        { NoteExpressionKind.Hold => "hold", NoteExpressionKind.Touch => "touch", NoteExpressionKind.TouchHold => "touchhold", _ => "tap" };
        var trajectory = new BorrowedTrajectory { Source = source, Route = parsed.trajectorySource,
            Family = family, Seconds = (float)Math.Max(.01, seconds) };
        foreach (var point in points) trajectory.Points.Add(new BorrowedTrajectory.Point(point.x, point.y));
        return new BorrowedNote(chart, now, trajectory)
        {
            Skin = string.IsNullOrEmpty(parsed.position.skin) ? null : parsed.position.skin,
            IsBreak = parsed.modifiers.HasHead(NoteModifierFlags.Break),
            IsEx = parsed.modifiers.HasHead(NoteModifierFlags.Ex),
            IsMine = parsed.modifiers.HasHead(NoteModifierFlags.Mine),
            IgnoreSV = ignoreSV
        };
    }
}
