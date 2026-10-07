using System.Runtime.CompilerServices;
using MuConvert.Antlr;
using P = MuConvert.Antlr.SimaiParser;

namespace MuConvert.mai;

public partial class SimaiParser
{
    // TouchHold is not a Tap and must not be put into Slide.OwnHead. Keep this
    // parser-only association until same-head paths have been read, then expose
    // the existing separately judged Hold/TouchHold + no-head Slide records.
    private static class HoldSlideHeads
    {
        internal sealed class State(Note head, bool explicitDuration)
        {
            internal readonly Note Head = head;
            internal readonly bool ExplicitDuration = explicitDuration;
        }
        internal static readonly ConditionalWeakTable<Slide, State> States = new();
    }

    public sealed override object VisitHoldSlideHead(P.HoldSlideHeadContext context)
    {
        currContext = context;
        var head = new Hold(chart, now)
        {
            Key = int.Parse((context.KEY()?.GetText() ?? context.D_ZONE().GetText())[..1]),
            IsDZone = context.D_ZONE() != null
        };
        currNote = head;
        ReadNoteSkin(context.noteSkin(), head);
        ApplyModifiers(context.modifiers(), head);
        if (context.duration() != null) head.Duration = (Duration)VisitDuration(context.duration());
        return head;
    }

    public sealed override object VisitTouchHoldSlideHead(P.TouchHoldSlideHeadContext context)
    {
        currContext = context;
        var head = new TouchHold(chart, now) { TouchArea = context.TOUCH_AREA().GetText() };
        currNote = head;
        ReadNoteSkin(context.noteSkin(), head);
        ApplyModifiers(context.modifiers(), head);
        if (context.duration() != null) head.Duration = (Duration)VisitDuration(context.duration());
        return head;
    }

    private void CompleteHoldSlideHead(Slide slide, P.SlideContext context)
    {
        Note? head = slide.OwnHead as Hold;
        var explicitDuration = context.holdSlideHead()?.duration() != null;
        if (context.touchHoldSlideHead() != null)
        {
            // Read the head before the body; its modifiers must stay separate.
            if (!HoldSlideHeads.States.TryGetValue(slide, out var state))
                throw new InvalidOperationException("TouchHold slide head was not parsed.");
            head = state.Head;
            explicitDuration = state.ExplicitDuration;
        }
        if (head == null) return;
        if (explicitDuration) slide.WaitTime.Seconds = head.Duration.Seconds;
        else head.Duration.Seconds = slide.WaitTime.Seconds;
        if (context.touchHoldSlideHead() == null)
            HoldSlideHeads.States.Add(slide, new HoldSlideHeads.State(head, explicitDuration));
    }

    private static void SplitHoldSlideHeads(List<Note> notes)
    {
        // Explicit head duration applies to all paths sharing that authored
        // head. Existing headless chains and implicit h waits stay unchanged.
        for (var i = 0; i < notes.Count; i++)
        {
            if (notes[i] is not Slide slide) continue;
            var root = slide.SharedHeadWithRoot;
            if (HoldSlideHeads.States.TryGetValue(root, out var state) && state.ExplicitDuration)
                slide.WaitTime.Seconds = state.Head.Duration.Seconds;
            if (slide.SharedHeadWith != null || state == null) continue;
            var key = slide.Key;
            if (state.Head is Hold) slide.OwnHead = null;
            slide.Key = key;
            slide.NoHead = true;
            if (slide.IgnoreSV) state.Head.IgnoreSV = true;
            notes.Insert(i++, state.Head);
        }
    }
}
