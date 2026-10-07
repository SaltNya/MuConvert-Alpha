using System.Globalization;
using System.Text.RegularExpressions;
using Antlr4.Runtime;
using Antlr4.Runtime.Misc;
using Antlr4.Runtime.Tree;
using MuConvert.Antlr;
using MuConvert.chart;
using MuConvert.parser;
using MuConvert.utils;
using Rationals;
using static MuConvert.utils.Alert.LEVEL;
using P = MuConvert.Antlr.SimaiParser;
using L = MuConvert.Antlr.SimaiLexer;
using Utils = MuConvert.utils.Utils;

namespace MuConvert.mai;

public partial class SimaiParser : SimaiBaseVisitor<object>, IParser<MaiChart>
{
    /**
     * 表示parser工作的严格程度的枚举。默认为Normal。
     * 
     * Strict: 不允许任何语法错误，任何语法错误直接报错。
     * Normal: 允许进行一些小的修复，如删除确定多出的符号、补充确定残缺的符号等。但遇到大的错误会直接解析失败。
     * Lax: 尽全力解析，出现错误的音符直接吞掉不解析，以换取整个谱面不要解析失败。
     */
    public enum StrictLevelEnum { Strict, Normal, Lax }
    public StrictLevelEnum StrictLevel;

    internal readonly MaiChart chart;
    internal readonly List<Alert> alerts = [];

    private Rational now = 0;
    private Rational step = new(1, 4);
    private decimal? absoluteTimeStep; // 此项必须和step本体一起更改
    private Rational extendedFalseEach = 0; // 扩展伪双押语法（多个连续的`）累计后移了多少时间。每次遇到逗号时，这个数字需要清零。

    private bool isIndependentStream; // Leading backticks are empty groups only inside an independent stream.
    private int streamSeq;
    private readonly List<(string Text, Rational Time, decimal Bpm, int Line)> overlayStreams = [];

    private ParserRuleContext? currContext; // 供调试报错AddAlert函数使用
    private Note? currNote; // 用于在部分visitor之间传递额外的参数，如visitDuration、visitSlideBody等，都需要Note对象作为参数传入的情况
    private bool isRealExactWaitTime; // 用于在VisitSlideBody和VisitSlideDuration之间传递额外的参数
    private readonly List<IToken> extraModifiers = []; // 通过ApplyModifiers不能通用处理的modifiers，需要落回到具体的音符处理逻辑中进行处理的。
    
    private bool absoluteTimeStepWarned; // 用于确保Warning只打印一次
    private bool extendedFalseEachWarned;

    public SimaiParser(bool bigTouch = false, int clockCount = 4, StrictLevelEnum strictLevel = StrictLevelEnum.Normal)
    {
        chart = new MaiChart { DefaultTouchSize = bigTouch ? "L1" : "M1", ClockCount = clockCount};
        StrictLevel = strictLevel;
    }
    
    private void AddAlert(Alert.LEVEL level, string content, ParserRuleContext? context = null)
    {
        var alert = new Alert(level, content, barTime: (chart, now));
        context ??= currContext;
        if (context != null)
        {
            alert.Line = context.Start.Line;
            alert.RelevantNote = context.GetText();
        }
        alerts.Add(alert);
    }
    
    [GeneratedRegex(@"(?<!\[[^\]]*|\{[^\}]*)#.*$", RegexOptions.Multiline)]
    private static partial Regex InlineSharpCommentRegex(); // 这里仅处理#开头的注释，因为||开头的注释在语法文件里已经处理过了。

    private static string StripBlockComments(string text)
    {
        var result = text.ToCharArray();
        var inComment = false;
        for (var i = 0; i < result.Length; i++)
        {
            if (!inComment && i + 1 < result.Length && result[i] == '|' && result[i + 1] == '*')
            {
                inComment = true;
                result[i] = result[i + 1] = ' ';
                i++;
                continue;
            }
            if (inComment && i + 1 < result.Length && result[i] == '*' && result[i + 1] == '|')
            {
                result[i] = result[i + 1] = ' ';
                inComment = false;
                i++;
                continue;
            }
            if (inComment && result[i] != '\r' && result[i] != '\n')
                result[i] = ' ';
        }
        return new string(result);
    }

    private string Preprocess(string text)
    {
        text = StripBlockComments(text);
        if (!preserveEditorDirectives) text = SimaiEditorDirectives.Strip(text);
        // The reference consumes an alpha command before looking for inline
        // comments. A # in quoted TEXT (or a HTML color) belongs to its body.
        var commands = Regex.Matches(text, @"<[A-Za-z]+\*[^>\r\n]*>");
        var protectedCommand = new bool[text.Length];
        foreach (Match command in commands)
            for (var i = command.Index; i < command.Index + command.Length; i++) protectedCommand[i] = true;
        var result = text.ToCharArray();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '#' || protectedCommand[i]) continue;
            var comment = InlineSharpCommentRegex().Match(text, i);
            if (!comment.Success || comment.Index != i) continue;
            for (var j = i; j < i + comment.Length; j++) if (result[j] != '\r' && result[j] != '\n') result[j] = ' ';
            i += comment.Length - 1;
        }
        // Majdata ignores an empty first simultaneous-note group: ,/4, is
        // the same slot as ,4,. Keep command text and all comma timing intact.
        var previous = ',';
        for (var i = 0; i < result.Length; i++)
        {
            if (protectedCommand[i]) { previous = result[i]; continue; }
            if (char.IsWhiteSpace(result[i])) continue;
            if (result[i] == '/' && previous == ',') { result[i] = ' '; continue; }
            previous = result[i];
        }
        return new string(result);
    }

    /**
     * 对词法分析得到的token流，在送入parser之前进行一些处理，以尝试修复一些特定类型的错误：
     * - 对星星头的修饰符，应该出现在键位号后、星星类型标记之前
     */
    private CommonTokenStream TokenProcess(CommonTokenStream src)
    {
        List<Alert> alertsBuf = [];
        
        src.Fill();
        var tokens = src.GetTokens().Index().Where(x=>x.Item.Channel == TokenConstants.DefaultChannel).ToList();
        var r = new TokenStreamRewriter(src);
        bool modified = false;
        for (int i = 0; i < tokens.Count - 1; i++)
        {
            var (idx, token) = tokens[i];
            if (token.Type == L.SLIDE_TYPE && 
                (i < tokens.Count - 1 && Utils.IsModifier(tokens[i+1].Item.Type)) && // SlideType后面接了modifier
                (i >= 2 && tokens[i-1].Item.Type == L.KEY && tokens[i-2].Item.Type != L.SLIDE_TYPE)) // 判断是否是星星的首个slidetype
            { // 类似1-b2[2:1]这种，星星头的修饰符错误地出现在了首个slidetype后面的情况。
                // 找到modifier的结束位置
                int endPos = i+1;
                while (Utils.IsModifier(tokens[endPos + 1].Item.Type)) endPos++;
                // 将tokens[i]挪到endPos后面去
                r.Delete(idx);
                r.InsertAfter(tokens[endPos].Index, token.Text);
                var modifiersText = src.GetText(tokens[i].Item, tokens[endPos].Item);
                alertsBuf.Add(new Alert(Warning, 
                    string.Format(Locale.FixModifiersOnHead, modifiersText) + Locale.Fixed, 
                    line: token.Line, relevantNote: src.GetText(tokens[i-1].Item, tokens[endPos + 1].Item)));
                modified = true;
            }
        }

        if (!modified) return src;  
        // 做过更改，则要重跑lexer
        alerts.Clear(); // 清空上次跑lexer时的报错，避免重复报错
        alerts.AddRange(alertsBuf);
        var inputStream = new AntlrInputStream(r.GetText());
        var lexer = new SimaiLexer(inputStream);
        lexer.RemoveErrorListeners();
        lexer.AddErrorListener(new ErrorListener(this));
        return new CommonTokenStream(lexer);
    }

    public (MaiChart, List<Alert>) Parse(string text)
    {
        if (now != 0) throw new Exception(Locale.InstanceMultipleUsage);
        P.ChartContext root;
        
        text = Preprocess(text); // 预处理
        
        try
        { // 词语法分析
            var inputStream = new AntlrInputStream(text);
            var lexer = new SimaiLexer(inputStream);
            lexer.RemoveErrorListeners();
            lexer.AddErrorListener(new ErrorListener(this));
            var tokens = new CommonTokenStream(lexer);
            if (StrictLevel != StrictLevelEnum.Strict) tokens = TokenProcess(tokens);
            
            var parser = new P(tokens) { ErrorHandler = ErrorStrategy() }; // MuConvert.Antlr.SimaiParser
            parser.RemoveErrorListeners();
            parser.AddErrorListener(new ErrorListener(this));
            root = parser.chart();
            if (root.GetText() == "<EOF>")
            { // 只有一个EOF
                alerts.Add(new Alert(Error, Locale.NoNotesInChart)); 
                throw new ConversionException(alerts);
            }
        }
        catch (ParseCanceledException e)
        { // ErrorListener里会把alerts加好的，因此这里直接抛异常就可以了。
            throw new ConversionException(alerts, e);
        }
        
        try
        { // 基于语法分析树，进行具体的解析和遍历
            VisitChart(root);
        }
        catch (ConversionException)
        {
            throw; // 看到主动丢出的ConversionException，就说明错误信息已经被加到message中过了。直接丢回去即可。
        }
        catch (Exception e)
        {
            // 否则，说明是意外的Exception，把它附加上详细信息、转换为一般的Exception。
            AddAlert(Error, e.Message);
            throw new ConversionException(alerts, e);
        }
        
        chart.Sort();
        return (chart, alerts);
    }

    private IAntlrErrorStrategy ErrorStrategy()
    {
        switch (StrictLevel)
        {
            case StrictLevelEnum.Strict:
                return new FixedBailErrorStrategy();
            case StrictLevelEnum.Lax:
                return new LaxErrorStrategy(this);
            case StrictLevelEnum.Normal:
            default:
                return new ModerateErrorStrategy(this);
        }
    }

    public sealed override object VisitChart(P.ChartContext context)
    {
        foreach (var notations in context.notations())
        {
            VisitNotations(notations);
            if (chart.BpmList.Count == 0) AddDefaultBpm();
            if (extendedFalseEach > 0)
            {
                now -= extendedFalseEach;
                extendedFalseEach = 0;
            }
            now = (now + step).CanonicalForm;
        }
        ApplyFakeState();
        ApplyVisualState();
        MergeOverlayStreams();
        MarkReferencePresentation();
        return true;
    }

    private void AddDefaultBpm()
    {
        // 谱面开头还没看到BPM，就看到音符（或绝对时间标记）了。这在MA2中是不允许的，MA2的BPM必须是从一开头就开始指定。
        // 因此，我们打印一个警告，然后帮用户补一个60。
        const int defaultStartBpm = 60;
        AddAlert(Warning, string.Format(Locale.StartNoBpm, defaultStartBpm));
        Utils.Assert(now == 0, "现在已经不是开头了？？");
        chart.BpmList.Add(new BPM(now, defaultStartBpm));
    }

    private static bool SubtreeHasException(ParserRuleContext root)
    {
        if (root.exception != null) return true;
        foreach (var child in root.children ?? Array.Empty<IParseTree>())
        {
            if (child is ParserRuleContext pr && SubtreeHasException(pr)) return true;
        }
        return false;
    }

    public sealed override object VisitNotations(P.NotationsContext context)
    { // 形如 (120){4}1/1 算作一组notations
        // Display commands consume no chart time. A BPM declared after those
        // commands in this same comma group also governs their beat lengths
        // and the chart's first offset. Do not inject 60 before reaching it.
        P.BpmTagContext? initialBpm = null;
        if (chart.BpmList.Count == 0)
        {
            foreach (var child in context.children ?? [])
            {
                if (child is P.NoteGroupContext or P.AbsulouteStepTagContext) break;
                if (child is not P.BpmTagContext bpm || SubtreeHasException(bpm)) continue;
                initialBpm = bpm;
                VisitBpmTag(bpm);
                break;
            }
        }
        foreach (var child in context.children ?? [])
        {
            if (child is IErrorNode) continue; // 忽略错误节点
            if (child is P.BpmTagContext bpmTag)
            {
                if (bpmTag != initialBpm) VisitBpmTag(bpmTag);
            }
            else if (child is P.MetTagContext metTag)
            {
                VisitMetTag(metTag);
            }
            else if (child is P.CommandTagContext commandTag)
            {
                VisitCommandTag(commandTag);
            }
            else if (child is P.OverlayStreamContext overlayStream)
            {
                VisitOverlayStream(overlayStream);
            }
            else if (child is P.WaveTimeSigContext waveTimeSig)
            {
                VisitWaveTimeSig(waveTimeSig);
            }
            else
            {
                if (chart.BpmList.Count == 0) AddDefaultBpm();
                if (child is P.AbsulouteStepTagContext absoluteStepTag)
                {
                    VisitAbsulouteStepTag(absoluteStepTag);
                }
                else if (child is P.NoteGroupContext noteGroup)
                {
                    VisitNoteGroup(noteGroup);
                }
            }
        }
        return true;
    }

    private void AlertExtraToken(string extraStr)
    {
        if (extraStr.First() != '\'') extraStr = "'" + extraStr + "'";
        if (StrictLevel == StrictLevelEnum.Strict)
        { // 严格模式，抛异常
            AddAlert(Error, string.Format(Locale.RecoverInlineExtraneousTokenStrict, extraStr));
            throw new ConversionException(alerts);
        }
        else AddAlert(Warning, string.Format(Locale.RecoverInlineExtraneousToken, extraStr));
    }

    private void AlertIfMoreThanOneTokens(IList<IToken> ps)
    {
        if (ps.Count <= 1) return;
        var extraStr = string.Join("", ps.Skip(1).Select(x => x.Text));
        AlertExtraToken(extraStr);
    }
    
    private void AlertIfMoreParentheses(IList<IToken> lp, IList<IToken> rp)
    {
        AlertIfMoreThanOneTokens(lp);
        AlertIfMoreThanOneTokens(rp);
    }

    public sealed override object VisitAbsulouteStepTag(P.AbsulouteStepTagContext context)
    {
        if (SubtreeHasException(context)) return false; // 如果本节点下有异常，则直接整个吞掉，（避免具体的规则遇到不完整子树、爆出更不可预测的错误）
        if (!absoluteTimeStepWarned)
        {
            AddAlert(Warning, string.Format(Locale.AbsoluteStepUsed, context.GetText()), context);
            absoluteTimeStepWarned = true;
        }
        currContext = context;
        AlertIfMoreParentheses(context._lp, context._rp);
        absoluteTimeStep = (decimal)VisitNumber(context.number());
        var currentBpm = chart.BpmList.Last().Bpm;
        step = (Rational)absoluteTimeStep / (240 / (Rational)currentBpm);
        return true;
    }

    public sealed override object VisitBpmTag(P.BpmTagContext context)
    {
        if (SubtreeHasException(context)) return false; // 如果本节点下有异常，则直接整个吞掉，（避免具体的规则遇到不完整子树、爆出更不可预测的错误）
        currContext = context;
        AlertIfMoreParentheses(context._lp, context._rp);
        var bpm = (decimal)VisitNumber(context.number());
        chart.BpmList.Add(new BPM(now, bpm));
        if (absoluteTimeStep != null)
        { // 如果当前处于绝对时间step模式下，则bpm变化也会引起小节制step的变化，更新之。
            step = (Rational)absoluteTimeStep / (240 / (Rational)bpm);
        }
        return true;
    }

    public sealed override object VisitMetTag(P.MetTagContext context)
    { // metTag指的是标记分音的tag，如{4}
        if (SubtreeHasException(context)) return false; // 如果本节点下有异常，则直接整个吞掉，（避免具体的规则遇到不完整子树、爆出更不可预测的错误）
        currContext = context;
        AlertIfMoreParentheses(context._lp, context._rp);
        var quaver = int.Parse(context.@int().GetText());
        step = new Rational(1, quaver);
        absoluteTimeStep = null;
        return true;
    }

    public sealed override object VisitOverlayStream(P.OverlayStreamContext context)
    {
        if (SubtreeHasException(context)) return false;
        if (chart.BpmList.Count == 0) AddDefaultBpm();
        var text = context.OVERLAY_STREAM().GetText();
        var content = text.StartsWith("@*") ? text[2..^2] : text[1..];
        if (!Regex.IsMatch(content, @"^\s*\{[1-9][0-9]*\}"))
            throw new ArgumentException("Independent stream must begin with a positive {division}.");
        overlayStreams.Add((content, now, chart.BpmList.Find(now).Bpm, context.Start.Line));
        return true;
    }

    public sealed override object VisitWaveTimeSig(P.WaveTimeSigContext context)
    { // 波形拍号（@分子/分母）：仅影响编辑器波形强弱拍网格，对ma2输出无影响，忽略。
        if (SubtreeHasException(context)) return false;
        currContext = context;
        return true;
    }

    /**
     * 时间轴命令（AquaMai mod）：<SV*2> <SV*tap=2,hold=0.75> <HS*1.2> <BOUNCE*8:1> <SPAWN*1.225> 等。
     * 命令位于当前时刻，不消耗step。
     */
    public sealed override object VisitCommandTag(P.CommandTagContext context)
    {
        if (SubtreeHasException(context)) return false;
        currContext = context;
        var text = context.COMMAND().GetText(); // "<SV*2>"
        var inner = text[1..^1];
        var star = inner.IndexOf('*');
        if (star <= 0) return true;
        var kind = inner[..star].ToLowerInvariant();
        var value = inner[(star + 1)..].Trim();
        if (AquaMai.ChartVisuals.MediaCommands.IsKind(kind))
        {
            if (chart.BpmList.Count == 0) AddDefaultBpm();
            if (AquaMai.ChartVisuals.MediaCommands.TryParse(kind, value, (float)chart.BpmList.Find(now).Bpm, out var media))
                chart.Commands.Add((now, kind, media.Encode()));
            else AddAlert(Warning, "Invalid " + kind.ToUpperInvariant() + " form: " + value);
            return true;
        }
        if (kind == "text")
        {
            if (chart.BpmList.Count == 0) AddDefaultBpm();
            if (AquaMai.ChartVisuals.SubtitleCommands.TryParse(value, (float)chart.BpmList.Find(now).Bpm, out var subtitle))
                chart.Commands.Add((now, kind, subtitle.Encode()));
            else AddAlert(Warning, "Invalid TEXT form: " + value);
            return true;
        }
        // Cabinet play has no editor side panels or outer display area.
        if (kind is "showjudgeinfo" or "showcomboinfo" or "outerbrightness") return true;
        if (AquaMai.ChartVisuals.PresentationCommands.IsKind(kind))
        {
            if (chart.BpmList.Count == 0) AddDefaultBpm();
            if (AquaMai.ChartVisuals.PresentationCommands.TryParse(kind, value, (float)chart.BpmList.Find(now).Bpm, out var presentation))
                chart.Commands.Add((now, presentation.Kind, presentation.Encode()));
            else AddAlert(Warning, "Invalid or unsupported " + kind.ToUpperInvariant() + " form: " + value);
            return true;
        }
        if (AquaMai.ChartVisuals.RingState.IsKind(kind) &&
            !AquaMai.ChartVisuals.RingState.TryParse(kind, value, 0, out _))
        {
            AddAlert(Warning, "Invalid " + kind.ToUpperInvariant() + " command: " + value);
            return true;
        }
        if (kind is "sv" or "hs" or "bounce" or "fake" || AquaMai.ChartVisuals.RingState.IsKind(kind) || AquaMai.ChartVisuals.VisualState.IsKind(kind))
            chart.Commands.Add((now, kind, value));
        else AddAlert(Warning, "Unsupported command was not converted: " + text);
        return true;
    }

    /// <summary>解析touch区文本（B1/A3/C/C1等）：返回 (区域, 键位号)。键位号1-indexed，C区为0。
    /// A 区位置就是按键本身（AquaMai 约定：6>A3 按 6>3 处理，转原版slide），返回 ("", key)。</summary>
    private static (string, int) ParseTouchArea(string text)
    {
        // D 区位置（Majdata 新版语法 "7d"，= 与环键 7 同编号的 D 区点）：对应 AquaMai code 的 'D'+数字，
        // 生成侧与 B/E 区同样处理（EndArea="D"）。
        if (text.Length == 2 && text[1] == 'd' && text[0] >= '1' && text[0] <= '8')
            return ("D", text[0] - '0');
        if (text.Length >= 2 && int.TryParse(text[1..], out var key) && key >= 1 && key <= 8)
            return (text[..1], key); // A 区=相邻环键中间的触摸区（游戏端 F 命令），与 B/D/E 一样保留区名
        return (text, 0); // C
    }

    public sealed override object VisitNoteGroup(P.NoteGroupContext context)
    { // 同一时刻出现的（双押，伪双押，同头星星...）构成一个NoteGroup。例如"1/2`3/4"，`1-2*-3[2:1]/4-5*-6[4:1]`都是NoteGroup。
        currContext = context;
        int falseEachIdx = isIndependentStream ? context.FALSE_EACH()?.GetText().Length ?? 0 : 0;
        foreach (var child in context.children)
        {
            if (child is IErrorNode) continue; // 忽略错误节点
            if (child is ITerminalNode leading && leading.Symbol.Type == L.FALSE_EACH) continue;
            P.NoteContext noteC;
            if (child is P.NoteContext c1) noteC = c1;
            else if (child is P.EachNoteContext c2)
            {
                noteC = c2.note();
                if (noteC == null)
                {
                    // Alpha allows an empty fake-each group, e.g. 6`/B3/B4.
                    // Its separator still advances the following group.
                    if (c2.sep.Type == L.FALSE_EACH)
                    {
                        falseEachIdx += c2.sep.Text.Length;
                        continue;
                    }
                    AlertExtraToken(c2.sep.Text);
                    continue;
                }
                if (c2.sep.Type == L.FALSE_EACH)
                {
                    if (c2.sep.Text.Length >= 2)
                    {
                        // 出现连续多个反引号的情况，如"2``3"。
                        // 这并不是标准的simai语法。但是，MajdataView中对此提供了支持，将每个`实现为128分音。
                        // 因此，我们也支持这一特性，在遇到大于一个`时，不实现成FalseEachIndex，而是直接给予相同的实现、每个`错后128分音。
                        var length = c2.sep.Text.Length * new Rational(1, 128);
                        now = (now + length).CanonicalForm;
                        extendedFalseEach += length;
                        falseEachIdx = 0;
                        if (!extendedFalseEachWarned)
                        {
                            AddAlert(Warning, Locale.ExtenedFalseEach, context);
                            extendedFalseEachWarned = true;
                        }
                    }
                    else falseEachIdx++; // 普通的伪双押
                }
                // else 是普通双押符号'/'。无需做任何特殊处理，正常解析noteContext就好。
            }
            else throw Utils.Fail();

            var result = (List<Note>)VisitNote(noteC);
            foreach (var note in result)
            {
                note.FalseEachIdx = falseEachIdx;
                chart.Notes.Add(note);
            }
        }
        return true;
    }

    public sealed override object VisitNote(P.NoteContext context)
    { 
        // 注：这个函数返回的是List<Note>，因为ANTLR中的NoteContext，虽然大多数时候只对应一个Note，但有时也可能是两个以上！
        // 具体而言，两种情况：1. "1234"这种simai允许的tap多押简略记法（等价于"1/2/3/4"）
        // 2. 同头星星如"1-2[2:1]*-3[2:1]"，它在我们定义的ANTLR语法中是作为一个note节点的！
        if (SubtreeHasException(context)) return new List<Note>(); // 如果本节点下有异常，则直接整个吞掉，（避免具体的规则遇到不完整子树、爆出更不可预测的错误）
        currContext = context;
        List<Note> result = [];
        foreach (var child in context.children)
        {
            if (child is IErrorNode) continue;
            Note note;
            switch (child)
            {
                case P.NoiseZoneContext noiseC:
                    VisitNoiseZone(noiseC);
                    continue;
                case P.BorrowedNoteContext borrowedC:
                    note = (BorrowedNote)VisitBorrowedNote(borrowedC);
                    break;
                case P.TapContext tapC:
                    note = (Tap)VisitTap(tapC);
                    break;
                case P.HoldContext holdC:
                    note = (Hold)VisitHold(holdC);
                    break;
                case P.TapHoldContext tapHoldC:
                    note = (Hold)VisitTapHold(tapHoldC);
                    break;
                case P.TouchContext touchC:
                    note = (Touch)VisitTouch(touchC);
                    break;
                case P.TouchStarContext touchStarC:
                    note = (TouchStar)VisitTouchStar(touchStarC);
                    break;
                case P.TouchHoldContext touchHoldC:
                    note = (TouchHold)VisitTouchHold(touchHoldC);
                    break;
                case P.SlideContext slideC:
                    note = (Slide)VisitSlide(slideC);
                    break;
                case P.SlideCodeNoteContext slideCodeC:
                    note = (Slide)VisitSlideCodeNote(slideCodeC);
                    break;
                case P.SharedHeadSlideContext shSlideC:
                    note = (Slide)VisitSharedHeadSlide(shSlideC);
                    break;
                case ITerminalNode n when n.Symbol.Type == L.KEY:
                    note = new Tap(chart, now) { Key = int.Parse(n.GetText())};
                    break;
                default:
                    throw Utils.Fail();
            }
            // A bare SC route has no nested duration and arrives through the
            // skin-shaped token; the reference distinguishes its body here.
            if (note is not BorrowedNote && AquaMai.Alpha053.Core.SlidePathParser.TryTakeTrajectoryBorrow(child.GetText(), out _, out _))
                note = BuildBorrowedNote(child.GetText());
            result.Add(note);

            if (extraModifiers.Count > 0)
            {
                AddAlert(Warning, string.Format(Locale.ExtraModifiersIgnored, string.Join("", extraModifiers.Select(x=>x.Text))), (ParserRuleContext)child);
            }
        }
        // MajSimai scans the entire note expression before splitting shared
        // paths: c on either the head or a body disables SV for all its parts.
        // Inspect modifier tokens only so a skin filename containing c is inert.
        bool HasSVOptOut(IParseTree tree) => tree is ITerminalNode terminal
            ? terminal.Symbol.Type == L.MODIFIER && terminal.GetText() == "c"
            : Enumerable.Range(0, tree.ChildCount).Any(i => HasSVOptOut(tree.GetChild(i)));
        if (HasSVOptOut(context))
            foreach (var note in result)
            {
                note.IgnoreSV = true;
                if (note is Slide { OwnHead: not null } slide) slide.OwnHead.IgnoreSV = true;
            }
        SplitHoldSlideHeads(result);
        return result;
    }

    public sealed override object VisitNumber(P.NumberContext context)
    {
        return decimal.Parse(context.GetText(), CultureInfo.InvariantCulture);
    }

    private void ApplyModifiers(P.ModifiersContext[] modifiersList, Note note, bool clearExtraArr = true)
    { // 提取可能在不同位置出现的所有modifiers
      // 将通用的modifier(即b和x)应用到note上，其余的modifier则通过extraModifiers数组返回。
        if (clearExtraArr) extraModifiers.Clear();
        foreach (var modifiers in modifiersList)
        {
            foreach (var child in modifiers.children ?? [])
            {
                if (child is IErrorNode) continue;
                if (child is not ITerminalNode modifier) throw Utils.Fail("modifiers里面居然不是ITerminalNode");
                var token = modifier.Symbol;
                if (token.Text == "m" && !note.IsMine) note.IsMine = true;
                else if (token.Text == "b" && !note.IsBreak) note.IsBreak = true;
                else if (token.Text == "x" && !note.IsEx) note.IsEx = true;
                else if (token.Text == "c") note.IgnoreSV = true;
                else if (token.Text == "f" && note is Touch { IsFirework: false } touch) touch.IsFirework = true;
                else if (token.Text == "f" && note is Tap { IsFirework: false } tap) tap.IsFirework = true;
                else if (token.Text == "f" && note is Slide slide)
                {
                    if (modifiers.Parent is P.SlideBodyContext)
                    {
                        AddAlert(Error, "Firework f must be written on the slide head.");
                        throw new ConversionException(alerts);
                    }
                    // Legacy SC keeps its existing whole-note modifier site;
                    // Firework belongs to its single head, never moving body.
                    var root = slide.SharedHeadWithRoot;
                    if (root.OwnHead != null) root.OwnHead.IsFirework = true;
                    else root.HeadIsFirework = true;
                }
                else extraModifiers.Add(token);
            }
        }
    }

    private bool GetModifier(int tokenType, out string text)
    {
        text = "";
        var idx = extraModifiers.FindIndex(x => x.Type == tokenType);
        if (idx == -1) return false;
        text = extraModifiers[idx].Text;
        extraModifiers.RemoveAt(idx);
        return true;
    }
    
    private bool GetModifier(int tokenType) => GetModifier(tokenType, out _);

    public sealed override object VisitTap(P.TapContext context)
    {
        currContext = context;
        var result = new Tap(chart, now)
        {
            Key = int.Parse((context.KEY()?.GetText() ?? context.D_ZONE().GetText()).Substring(0, 1)),
            IsDZone = context.D_ZONE() != null
        };
        ReadNoteSkin(context.noteSkin(), result);
        ApplyModifiers([context.modifiers()], result);
        var stars = 0;
        while (GetModifier(L.TAP_TO_STAR, out var text)) stars += text.Length;
        if (stars > 2) { AddAlert(Error, "A star accepts at most two $ modifiers."); throw new ConversionException(alerts); }
        if (stars > 0 && context.Parent is not P.SlideContext)
            result = new Star(result) { IsForcedStar = true, IsFakeRotate = stars == 2 };
        return result;
    }

    public sealed override object VisitTouch(P.TouchContext context)
    {
        currContext = context;
        var result = new Touch(chart, now)
        {
            TouchArea = context.TOUCH_AREA().GetText()
        };
        result.CustomRadius = ReadTouchRadius(context.radiusOverride(), result);
        ReadNoteSkin(context.noteSkin(), result);
        ApplyModifiers([context.modifiers()], result);
        return result;
    }

    public sealed override object VisitTouchStar(P.TouchStarContext context)
    {
        // B4$ 简写 touchstar：只有星头没有轨迹；$ 后的修饰符 m=地雷 / b=绝赞 由 ApplyModifiers 消费。
        currContext = context;
        var result = new TouchStar(chart, now)
        {
            TouchArea = context.TOUCH_AREA().GetText()
        };
        result.CustomRadius = ReadTouchRadius(context.radiusOverride(), result);
        ReadNoteSkin(context.noteSkin(), result);
        ApplyModifiers([context.modifiers()], result);
        return result;
    }

    public sealed override object VisitDuration(P.DurationContext? context)
    {
        var result = new Duration(currNote!);
        if (context == null)
        { // context为null，说明hold上没有写持续时间标记。根据文档，这属于“疑似each”，持续时间定义为0。
            result.InvariantBar = 0;
            return result;
        }
        AlertIfMoreParentheses(context._lp, context._rp);
        if (context.beats() != null) result.InvariantBar = (Rational)VisitBeats(context.beats());
        else result.Seconds = (Rational)(decimal)VisitNumber(context.number());        
        return result;
    }

    public sealed override object VisitBeats(P.BeatsContext context)
    {
        return new Rational(int.Parse(context.@int(1).GetText()), int.Parse(context.@int(0).GetText()));
    }

    public sealed override object VisitHold(P.HoldContext context)
    {
        currContext = context;
        var result = new Hold(chart, now)
        {
            Key = int.Parse((context.KEY()?.GetText() ?? context.D_ZONE().GetText()).Substring(0, 1)),
            IsDZone = context.D_ZONE() != null
        };
        currNote = result;
        var duration = (Duration)VisitDuration(context.duration());
        result.Duration = duration;

        ReadNoteSkin(context.noteSkin(), result);
        ApplyModifiers(context.modifiers(), result);
        return result;
    }

    public sealed override object VisitTapHold(P.TapHoldContext context)
    {
        // 7[4:2] 省略 h 的 hold 简写：与 VisitHold 等价，modifiers 可出现在 duration 前后
        currContext = context;
        var result = new Hold(chart, now)
        {
            Key = int.Parse((context.KEY()?.GetText() ?? context.D_ZONE().GetText()).Substring(0, 1)),
            IsDZone = context.D_ZONE() != null
        };
        currNote = result;
        var duration = (Duration)VisitDuration(context.duration());
        result.Duration = duration;
        ApplyModifiers(context.modifiers(), result);
        return result;
    }
    
    public sealed override object VisitTouchHold(P.TouchHoldContext context)
    {
        currContext = context;
        var result = new TouchHold(chart, now)
        {
            TouchArea = context.TOUCH_AREA().GetText()
        };
        currNote = result;
        var duration = (Duration)VisitDuration(context.duration());
        result.Duration = duration;
        
        ReadNoteSkin(context.noteSkin(), result);
        ApplyModifiers(context.modifiers(), result);
        return result;
    }
    
    public sealed override object VisitSlideDuration(P.SlideDurationContext context)
    {
        var result = new Duration(currNote!);
        Duration? waitTime = null;
        isRealExactWaitTime = false; // 是否通过##，指定了绝对的等待时间值
        Rational? anotherBpm = null; // 是否通过类似 160#8:3，指定了显式的BPM，且和当前的实际BPM不同 
        AlertIfMoreParentheses(context._lp, context._rp);

        // 解析绝对的等待时间
        if (context.waitTime() != null)
        {
            waitTime = new Duration(currNote!)
            {
                Seconds = (Rational)(decimal)VisitNumber(context.waitTime().number())
            };
            isRealExactWaitTime = true;
        }
        
        // 解析显式的BPM
        if (context.asBpm() != null)
        {
            var currentBpm = chart.BpmList.Last().Bpm;
            var bpm = (decimal)VisitNumber(context.asBpm().number());
            if (bpm != currentBpm)
            {
                anotherBpm = (Rational)bpm;
                if (waitTime == null) // 如果未显式指定绝对的waitTime秒数，则waitTime也要变成该强行指定的bpm下的一拍。不然默认就是currentBpm下的一拍了。
                    waitTime = new Duration(currNote!) { Seconds = 60 / anotherBpm.Value };
            }
        }
        
        if (context.number() != null) result.Seconds = (Rational)(decimal)VisitNumber(context.number());
        else
        {
            var value = (Rational)VisitBeats(context.beats());
            if (anotherBpm == null) result.InvariantBar = value;
            else result.Seconds = value * (240 / anotherBpm.Value); // 显式指定了bpm的情况，需要根据强行指定的bpm换算为秒数
        }
        return (waitTime, result);
    }

    public sealed override object VisitSlideBody(P.SlideBodyContext context)
    {
        var slide = (Slide)currNote!;
        
        Utils.Assert(context.slideType().Length == context.KEY().Length + context.TOUCH_AREA().Length + context.D_ZONE().Length);
        // 依次取本段终点：可能是按键（KEY）、touch区（TOUCH_AREA）或 D 区位置（D_ZONE，Majdata 的 "7d"）
        var endTokens = (context.children ?? [])
            .Where(c => c is ITerminalNode t && (t.Symbol.Type == L.KEY || t.Symbol.Type == L.TOUCH_AREA || t.Symbol.Type == L.D_ZONE))
            .Cast<ITerminalNode>()
            .ToList();
        for (int i = 0; i < context.slideType().Length; i++)
        {
            var endText = endTokens[i].GetText();
            var key = 0;
            var endArea = "";
            if (endTokens[i].Symbol.Type == L.TOUCH_AREA || endTokens[i].Symbol.Type == L.D_ZONE)
            {
                (endArea, key) = ParseTouchArea(endText);
            }
            else
            {
                key = int.Parse(endText);
            }
            var segment = new SlideSegment((Slide)currNote!)
            {
                Type = context.slideType()[i].GetText().StartsWith("V") &&
                    ((slide.StartArea != "" && !slide.StartIsDZone) || context.TOUCH_AREA().Length > 0)
                    ? SlideType.SLR // TouchSlide follows its explicit middle node.
                    : SlideTypeTool.FromSimai(context.slideType()[i].GetText(), slide.EndKey, key,
                    slide.StartArea != "" || context.TOUCH_AREA().Length > 0 || context.D_ZONE().Length > 0),
                RawShape = context.slideType()[i].GetText(),
                EndIsDZone = endTokens[i].Symbol.Type == L.D_ZONE,
                EndKey = key,
                EndArea = endArea
            };
            slide.segments.Add(segment);
        }
        
        // 接下来开始添加时间
        var durationCount = context.slideDuration().Length;
        if (durationCount == 1)
        { // 第一种情况，只有一个时间标记。则是全局时间标记
            var C = context.slideDuration()[0];
            var (waitTime, duration) = ((Duration?, Duration))VisitSlideDuration(C);
            if (waitTime != null) slide.WaitTime = waitTime;
            slide.Duration = duration;
        }
        else if (durationCount == context.slideType().Length)
        { // 第二种情况，每个上都有时间标记
            var waitTimeSet = 0; // 0:waitTime还未被设置，1:waitTime已被隐式设置，2:waitTime已被显式设置
            for (int i = 0; i < durationCount; i++)
            {
                var C = context.slideDuration()[i];
                var (waitTime, duration) = ((Duration?, Duration))VisitSlideDuration(C);
                if (waitTime != null)
                {
                    // 本次返回的waitTime的强度。用户显式设置的记为2，用户未显式设置、但是中括号中形如[190#8:3]这样指定了bpm、导致产生了一个隐式的waitTime的，记为1。
                    var hereStrength = isRealExactWaitTime ? 2 : 1;
                    // 采信这个waitTime的条件：必须在头尾，且强度更大
                    if ((i == 0 || i == durationCount - 1) && hereStrength > waitTimeSet)
                    {
                        slide.WaitTime = waitTime;
                        waitTimeSet = hereStrength;
                    }
                    // 给警告的条件：未被采信（即上一个分支没命中），且是显式设置的
                    else if (isRealExactWaitTime) AddAlert(Warning, Locale.InvalidWaitTime);
                }
                slide.segments[i].Duration = duration;
            }
        }
        else throw Utils.Fail("duration的个数不对"); // 已经在语法层做过检查了，所以这个分支按说是永远不会命中的。

        ApplyModifiers(context.modifiers(), slide, false);
        if (slide.HasSelectableOrbit)
        {
            // Validate only the new selector syntax; preserve raw legacy SC.
            // The actual reference refuses zero-length C orbits and mixed
            // Touch paths containing key-only shapes such as wifi/thunder.
            var error = "Invalid selectable-orbit slide";
            if (!AquaMai.Alpha053.Core.SlidePathParser.TryParsePath(
                    slide.GetTouchPathExpression() + "[4:1]", out var path) ||
                !AquaMai.Alpha053.Core.SlideSyntaxValidator.TryValidate(path, out error))
            {
                AddAlert(Error, error);
                throw new ConversionException(alerts);
            }
        }
        if (StrictLevel != StrictLevelEnum.Strict && slide.OwnHead is Star)
        { // 在VisitSlide中构造星星头时没有检测到任何特殊修饰符，所以被按常规方法构造了。
            // 这里我们再检查一次，如果有修饰符的话应用之并给警告
            if (GetModifier(L.NO_STAR, out var t))
            { // 标记了NO_STAR的星星，则不要放head、但是需要手动设置Key
                var key = slide.OwnHead.Key;
                slide.OwnHead = null;
                slide.Key = key;
                slide.NoHead = true;
                AddAlert(Warning, string.Format(Locale.FixModifiersOnHead, t));
            }
            else if (GetModifier(L.STAR_TO_TAP, out var t2))
            {
                slide.OwnHead = new Tap(slide.OwnHead);
                AddAlert(Warning, string.Format(Locale.FixModifiersOnHead, t2));
            }
        }
        return true;
    }

    public sealed override object VisitSlide(P.SlideContext context)
    {
        if (context.tap()?.noteSkin() != null) throw new ArgumentException("~ 图片皮肤暂不支持普通 slide。");
        currContext = context;
        var result = new Slide(chart, now);
        
        if (context.holdSlideHead() != null)
        {
            var head = (Hold)VisitHoldSlideHead(context.holdSlideHead());
            result.OwnHead = head;
            result.StartIsDZone = head.IsDZone;
        }
        else if (context.touchHoldSlideHead() != null)
        {
            var head = (TouchHold)VisitTouchHoldSlideHead(context.touchHoldSlideHead());
            (result.StartArea, result.Key) = ParseTouchArea(head.TouchArea);
            result.NoHead = true;
            HoldSlideHeads.States.Add(result, new HoldSlideHeads.State(head,
                context.touchHoldSlideHead().duration() != null));
        }
        else if (context.tap() != null)
        { // 普通按键起点
            // 处理星星头
            Tap? head = (Tap)VisitTap(context.tap());
            result.StartIsDZone = head.IsDZone;
            if (GetModifier(L.NO_STAR))
            { // 标记了NO_STAR的星星，则不要放head、但是需要手动设置Key
                result.Key = head.Key;
                head = null;
                result.NoHead = true;
            }
            else if (!GetModifier(L.STAR_TO_TAP)) head = new Star(head); // 除非标记了STAR_TO_TAP，否则把tap转为star
            result.OwnHead = head;
        }
        else
        { // touch区起点（B1-5这种，AquaMai mod NMSSS）：无普通星头，记录StartArea
            var headCtx = context.touchHead();
            var headText = headCtx.TOUCH_AREA().GetText();
            (result.StartArea, result.Key) = ParseTouchArea(headText);
            result.StartIsDZone = headText.EndsWith('d');
            // 星星头类型只由头自己的修饰符决定：Cm- → MNSTP、Cb- → BRSTP、C-（body 带 m 也不影响）→ NMSTP（2026-08-25 用户规则）。
            // 头的 m/b 不再写入 slide.IsMine/IsBreak——body 的绝赞/地雷位只由 body 修饰符决定（Cb-A4m → BRSTP + MNSSS，不是 MBSSS）。
            foreach (var headMod in context.touchHead().modifiers().children ?? [])
            {
                if (headMod is ITerminalNode headTok)
                {
                    var headModText = headTok.GetText();
                    if (headModText == "m") result.HeadIsMine = true;
                    else if (headModText == "b") result.HeadIsBreak = true;
                    else if (headModText == "x") result.IsEx = true;
                    else if (headModText == "f") result.HeadIsFirework = true;
                    else if (headModText == "c") result.IgnoreSV = true;
                    else extraModifiers.Add(headTok.Symbol); // ? / ! / $ / @ 等留待 GetModifier / 尾部警告
                }
            }
            if (GetModifier(L.NO_STAR)) result.NoHead = true; // touch区起点也可以写?/!去掉touchstar头
        }
        
        currNote = result;
        VisitSlideBody(context.slideBody());
        CompleteHoldSlideHead(result, context);
        return result;
    }

    /// <summary>自定义滑条 code 直通（Majdata 的 SC shape，如 3Q5K7[8:2] / 7Q1K3b[8:2]）：
    /// Q5/P5 的 5 是 Orbit 圆编号而非键位、K 是终点命令，无法用 SlideSegment 建模，
    /// 于是整段 code 原样交给生成器写进 ma2 自定义滑条第 7 列（游戏端 AquaMai SlideCodeParser 解析）。
    /// 起点是环键 → 星头 NMSTR 系列；起点是 touch/D 区 → NMSTP 系列。</summary>
    public sealed override object VisitSlideCodeNote(P.SlideCodeNoteContext context)
    {
        currContext = context;
        var code = context.SLIDE_CODE().GetText();
        if (code.All(c => "1234567890ABCPQK".Contains(c)) &&
            !AquaMai.ChartVisuals.CanonicalSlideCodeParser.TryParse(code, out _, out var codeError))
        {
            AddAlert(Error, codeError);
            throw new ConversionException(alerts);
        }
        var result = new Slide(chart, now) { RawCustomCode = code };

        var c0 = code[0];
        if (c0 >= '1' && c0 <= '8')
        {
            result.Key = c0 - '0';
            result.OwnHead = new Star(new Tap(chart, now) { Key = result.Key });
        }
        else
        { // touch/D 区起点：与 touchHead 同样处理（无普通星头，记录 StartArea）
            var headText = code.Length >= 2 && code[1] == 'd' ? code[..2] : code[..1];
            (result.StartArea, result.Key) = ParseTouchArea(headText);
        }

        currNote = result; // Duration must belong to this SC slide, including at chart start.
        var durationSet = false;
        foreach (var child in context.children ?? [])
        {
            if (child is P.SlideDurationContext durCtx && !durationSet)
            {
                durationSet = true;
                var (waitTime, duration) = ((Duration?, Duration))VisitSlideDuration(durCtx);
                if (waitTime != null) result.WaitTime = waitTime;
                result.Duration = duration;
            }
        }

        ApplyModifiers([context.modifiers()], result, false);
        // code 语法里没有单独的头修饰符位置（b/m/x 写在 code 之后，覆盖整条星星），
        // 因此把头一并同步：7Q1K3b → BRSSS + BRSTR。
        if (result.OwnHead != null)
        {
            result.OwnHead.IsBreak = result.IsBreak;
            result.OwnHead.IsMine = result.IsMine;
            result.OwnHead.IsEx = result.IsEx;
        }
        return result;
    }

    public sealed override object VisitSharedHeadSlide(P.SharedHeadSlideContext context)
    {
        currContext = context;
        var result = new Slide(chart, now);
        if (currNote is Slide prevSlide)
        {
            result.SharedHeadWith = prevSlide.SharedHeadWith??prevSlide;
            result.StartArea = prevSlide.StartArea; // 同头星星继承起点的区域（touch区也继承）
            result.StartIsDZone = prevSlide.StartIsDZone;
            // 链段续写（*5-3 带显式起点键）：段起点=该键（合法谱面中=上段终点），供段类型计算/生成器取起点。
            // 无键的同头（*-6）：起点=星头键（Slide.Key 经 SharedHeadWith 链到树根），此处不设 override。
            var startKeyTok = context.KEY();       // (KEY | TOUCH_AREA | D_ZONE)? 可选 → 单元素访问器，null=缺省
            var startAreaTok = context.TOUCH_AREA();
            var startDZoneTok = context.D_ZONE();
            if (startKeyTok != null || startAreaTok != null || startDZoneTok != null)
            {
                var startText = startKeyTok != null ? startKeyTok.GetText()
                    : startAreaTok != null ? startAreaTok.GetText() : startDZoneTok!.GetText();
                var startKey = startKeyTok != null ? int.Parse(startText) : ParseTouchArea(startText).Item2;
                result.StartIsDZone = startDZoneTok != null;
                if (startKeyTok == null)
                { // 显式给了区域起点（*B1-5 / *7d-5）：区域也从该起点取，否则会继承上一条的起区
                    result.StartArea = ParseTouchArea(startText).Item1;
                }
                if (startKey != prevSlide.EndKey)
                    AddAlert(Warning, string.Format(Locale.InvalidSlide, $"{startText} (shared-head start {startKey} != previous segment end {prevSlide.EndKey})")); // 仅警告，仍以谱面为准
                result.startKeyOverride = startKey;
            }
        }
        else throw Utils.Fail("同头星星，找不到上一条");
        
        currNote = result;
        VisitSlideBody(context.slideBody());
        return result;
    }
}
