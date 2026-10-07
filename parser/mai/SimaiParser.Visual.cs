using AquaMai.ChartVisuals;
using static MuConvert.utils.Alert.LEVEL;

namespace MuConvert.mai;

public partial class SimaiParser
{
    private void MarkReferencePresentation()
    {
        var referenceCommands = chart.Commands.Any(c => c.Kind is "sv" or "hs" || RingState.IsKind(c.Kind));
        if (!referenceCommands && !chart.Notes.OfType<Slide>().Any(s => s.HasSelectableOrbit)) return;
        chart.HasVisualCommands = true;
        var seen = new HashSet<Note>();
        void Mark(Note note)
        {
            if (!seen.Add(note)) return;
            var reference = referenceCommands || note is Slide { HasSelectableOrbit: true };
            if (note.Visual != null && reference) note.Visual.ReferenceMotion = true;
            if (note is Slide slide)
            {
                if (slide.HeadVisual != null && reference) slide.HeadVisual.ReferenceMotion = true;
                if (slide.Visual != null && slide.RawCustomCode == null)
                    slide.Visual.SlideAppearance = slide.HasSelectableOrbit || slide.StartArea.Length != 0 || slide.segments.Any(s => s.EndArea.Length != 0) ? 2 : 1;
            }
            foreach (var child in note.Children.OfType<Note>()) Mark(child);
        }
        foreach (var note in chart.Notes) Mark(note);
    }
    private void ApplyVisualState()
    {
        // A carrier uses one NMTAP placeholder for every visual family. Keep its
        // authored family/Break/Mine/Each classification even without commands.
        chart.HasVisualCommands |= chart.Notes.Any(n => n is BorrowedNote);
        chart.HasVisualCommands |= chart.Notes.OfType<Slide>().Any(s => s.HasSelectableOrbit);
        // Bake authored-time values before merging streams. Keep classification
        // even in streams without commands, because another stream may use live commands.
        var changes = new List<VisualChange>();
        chart.HasVisualCommands |= chart.Commands.Any(c => c.Kind is "sv" or "hs" || RingState.IsKind(c.Kind));
        foreach (var command in chart.Commands.Where(c => VisualState.IsKind(c.Kind)).ToList())
        {
            chart.HasVisualCommands = true;
            if (!VisualState.TryParse(command.Kind, command.Value, (double)chart.ToSecond(command.Time), out var parsed))
            {
                AddAlert(Warning, "Invalid " + command.Kind.ToUpperInvariant() + " command: " + command.Value);
                chart.Commands.Remove(command);
            }
            else if (!command.Kind.EndsWith('v')) changes.AddRange(parsed);
        }
        changes = changes.OrderBy(c => c.Time).ToList();
        var each = chart.Notes.GroupBy(n => (n.Time, n.FalseEachIdx))
            .Where(g => g.Count(n => n is not Slide s || (!s.NoHead && s.SharedHeadWith == null)) > 1)
            .Select(g => g.Key).ToHashSet();
        VisualNote Build(Note note, string family, bool isBreak, bool isMine)
        {
            var visual = new VisualNote { Family = family, Each = each.Contains((note.Time, note.FalseEachIdx)), Break = isBreak, Mine = isMine, IgnoreSV = note.IgnoreSV };
            var seconds = (double)note.TimeInSecond + note.FalseEachIdx * 1.875 / (double)chart.BpmList.Find(note.Time).Bpm;
            visual.Base = VisualState.Resolve(changes, seconds, visual);
            if (family == "slide") visual.Guide = VisualState.Resolve(changes, seconds, visual, "slidestar");
            return visual;
        }
        foreach (var note in chart.Notes)
        {
            if (note.IgnoreSV) chart.HasVisualCommands = true;
            if (note is Slide slide)
            {
                slide.Visual = Build(note, "slide", slide.IsBreak, slide.IsMine);
                slide.HeadVisual = Build(note, slide.OwnHead != null ? slide.OwnHead is Star ? "star" : "tap" :
                    slide.StartArea.Length == 0 || slide.StartIsDZone ? "star" : "touch",
                    slide.OwnHead?.IsBreak ?? slide.HeadIsBreak, slide.OwnHead?.IsMine ?? slide.HeadIsMine);
                if (slide.OwnHead != null) slide.OwnHead.Visual = slide.HeadVisual;
            }
            else note.Visual = Build(note, note switch { BorrowedNote borrowed => borrowed.Trajectory.Family, TouchHold => "touchhold", Touch => "touch", Hold => "hold", Star => "star", _ => "tap" }, note.IsBreak, note.IsMine);
        }
    }
}
