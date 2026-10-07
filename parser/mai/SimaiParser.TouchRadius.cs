using AquaMai.ChartVisuals;
using P = MuConvert.Antlr.SimaiParser;

namespace MuConvert.mai;

public partial class SimaiParser
{
    private float ReadTouchRadius(P.RadiusOverrideContext? context, Touch note)
    {
        if (context == null) return 0;
        var text = context.GetText();
        var body = text.Substring(2, text.Length - 3);
        // Alpha resolves a '-' inside ~[] as a borrowed trajectory before
        // trying a number. Negative exponents therefore are not radius syntax.
        if (!TouchRadius.IsArea(note.TouchArea) ||
            body.IndexOf('-') >= 0 || !TouchRadius.TryParse(body, out var radius))
            throw new ArgumentException("Touch 半径须写成 A/B/D/E 区的 ~[数字]，取值大于 0 且不超过 10。");
        return radius;
    }
}
