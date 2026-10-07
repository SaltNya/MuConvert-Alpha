using Newtonsoft.Json;

namespace MuConvert.media;

public enum MediaTrackKind { Video, Audio }

public sealed class MediaTimelineClip
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public MediaTrackKind Track { get; set; }
    public int TrackIndex { get; set; }
    public string SourcePath { get; set; } = "";
    public string Name { get; set; } = "";
    public double TimelineStart { get; set; }
    public double SourceOffset { get; set; }
    public double SourceDuration { get; set; }
    public double Duration { get; set; } = 1d;
    public bool IsStillImage { get; set; }
    [JsonIgnore] public double TimelineEnd => TimelineStart + Duration;
}

// Load and normalization policy from the supplied Alpha editor. A valid
// working copy takes precedence over the saved project, even when empty.
public sealed class MediaTimelineProject
{
    public const string FileName = "media_timeline.json";
    public const string TemporaryFileName = FileName + ".tmp";
    public int Version { get; set; } = 1;
    public List<MediaTimelineClip> Clips { get; set; } = new();
    public static MediaTimelineProject LoadWorking(string chartDirectory) =>
        LoadFile(Path.Combine(chartDirectory, TemporaryFileName)) ??
        LoadFile(Path.Combine(chartDirectory, FileName)) ?? new();

    private static MediaTimelineProject? LoadFile(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var project = JsonConvert.DeserializeObject<MediaTimelineProject>(File.ReadAllText(path)) ?? new();
            project.Clips ??= new();
            foreach (var clip in project.Clips)
            {
                clip.Id = string.IsNullOrWhiteSpace(clip.Id) ? Guid.NewGuid().ToString("N") : clip.Id;
                clip.Name = string.IsNullOrWhiteSpace(clip.Name) ? Path.GetFileName(clip.SourcePath) : clip.Name;
                clip.TrackIndex = Math.Clamp(clip.TrackIndex, 0, 1);
                clip.TimelineStart = Math.Max(0d, FiniteOr(clip.TimelineStart, 0d));
                clip.SourceOffset = Math.Max(0d, FiniteOr(clip.SourceOffset, 0d));
                clip.Duration = Math.Max(0.01d, FiniteOr(clip.Duration, 1d));
                clip.SourceDuration = Math.Max(clip.SourceOffset + clip.Duration,
                    FiniteOr(clip.SourceDuration, clip.SourceOffset + clip.Duration));
            }
            return project;
        }
        catch { return null; }
    }
    private static double FiniteOr(double value, double fallback) => double.IsFinite(value) ? value : fallback;
    public string ResolveSourcePath(string chartDirectory, MediaTimelineClip clip) =>
        Path.IsPathRooted(clip.SourcePath) ? clip.SourcePath : Path.GetFullPath(Path.Combine(chartDirectory, clip.SourcePath));
    public bool HasAudioTimeline => Clips.Any(clip => clip.Track == MediaTrackKind.Audio);
    public bool IsDefaultVideoPassThrough(string chartDirectory)
    {
        var clips = Clips.Where(clip => clip.Track == MediaTrackKind.Video).ToList();
        if (clips.Count != 1) return false;
        var clip = clips[0];
        var fileName = Path.GetFileName(ResolveSourcePath(chartDirectory, clip));
        return new[] { "pv.mp4", "mv.mp4", "bg.mp4" }.Contains(fileName, StringComparer.OrdinalIgnoreCase) &&
            clip.TrackIndex == 0 && clip.TimelineStart <= .0001d && clip.SourceOffset <= .0001d &&
            clip.Duration + .02d >= clip.SourceDuration;
    }
}
