using System.Diagnostics;
using System.Globalization;

namespace MuConvert.media;

public static class MediaTimelineAudio
{
    public static async Task<string?> BuildAsync(MediaTimelineProject project, string chartDirectory,
        string outputPath, string ffmpeg, CancellationToken cancellationToken = default)
    {
        var clips = project.Clips.Where(c => c.Track == MediaTrackKind.Audio)
            .Select(c => (Clip: c, Path: project.ResolveSourcePath(chartDirectory, c)))
            .Where(c => File.Exists(c.Path)).OrderBy(c => c.Clip.TrackIndex).ThenBy(c => c.Clip.TimelineStart).ToList();
        if (clips.Count == 0) return null;
        if (clips.Count == 1)
        {
            var (clip, path) = clips[0];
            if (clip.TrackIndex == 0 && clip.TimelineStart <= .0001 && clip.SourceOffset <= .0001 &&
                clip.Duration + .02 >= clip.SourceDuration &&
                new[] { "track.mp3", "track.ogg" }.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)) return path;
        }
        outputPath = Path.GetFullPath(outputPath);
        if (clips.Any(c => string.Equals(Path.GetFullPath(c.Path), outputPath, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Timeline output would overwrite an input source.");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var temporary = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp.wav";
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        foreach (var c in clips) { args.Add("-i"); args.Add(c.Path); }
        var filters = clips.Select((c, i) => $"[{i}:a:0]atrim=start={Fmt(c.Clip.SourceOffset)}:duration={Fmt(c.Clip.Duration)}," +
            "asetpts=PTS-STARTPTS,aresample=44100,aformat=sample_fmts=s16:channel_layouts=stereo," +
            $"adelay={Math.Max(0L, (long)Math.Round(c.Clip.TimelineStart * 1000d))}:all=1[a{i}]").ToList();
        filters.Add(clips.Count == 1 ? "[a0]anull[outa]" :
            string.Concat(Enumerable.Range(0, clips.Count).Select(i => $"[a{i}]")) +
            $"amix=inputs={clips.Count}:duration=longest:dropout_transition=0:normalize=0[outa]");
        args.AddRange(new[] { "-filter_complex", string.Join(";", filters), "-map", "[outa]", "-ar", "44100", "-ac", "2", "-c:a", "pcm_s16le", temporary });
        var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(start) ?? throw new IOException("Could not start ffmpeg.");
            var errors = process.StandardError.ReadToEndAsync(cancellationToken);
            try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
            catch { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); throw; }
            var error = await errors.ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(temporary) || new FileInfo(temporary).Length == 0)
                throw new InvalidDataException("Timeline audio mix failed: " + error);
            File.Move(temporary, outputPath, true);
            return outputPath;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    // The reference mixer rounds trim values to three decimal places.
    private static string Fmt(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
