using System.Globalization;
using MuConvert.chart;
using Rationals;

namespace MuConvert.mai;

public partial class SimaiParser
{
    // Parse each stream on its own BPM clock, then map seconds onto the completed
    // main BPM timeline. Future main BPM changes must not alter overlay durations.
    private void MergeOverlayStreams()
    {
        foreach (var stream in overlayStreams)
        {
            if (stream.Text.Contains("@*") || stream.Text.Contains("@{"))
                throw new ArgumentException("Nested independent streams are not supported by Majdata.");
            var parser = new SimaiParser(chart.DefaultTouchSize == "L1", chart.ClockCount, StrictLevel) { preserveEditorDirectives = true, isIndependentStream = true };
            var (local, localAlerts) = parser.Parse("(" + stream.Bpm.ToString(CultureInfo.InvariantCulture) + ")" + stream.Text);
            chart.HasVisualCommands |= local.HasVisualCommands;
            var origin = chart.ToSecond(stream.Time);
            Rational Map(Rational seconds) => chart.BpmList.ConvertTime(0, origin + seconds, 240, null);
            // Frame/display commands belong to the shared screen. Emit each
            // authored event once, rather than once per note-speed group.
            foreach (var command in local.Commands.Where(c => c.Kind is "text" or "noisezone" || AquaMai.ChartVisuals.MediaCommands.IsKind(c.Kind) || AquaMai.ChartVisuals.PresentationCommands.IsKind(c.Kind)))
                chart.Commands.Add((Map(local.ToSecond(command.Time)), command.Kind, command.Value));
            foreach (var alert in localAlerts)
            {
                if (alert.Line.HasValue) alert.Line += stream.Line - 1;
                if (alert.TimeInBar.HasValue) alert.TimeInBar = Map(local.ToSecond(alert.TimeInBar.Value));
                if (alert.TimeInSeconds.HasValue) alert.TimeInSeconds += (double)origin;
                alerts.Add(alert);
            }
            var notes = new HashSet<Note>();
            void Collect(Note note)
            {
                if (!notes.Add(note)) return;
                foreach (var child in note.Children.OfType<Note>()) Collect(child);
            }
            foreach (var note in local.Notes)
            {
                if (note is Slide { OwnHead: not null } s) s.OwnHead.FalseEachIdx = s.FalseEachIdx;
                Collect(note);
            }
            var touchHeads = new Dictionary<Slide, TouchStar>();
            foreach (var slide in local.Notes.OfType<Slide>().Where(s => s.StartArea != "" && s.OwnHead == null && s.SharedHeadWith == null && !s.NoHead))
            {
                var head = new TouchStar(local, slide.Time) { IsMine = slide.HeadIsMine, IsBreak = slide.HeadIsBreak, FalseEachIdx = slide.FalseEachIdx,
                    TouchArea = slide.StartArea == "C" ? "C" : slide.StartArea + slide.Key };
                touchHeads[slide] = head;
                notes.Add(head);
            }
            string Family(Note n) => n switch
            {
                Slide => "slide", TouchHold => "touchhold", TouchStar => "star",
                Touch => "touch", Hold => "hold", Star => "star", _ => "tap"
            };
            foreach (var pair in touchHeads) pair.Value.Visual = pair.Key.HeadVisual;
            var groups = notes.GroupBy(n => (Type: n is BorrowedNote ? "slide" : n.Visual?.Family ?? Family(n),
                IsMine: n is not BorrowedNote && n.IsMine, IsBreak: n is not BorrowedNote && n.IsBreak,
                Each: n.Visual?.Each ?? false)).ToList();
            foreach (var group in groups)
            {
                var id = "s" + ++streamSeq;
                void Put(string kind, Rational time, string value)
                {
                    // The game sorts equal-time entries without a stable order.
                    // Emit only the last authored state for a scoped key/time.
                    chart.Commands.RemoveAll(c => c.Kind == kind && c.Time == time && c.Value.StartsWith(id + "="));
                    chart.Commands.Add((time, kind, id + "=" + value));
                }
                foreach (var note in group) note.StreamId = id;
                // Copy live state into each existing speed-curve group. The visual
                // target remains intact and resolves against the note's baked class.
                foreach (var command in local.Commands.Where(c => AquaMai.ChartVisuals.VisualState.IsKind(c.Kind) && c.Kind.EndsWith('v')))
                    chart.Commands.Add((Map(local.ToSecond(command.Time)), command.Kind, id + "~" + command.Value));
                // Explicit defaults also isolate notes before their first local command.
                Put("sv", 0, "1");
                Put("hs", 0, "1");
                foreach (var kind in new[] { "sv", "hs", "spawn", "spawnmode", "destroy", "bounce" })
                {
                    var state = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var events = local.Commands.Where(c => c.Kind == kind).OrderBy(c => c.Time).ToList();
                    var priorities = new List<string>();
                    if (group.Key.IsMine) priorities.Add("mine");
                    if (group.Key.IsBreak) priorities.Add("break");
                    if (group.Key.Each) priorities.Add("each");
                    priorities.Add(group.Key.Type);
                    if (kind != "sv" || group.Key.Type != "slide") priorities.Add("");
                    if ((kind == "hs" || kind == "sv") && group.Key.Type == "slide")
                    { priorities.Clear(); priorities.Add("slide"); }
                    // ResolveSvType selects a typed curve for the entire note.
                    // Clearing that curve falls back to its stream global curve,
                    // rather than selecting a lower-priority typed curve.
                    var declared = events.SelectMany(c => c.Value.Split(','))
                        .Where(p => p.Contains('='))
                        .Select(p => p[..p.IndexOf('=')].Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var svKey = priorities.FirstOrDefault(declared.Contains);
                    foreach (var command in events)
                    {
                        foreach (var part in command.Value.Split(','))
                        {
                            var equal = part.IndexOf('=');
                            var key = equal < 0 ? "" : part[..equal].Trim();
                            var value = equal < 0 ? part.Trim() : part[(equal + 1)..].Trim();
                            if (value.Equals("NULL", StringComparison.OrdinalIgnoreCase) ||
                                value.Equals("FALSE", StringComparison.OrdinalIgnoreCase)) state.Remove(key);
                            else state[key] = FreezeBeatValue(value, local.BpmList.Find(command.Time).Bpm);
                        }
                        var selected = priorities.FirstOrDefault(state.ContainsKey);
                        if (kind == "sv") selected = svKey != null && state.ContainsKey(svKey) ? svKey
                            : group.Key.Type != "slide" && state.ContainsKey("") ? "" : null;
                        var valueAtTime = selected != null ? state[selected] : kind switch
                        { "sv" or "hs" => "1", "spawn" => "1.225", "spawnmode" => "Rewind", "destroy" => "4.8", _ => "0" };
                        Put(kind, Map(local.ToSecond(command.Time)), valueAtTime);
                    }
                }
            }
            foreach (var pair in touchHeads) pair.Key.HeadStreamId = pair.Value.StreamId;
            // Snapshot every duration before rebinding any note (shared slide heads
            // and segments refer to their original chart for invariant BPM values).
            var snapshots = notes.Select(n => new
            {
                Note = n,
                Seconds = local.ToSecond(n.Time) + (Rational)(1.875m / local.BpmList.Find(n.Time).Bpm) * n.FalseEachIdx,
                Duration = n.Duration.Seconds,
                Wait = n is Slide s ? s.WaitTime.Seconds : Rational.Zero,
                Segments = n is Slide slide ? slide.segments.Select(s => s.Duration?.Seconds).ToArray() : []
            }).ToList();
            foreach (var snapshot in snapshots)
            {
                var note = snapshot.Note;
                note.Chart = chart;
                note.Time = Map(snapshot.Seconds);
                note.FalseEachIdx = 0;
                if (note is Slide slide)
                {
                    slide.WaitTime = new Duration(slide) { Seconds = snapshot.Wait };
                    if (slide.segments.Count == 0) slide.RawDuration = new Duration(slide) { Seconds = snapshot.Duration };
                    for (int i = 0; i < slide.segments.Count; i++)
                        slide.segments[i].Duration = snapshot.Segments[i] is { } seconds ? new Duration(slide) { Seconds = seconds } : null;
                }
                else if (note is Hold or TouchHold) note.Duration = new Duration(note) { Seconds = snapshot.Duration };
            }
            chart.Notes.AddRange(local.Notes);
        }
    }

    private static string FreezeBeatValue(string value, decimal bpm)
    {
        var parts = value.Split(':');
        if (parts.Length == 2 && decimal.TryParse(parts[0], NumberStyles.Number, CultureInfo.InvariantCulture, out var division)
            && decimal.TryParse(parts[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var count) && division > 0)
            return (240m / bpm / division * count).ToString(CultureInfo.InvariantCulture);
        return value;
    }
}
