using Rationals;
using static MuConvert.utils.Alert.LEVEL;

namespace MuConvert.mai;

public partial class SimaiParser
{
    private void ApplyFakeState()
    {
        // Each parser owns one stream. Resolve before merging overlay notes so
        // a main-stream FAKE command cannot leak into an independent stream.
        var changes = new List<(double Seconds, string Type, bool Enabled)>();
        var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "tap", "each", "hold", "slide", "star", "break", "mine", "touch", "touchhold" };
        foreach (var command in chart.Commands.Where(c => c.Kind == "fake"))
        {
            var pending = new List<(double, string, bool)>();
            foreach (var part in command.Value.Split(','))
            {
                var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
                var type = pair.Length == 2 ? pair[0].ToLowerInvariant() : "";
                var value = pair[^1];
                var enabled = value.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || value == "1";
                var disabled = value.Equals("FALSE", StringComparison.OrdinalIgnoreCase) || value == "0";
                if ((!enabled && !disabled) || (type.Length != 0 && !types.Contains(type)))
                {
                    pending.Clear();
                    AddAlert(Warning, "Invalid FAKE command: " + command.Value);
                    break;
                }
                pending.Add(((double)chart.ToSecond(command.Time), type, enabled));
            }
            changes.AddRange(pending);
        }
        if (changes.Count == 0) return;
        changes = changes.OrderBy(c => c.Seconds).ToList(); // Stable source order for ties.
        var each = chart.Notes.GroupBy(n => (n.Time, n.FalseEachIdx))
            .Where(g => g.Count(n => n is not Slide slide || (!slide.NoHead && slide.SharedHeadWith == null)) > 1)
            .Select(g => g.Key).ToHashSet();
        bool? Latest(string type, double seconds)
        {
            for (var i = changes.Count - 1; i >= 0; i--)
                if (changes[i].Type == type && changes[i].Seconds <= seconds + .000001)
                    return changes[i].Enabled;
            return null;
        }
        bool Resolve(Note note, string type, bool isBreak, bool isMine)
        {
            var seconds = (double)note.TimeInSecond + note.FalseEachIdx * 1.875 / (double)chart.BpmList.Find(note.Time).Bpm;
            // Alpha's grammar also exposes the mine target. Honor it in the
            // playable mod before break/each, just as other typed commands do.
            // FALSE is an explicit override, not a reset.
            var typed = isMine ? Latest("mine", seconds) : null;
            typed ??= isBreak ? Latest("break", seconds)
                : each.Contains((note.Time, note.FalseEachIdx)) ? Latest("each", seconds) : null;
            return typed ?? Latest(type, seconds) ?? Latest("", seconds) ?? false;
        }
        foreach (var note in chart.Notes)
        {
            if (note is BorrowedNote) { note.IsFake = true; continue; }
            if (note is Slide slide)
            {
                slide.HeadIsFake = Resolve(note, "star", slide.OwnHead?.IsBreak ?? slide.HeadIsBreak,
                    slide.OwnHead?.IsMine ?? slide.HeadIsMine);
                slide.IsFake = Resolve(note, "slide", slide.IsBreak, slide.IsMine);
                if (slide.OwnHead != null) slide.OwnHead.IsFake = slide.HeadIsFake;
            }
            else
            {
                var type = note switch { TouchHold => "touchhold", Touch => "touch", Hold => "hold", Star => "star", _ => "tap" };
                note.IsFake = Resolve(note, type, note.IsBreak, note.IsMine);
            }
        }
    }
}
