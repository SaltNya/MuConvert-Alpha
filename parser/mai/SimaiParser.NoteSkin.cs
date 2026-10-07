using AquaMai.ChartVisuals;
using P = MuConvert.Antlr.SimaiParser;

namespace MuConvert.mai;

public partial class SimaiParser
{
    private static void ReadNoteSkin(P.NoteSkinContext? context, Note note)
    {
        if (context == null) return;
        var text = context.GetText();
        var body = text.Substring(2, text.Length - 3);
        if (AquaMai.Alpha053.Core.SlidePathParser.TryReadTrajectoryBorrow(text, 0, out _, out _)) return;
        if (!NoteSkin.LooksLikePath(body) || !NoteSkin.Normalize(body, out var path))
            throw new ArgumentException("音符皮肤须为谱面目录中的相对 png/jpg/jpeg 路径，如 ~[skins\\star.png]。");
        note.Skin = path;
    }
}
