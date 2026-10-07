namespace MuConvert.mai;

public partial class MA2Generator
{
    private static bool UsesAdjacentStraightGeometry(Slide slide)
        => slide.RawCustomCode == null && !slide.HasSelectableOrbit && slide.StartArea == "" && !slide.StartIsDZone &&
            slide.segments.All(s => s.EndArea == "" && !s.EndIsDZone) &&
            !slide.segments.Any(s => s.RawShape is "rp" or "rq") &&
            slide.segments.Any(s => s.RawShape == "-" && (s.EndKey - s.StartKey + 8) % 8 is 1 or 7);
}
