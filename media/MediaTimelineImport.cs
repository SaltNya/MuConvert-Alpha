using AquaMai.ChartVisuals;
using MuConvert.mai;
using Rationals;

namespace MuConvert.media;

public static class MediaTimelineImport
{
    // Apply after the chart's own first/padding shift. TimelineStart already
    // measures music seconds; only an actual main-audio padding moves it.
    public static void Apply(MaiChart chart, MediaTimelineProject project, string chartDirectory,
        double audioPadding = 0, Action<string>? warning = null)
    {
        if (!double.IsFinite(audioPadding)) throw new ArgumentOutOfRangeException(nameof(audioPadding));
        if (project.Clips.Count == 0) return;
        if (project.HasAudioTimeline) chart.Commands.RemoveAll(c => c.Kind == "audio");
        if (project.IsDefaultVideoPassThrough(chartDirectory)) return;
        foreach (var clip in project.Clips.Where(clip => clip.Track == MediaTrackKind.Video))
        {
            var source = project.ResolveSourcePath(chartDirectory, clip);
            if (!File.Exists(source)) { warning?.Invoke("Missing timeline media: " + clip.SourcePath); continue; }
            var path = Path.GetRelativePath(chartDirectory, source).Replace('\\', '/');
            // The editor skips outside-chart video sources when constructing
            // its effective playback media table. Audio is baked separately.
            if (Path.IsPathRooted(path) || path.StartsWith("..", StringComparison.Ordinal)) continue;
            if (MediaCommands.NormalizePath("pvoverlay", path) != path)
                throw new InvalidDataException("Unsupported timeline media path: " + path);
            var start = clip.TimelineStart + audioPadding;
            var duration = clip.Duration;
            var offset = clip.SourceOffset;
            if (start < 0) { duration += start; offset -= start; start = 0; }
            if (duration <= 0) continue;
            var on = new MediaChange { kind = "pvOverlay", enabled = true, path = path, track = clip.TrackIndex,
                sourceOffset = offset, duration = duration, timelineClip = true };
            var off = new MediaChange { kind = "pvOverlay", track = clip.TrackIndex, timelineClip = true };
            chart.Commands.Add((Bar(start), "pvoverlay", on.Encode()));
            chart.Commands.Add((Bar(start + duration), "pvoverlay", off.Encode()));
        }
        // Match the editor at clip boundaries: disable before enable at the
        // same timestamp; retain stable source order otherwise.
        chart.Commands = chart.Commands.OrderBy(c => c.Time)
            .ThenBy(c => MediaCommands.Decode(c.Kind, c.Value, 0, out var item) && !item.enabled ? 0 : 1).ToList();
        Rational Bar(double seconds) => chart.BpmList.ConvertTime(0, (Rational)(decimal)seconds, 240, null);
    }
}
