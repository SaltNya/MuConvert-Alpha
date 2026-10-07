namespace MuConvert.mai;

internal static class SimaiEditorDirectives
{
    internal static string Strip(string text)
    {
        var result = text.ToCharArray();
        var haveNote = false;
        var commands = System.Text.RegularExpressions.Regex.Matches(text, @"<[A-Za-z]+\*[^>\r\n]*>")
            .ToDictionary(m => m.Index, m => m.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (commands.TryGetValue(i, out var commandLength))
            { i += commandLength - 1; continue; }
            if (text[i] == '#' || text[i] == '|' && i + 1 < text.Length && text[i + 1] == '|')
            {
                while (i + 1 < text.Length && text[i + 1] != '\r' && text[i + 1] != '\n') i++;
                continue;
            }
            if (text[i] is '@' or '&' && !haveNote && EditorDirectiveScanner.TryRead(text, i, out var directive))
            {
                if (directive.kind == EditorDirectiveKind.Overlay)
                {
                    // The nested parser preprocesses the overlay itself. Its
                    // commas and note text must not change the main beat state.
                    if (i + 1 < text.Length && text[i + 1] == '*') i += directive.length - 1;
                    else while (i + 1 < text.Length && text[i + 1] != '\r' && text[i + 1] != '\n') i++;
                }
                else
                {
                    // Blank only the characters the primary scanner owns.
                    // Keep source positions and notes after a section tint.
                    for (var j = i; j < i + directive.length; j++)
                        if (result[j] != '\r' && result[j] != '\n') result[j] = ' ';
                    i += directive.length - 1;
                }
                continue;
            }
            if (text[i] is '(' or '{' or '[')
            {
                var open = text[i]; var close = open == '(' ? ')' : open == '{' ? '}' : ']';
                var depth = 1; var end = i + 1;
                for (; end < text.Length && depth != 0; end++)
                { if (text[end] == open) depth++; else if (text[end] == close) depth--; }
                if (depth == 0)
                {
                    if (open != '[') haveNote = false;
                    i = end - 1;
                    continue;
                }
            }
            if (text[i] == ',') haveNote = false;
            else if (!char.IsWhiteSpace(text[i])) haveNote = true;
        }
        return new string(result);
    }
}

public partial class SimaiParser
{
    private bool preserveEditorDirectives; // The primary overlay parser treats these as note text.
}
