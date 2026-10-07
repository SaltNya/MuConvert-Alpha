using System.Globalization;
using System.Text;
using MuConvert.generator;
using MuConvert.utils;
using Rationals;
using static MuConvert.utils.Alert.LEVEL;

namespace MuConvert.mai;

public partial class MA2Generator : IGenerator<MaiChart>
{
    protected record MA2Line(string Name, int Bar, int Tick, int Key, string Extra = "")
    {
        public override string ToString()
        {
            var extra = !string.IsNullOrEmpty(Extra) ? "\t" + Extra : "";
            return $"{Name}\t{Bar}\t{Tick}\t{Key}{extra}";
        }
    };
    
#pragma warning disable CS8618
    public MA2Generator(bool isUtage = false)
#pragma warning restore CS8618
    {
        IsUtage = isUtage;
    }

    // 除非你知道你在做什么，不然以下两个变量请勿修改！
    public bool IsUtage;
    public int MA2Version = 105;
    public int RSL = 384;
    
    protected MaiChart chart;
    protected List<MA2Line> lines = [];
    protected readonly List<Alert> alerts = [];
    
    private string headTemplate = @"VERSION	0.00.00	{0}
FES_MODE	{1}
BPM_DEF	{2:F3}	{3:F3}	{4:F3}	{5:F3}
MET_DEF	4	4
RESOLUTION	{6}
CLK_DEF	{7}
COMPATIBLE_CODE	MA2
GENERATED_BY	MuConvert v{8}

";

    private Rational __1_384 = new(1, 384);

    /**
     * 把Rational的时间近似到RESOLUTION允许的最接近tick上
     */
    private (int, int) BT(Rational r, int offset = 0) => Utils.BarAndTick(r, RSL, offset);

    // 持续时间/等待时间，使用"总tick数"（可超过1小节），不是小节内tick
    protected int T(Rational r, int offset = 0) => Utils.Tick(r, RSL, offset, r > 0 ? 1 : 0);
    protected int T(int bar, int tick) => bar * RSL + tick;
    protected int T(MA2Line ma2Line) => T(ma2Line.Bar, ma2Line.Tick);

    protected void Warn(string description, Note note)
    {
        alerts.Add(new Alert(Warning, description, (chart, note.Time), null, note.DebuggerDisplay()));
    }
    
    protected virtual MA2Line? AddTap(Tap tap, int bar, int tick)
    {
        var prefix = "NM";
        if (tap.IsMine)
        { // 地雷键（AquaMai mod）：MN=普通 MB=绝赞 MX=EX MZ=EX绝赞
            if (tap.IsEx && tap.IsBreak) prefix = "MZ";
            else if (tap.IsBreak) prefix = "MB";
            else if (tap.IsEx) prefix = "MX";
            else prefix = "MN";
        }
        else if (tap.IsBreak && tap.IsEx) prefix = "BX";
        else if (tap.IsBreak) prefix = "BR";
        else if (tap.IsEx) prefix = "EX";
        var name = tap is Star ? "STR" : "TAP";
                
        string extra = "";
        if (tap is Hold hold)
        {
            name = "HLD";
            extra = T(hold.Duration.Bar, hold.Visual?.ReferenceMotion == true ? 0 : -hold.FalseEachIdx).ToString();
        } 
        if (tap.IsDZone) extra = extra.Length == 0 ? "DZ" : extra + "\tDZ";
        return AppendStreamId(new MA2Line(prefix + name, bar, tick, tap.Key - 1, extra), tap);
    }

    // 重叠流内的音符行尾附加所属流类型键（ma2 行尾 s{N} 字段，AquaMai mod 读取：
    // 该音符的曲线类型键 = s{N}，只吃本流的 `SVSP/HS ... s{N}=...` 类型化曲线，
    // 与主谱全局/普通类型曲线完全隔离）。流内变速由曲线驱动，不再内嵌单点倍率。
    protected MA2Line AppendStreamId(MA2Line line, Note note)
        => AppendStreamId(AppendVisualMarker(AppendFakeMarker(AppendNoteSkinMarker(AppendTouchRadiusMarker(AppendStarHeadMarker(AppendFireworkMarker(line, note), note), note), note), note.IsFake), note.Visual), note.StreamId);

    private static MA2Line AppendFireworkMarker(MA2Line line, Note note)
        => note is Tap { IsFirework: true } ? line with { Extra = line.Extra.Length == 0 ? "FW1" : line.Extra + "\tFW1" } : line;

    private static MA2Line AppendStarHeadMarker(MA2Line line, Note note)
    {
        if (note is not Star { IsForcedStar: true } star) return line;
        var marker = AquaMai.ChartVisuals.StarHead.Encode(star.IsFakeRotate);
        return line with { Extra = line.Extra.Length == 0 ? marker : line.Extra + "\t" + marker };
    }

    private static MA2Line AppendNoteSkinMarker(MA2Line line, Note note)
    {
        if (string.IsNullOrEmpty(note.Skin)) return line;
        var marker = AquaMai.ChartVisuals.NoteSkin.Encode(note.Skin);
        return line with { Extra = line.Extra.Length == 0 ? marker : line.Extra + "\t" + marker };
    }

    private static MA2Line AppendTouchRadiusMarker(MA2Line line, Note note)
    {
        if (note is not Touch { CustomRadius: > 0 } touch || note is TouchHold) return line;
        var marker = AquaMai.ChartVisuals.TouchRadius.Encode(touch.CustomRadius);
        return line with { Extra = line.Extra.Length == 0 ? marker : line.Extra + "\t" + marker };
    }

    private MA2Line AppendVisualMarker(MA2Line line, AquaMai.ChartVisuals.VisualNote? visual)
        => chart.HasVisualCommands && visual != null ? line with { Extra = line.Extra.Length == 0 ? visual.Encode() : line.Extra + "\t" + visual.Encode() } : line;

    private static MA2Line AppendFakeMarker(MA2Line line, bool fake)
        => fake ? line with { Extra = line.Extra.Length == 0 ? "FK" : line.Extra + "\tFK" } : line;

    private MA2Line AppendStreamId(MA2Line line, string? streamId)
    {
        if (!string.IsNullOrEmpty(streamId))
        {
            line = line with { Extra = line.Extra.Length > 0 ? line.Extra + "\t" + streamId : streamId };
        }
        return line;
    }

    private HashSet<string> _broadTap = ["TAP", "HLD", "STR", "BRK", "XTP", "XHO", "BST", "XST"];
    protected bool hasSameTimeTap(MA2Line ma2Line)
    {
        var curT = T(ma2Line);
        for (int i = lines.Count - 1; i >=0 ; i--)
        {
            var l = lines[i];
            if (T(l) < curT) break;
            if (T(l) == curT && l.Key == ma2Line.Key && l.Extra.Split('\t').Contains("DZ") == ma2Line.Extra.Split('\t').Contains("DZ") && // D 与普通键属于独立判定队列
                _broadTap.Contains(l.Name[^3..]) && _broadTap.Contains(ma2Line.Name[^3..])) return true;
        }
        return false;
    }

    protected virtual List<MA2Line> AddSlide(Slide slide, int bar, int tick)
    {
        List<MA2Line> result = [];

        if (slide.Visual?.ReferenceMotion == true && !slide.HasSelectableOrbit && slide.RawCustomCode == null && slide.StartArea == "" &&
            slide.segments.All(s => s.EndArea == "" && s.RawShape != "w" && s.Type != SlideType.SF_))
        {
            var visualRoute = BuildNativeDCode(slide);
            if (visualRoute?.StartsWith("DG1:", StringComparison.Ordinal) == true)
                slide.Visual.ReferenceSlide = visualRoute[4..];
        }

        // 自定义 slide（任一端点是 touch 区，或 code 直通如 3Q5K7，AquaMai mod）：NMSSS/BRSSS/MNSSS/MBSSS + 星头
        if (slide.HasSelectableOrbit || UsesNativeDGeometry(slide) || slide.StartArea != "" || slide.segments.Any(s => s.EndArea != "") || slide.RawCustomCode != null)
            return AddCustomSlide(slide, bar, tick);

        if (slide.OwnHead != null)
        {
            var headTap = AddTap(slide.OwnHead, bar, tick);
            if (headTap != null)
            {
                if (hasSameTimeTap(headTap)) Warn(Locale.SimultaneousSlideHead, slide);
                else result.Add(headTap);
            }
        }
        
        // 首先很重要的一点是，详见 https://github.com/Neskol/MaiLib/issues/46#issuecomment-3301893924 ，
        // 官机现在对于多段星星，是会无视掉每一段分别指定的时长，把总时长加和然后全程匀速处理的。
        // 至少在我上述测试的版本是这样；但为了防止万一我测试错了、或者将来相关的行为改变，这里还是尊重chart原始记法、分两类处理。
        var totalLen = T(slide.Duration.Bar);
        
        # region 把时长平均分配给所有没有显式写出时长的段
        List<int?> segmentValue = [];
        var unassignedValue = totalLen;
        var unassignedCount = 0;
        for (int i = 0; i < slide.segments.Count - 1; i++)
        {
            var seg = slide.segments[i];
            if (seg.Duration != null)
            {
                var t = T(seg.Duration.Bar);
                segmentValue.Add(t);
                unassignedValue -= t;
            }
            else
            {
                segmentValue.Add(null);
                unassignedCount++;
            }
        }
        unassignedCount++; // 对应于最后一段
        var toAssignValue = unassignedValue / unassignedCount; // 未分配的时间分配给所有未分配段，每段分配到的量
        # endregion
        
        int segIdx;
        for (segIdx = 0; segIdx < slide.segments.Count; segIdx++)
        {
            var seg = slide.segments[segIdx];
            var len = segIdx == slide.segments.Count - 1 ? 
                totalLen : // 对于最后一段，剩的时间全给它。以保证总长是正确的。
                segmentValue[segIdx] ?? toAssignValue; // 除此之外，则是优先使用显式分配的时间、没有则使用平均时间
            totalLen -= len;
            int waitTime = 0;
            
            var prefix = "NM";
            if (segIdx == 0)
            {
                if (slide.IsMine)
                { // 地雷slide：MN=普通 MB=绝赞 MZ=EX绝赞
                    if (slide.IsEx && slide.IsBreak) prefix = "MZ";
                    else if (slide.IsBreak) prefix = "MB";
                    else prefix = "MN";
                }
                else if (slide.IsEx && slide.IsBreak) prefix = "BX";
                else if (slide.IsBreak) prefix = "BR";
                waitTime = T(slide.WaitTime.Bar, slide.Visual?.ReferenceMotion == true ? 0 : -slide.FalseEachIdx);
            }
            else prefix = "CN";

            var name = seg.Type.ToString();
            
            result.Add(AppendStreamId(new MA2Line(prefix + name, bar, tick, seg.StartKey - 1,
                string.Join("\t", [waitTime, len, seg.EndKey - 1])), slide));
            tick += waitTime + len;
            while (tick >= RSL) { tick -= RSL; bar++; }
        }

        if (slide.IsEx) Warn(Locale.ExSlideIn105, slide);
        return result;
    }

    /**
     * 自定义 slide（任一端点是 touch 区，AquaMai mod）：NMSSS/BRSSS/MNSSS/MBSSS。
     * 行格式：TAG\tbar\tgrid\tstart\twait\tshoot\tend\tcode；星头由转换器输出
     * （按键环起点=NMSTR/MNSTR/BRSTR/MBSTR，touch区起点=NMSTP/BRSTP/MNSTP/MBTTP；
     *  MNSTP=地雷 touchstar，2026-08-26 起替代原 MNTTP 输出，游戏端 MNTTP 仍兼容旧谱）。
     * 同头slide只有树根输出星头。
     */
    protected virtual List<MA2Line> AddCustomSlide(Slide slide, int bar, int tick)
    {
        List<MA2Line> result = [];
        var nativeD = UsesNativeDGeometry(slide);
        var nativeTouch = slide.RawCustomCode == null && (slide.HasSelectableOrbit || slide.StartArea != "" || slide.segments.Any(s => s.EndArea != ""));
        var tag = (slide.IsMine, slide.IsBreak) switch
        {
            (false, false) => "NMSSS",
            (true, false) => "MNSSS",
            (false, true) => "BRSSS",
            (true, true) => "MBSSS",
        };

        // 同点滑条（相邻节点相同，如 E2m<E2m）：游戏端 SlideCodeParser 要求路径至少一段，
        // 距离 0 无法生成路径，报 "At least one path segment is required"（2026-08-25 实测）。
        // 检测任意相邻节点（起点→每段终点链）相同即整体跳过（含星头），避免留下孤星星。
        // 源谱写法 E2m<E2m/E6m>E6m 即此情况。
        var lastSeg = slide.RawCustomCode == null ? slide.segments[^1] : null; // code 直通没有 segment（Orbit 无法建模）
        if (!nativeD && !nativeTouch)
        {
            // 相邻同点节点（起点=终点，如 E2m<E2m 或 8q8）：游戏端距离 0 无法生成路径段，
            // 单节点情况必须整条跳过（否则只剩起点+终点，报 At least one path segment is required）。
            // 多节点情况（如 8q8-6>6>Cb）交由 BuildSlideCode 只跳过这些同点节点，保住整条滑条。
            string prevArea = slide.StartArea;
            int prevKey = slide.Key;
            bool prevD = slide.StartIsDZone;
            var sameCount = 0;
            foreach (var seg in slide.segments)
            {
                if (prevArea == seg.EndArea && prevKey == seg.EndKey && prevD == seg.EndIsDZone) sameCount++;
                prevArea = seg.EndArea;
                prevKey = seg.EndKey;
                prevD = seg.EndIsDZone;
            }
            if (sameCount == slide.segments.Count && slide.segments.Count > 0)
            {
                // 每一段都是同点节点 → 一个路径段也生成不出来，游戏端会报
                // "At least one path segment is required"，整条跳过。
                Warn("该slide的每一段都是同点节点（起点=终点），已跳过", slide);
                return result;
            }
        }

        // code 直通（3Q5K7 这类 Majdata SC 写法）：原样使用；否则由 segment 生成。
        // 放在星头之前：code 无法表达时（v/w 等）整条跳过，避免留下孤零零一个星星头。
        var code = slide.RawCustomCode ?? (nativeTouch ? BuildNativeTouchCode(slide) : nativeD ? BuildNativeDCode(slide) : BuildSlideCode(slide));
        if (code == null)
        {
            Warn("该slide含无法用自定义slide code表达的形状（v/w等），已跳过", slide);
            return result;
        }

        // 同头slide只有树根输出星头；无头（?/!）不输出星头（按键起点去NMSTR，touch区起点去touchstar）
        if (slide.SharedHeadWith == null && !slide.NoHead)
        {
            if (slide.StartArea == "")
            {
                if ((slide.StartIsDZone || UsesAdjacentStraightGeometry(slide)) && slide.OwnHead != null)
                {
                    var dHead = AddTap(slide.OwnHead, bar, tick);
                    if (dHead != null) result.Add(dHead);
                }
                else
                {
                // 星头类型只由星头自己的修饰符决定（OwnHead 上的 m/b；body 的 m 如 2-B6m 不影响星头）：
                // 2-6m / 2-B6m → NMSTR；2m-6 → MNSTR；2b-6 → BRSTR（2026-08-25 用户规则，与 touch 区 NMSTP/MNSTP 一致）。
                var headMine = slide.OwnHead?.IsMine ?? false;
                var headBreak = slide.OwnHead?.IsBreak ?? false;
                var headName = (headMine, headBreak) switch
                {
                    (false, false) => "NMSTR",
                    (true, false) => "MNSTR",
                    (false, true) => "BRSTR",
                    (true, true) => "MBSTR",
                };
                result.Add(AppendStreamId(new MA2Line(headName, bar, tick, slide.Key - 1), (Note?)slide.OwnHead ?? slide));
                }
            }
            else
            {
                // 星头类型只由头自己的修饰符决定（HeadIsMine/HeadIsBreak，见 SimaiParser.VisitSlide）：
                // C- / 8x-（body 是地雷 m）→ NMSTP；Cb- → BRSTP；Cm- → MNSTP（2026-08-25 用户规则）。
                var headTag = (slide.HeadIsMine, slide.HeadIsBreak) switch
                {
                    (false, false) => "NMSTP",
                    (true, false) => "MNSTP", // 地雷 touchstar（MNTTP 不再输出，游戏端兼容旧谱）
                    (false, true) => "BRSTP",
                    (true, true) => "MBTTP",
                };
                var key = slide.StartArea != "C" ? slide.Key - 1 : 0;
                result.Add(AppendStreamId(AppendVisualMarker(AppendFakeMarker(new MA2Line(headTag, bar, tick, key, $"{slide.StartArea}\t{(slide.HeadIsFirework ? 1 : 0)}\tM1"), slide.HeadIsFake), slide.HeadVisual), slide.HeadStreamId ?? slide.StreamId));
            }
        }

        // C 区起点（Key=0）时 start 字段必须输出 0，不能是 Key-1=-1——游戏端
        // PrepareBasicNoteData 用 MirrorInfo[mode, start] 查表，-1 会 IndexOutOfRange 崩溃（2026-08-25 实测修复）。
        var start = slide.StartArea != "C" ? slide.Key - 1 : 0;
        // end 列（0-7）会被游戏端 MirrorInfo → slideData.targetNote。touch 区终点
        // （B/C/D/E/A）的 end 列对渲染/判定无意义（真正的终点在 code 里），但 Star 头的
        // GetSlideLength(Straight, start, targetNote) 会把它当环键算出 len 0/1/7 →
        // Straight 路径表（仅 len2-6）OOB 刷红（2026-08-25 19ZZ 实测）。
        // 伪装成距起点 2 步的合法环键 (start+2)%8（len=2 必有路径，游戏端不崩）。
        // 注意 C 区起点时 start=0，EndKey-1 对 C 区终点是 -1（MirrorInfo 越界），也一并由伪装覆盖。
        var end = lastSeg == null || lastSeg.EndArea != "" || code.StartsWith("DG1:") || slide.HasSelectableOrbit
            ? (start + 2) % 8 : lastSeg.EndKey - 1;
        result.Add(AppendStreamId(new MA2Line(tag, bar, tick, start,
            $"{T(slide.WaitTime.Bar)}\t{T(slide.Duration.Bar)}\t{end}\t{code}"), slide));
        return result;
    }

    private static bool UsesNativeDGeometry(Slide slide)
    {
        if (slide.RawCustomCode != null || slide.HasSelectableOrbit || slide.segments.Count == 0) return false;
        var hasD = slide.StartIsDZone || slide.segments.Any(s => s.EndIsDZone ||
            s.RawShape != null && s.RawShape.StartsWith("V", StringComparison.Ordinal) && s.RawShape.EndsWith("d", StringComparison.Ordinal));
        var hasReverse = slide.segments.Any(s => s.RawShape is "rp" or "rq");
        // Stock straight templates only cover distances 2..6. Keep adjacent
        // key chains on the existing native geometry path, including 8↔1.
        var hasAdjacentStraight = UsesAdjacentStraightGeometry(slide);
        // Cross-area SSS retain the restored SC pipeline. Numeric/d routes use
        // reference geometry only; the mod generates native playable hit areas.
        // rp/rq reverse a pp/qq template including its line stems. Replacing
        // them with qq/pp draws a different orbit; import their geometry too.
        return (hasD || hasReverse || hasAdjacentStraight) && (slide.StartArea == "" || slide.StartIsDZone)
            && slide.segments.All(s => s.EndArea == "" || s.EndIsDZone);
    }

    private static string BuildNativeTouchCode(Slide slide)
    {
        return "TG1:" + slide.GetTouchPathExpression();
    }

    private static string? BuildNativeDCode(Slide slide)
    {
        var start = slide.startKeyOverride >= 0 ? slide.startKeyOverride : slide.Key;
        if (slide.segments.Count == 1 && (slide.segments[0].RawShape == "w" || slide.segments[0].Type == SlideType.SF_))
        {
            var fan = slide.segments[0];
            return $"DF1:{start}{(slide.StartIsDZone ? "d" : "")}w{fan.EndKey}{(fan.EndIsDZone ? "d" : "")}";
        }
        var expression = new StringBuilder("DG1:").Append(start);
        if (slide.StartIsDZone) expression.Append('d');
        var previousKey = start;
        var previousD = slide.StartIsDZone;
        var count = 0;
        foreach (var segment in slide.segments)
        {
            var shape = segment.RawShape ?? segment.Type.ToSimai(segment.StartKey);
            // A chained D wifi needs its own stem/fork transition; do not send
            // it to the single-lane root.
            if (shape == "w") return null;
            if (shape == "-" && segment.EndKey == previousKey && segment.EndIsDZone == previousD) continue;
            expression.Append(shape).Append(segment.EndKey);
            if (segment.EndIsDZone) expression.Append('d');
            previousKey = segment.EndKey;
            previousD = segment.EndIsDZone;
            count++;
        }
        return count > 0 ? expression.ToString() : null;
    }

    /// <summary>把1-indexed键位编码为自定义slide code的位置文本。
    /// ""=按键环数字（NodeA），C=中心（NodeC），A=两键中间（NodeF），其余=字母+数字（B/D/E区）。
    /// 规则：C(中心)之后禁止裸数字（游戏端 SlideCodeParser 报 digit should not follow 'C'），
    /// 因此 C 之后接环键节点时必须先输出 'A' 切回环键区。</summary>
    private static void AppendPos(StringBuilder sb, ref char currentCmd, string area, int key1)
    {
        switch (area)
        {
            case "":
                // 数字=环键(NodeA)。C 之后禁止裸数字，B/E/D/F 区之后裸数字也会被游戏端
                // 挂到该区（位置错），因此只要当前不是环键区就补 'A' 切回 NodeA。
                // 已是环键区时也要补：游戏端要求 code[1] 必须是命令字符（"the 2nd char should be a command"），
                // 起点后紧跟第二个环键节点（如 4-8 直线、v 折点、L 折点）会生成 "48..." 而被整条拒绝，
                // 补成 "4A8..." 才合法（2026-09 实测 4 个难度共 9 条被拒绝）。
                if (currentCmd != '\0')
                {
                    sb.Append('A');
                }
                currentCmd = 'A';
                sb.Append(key1);
                break;
            case "C":
                sb.Append('C');
                currentCmd = 'C';
                break;
            case "A":
                sb.Append('F').Append(key1); // A 区=相邻环键中间的触摸区，游戏端 NodeF
                currentCmd = 'F';
                break;
            default:
                sb.Append(area).Append(key1);
                currentCmd = area[0];
                break;
        }
    }

    /// <summary>K(终点命令)之后的编码——游戏端 SlideCodeParser 要求 code 倒数第二字符必须是 'K'
    /// （否则报 "should end with 'K' command"），即 K 后恰好 1 个字符：
    /// 环键=裸数字（NodeEnd→环键），C=C，A=F，B/D/E=区字母（1 字符）。
    /// 终点键号由 K 前最后一个节点（AppendPos 已输出完整 area+key）承担，
    /// K 后的 1 字符只是满足校验/冗余标记。</summary>
    private static void AppendEnd(StringBuilder sb, string area, int key1)
    {
        switch (area)
        {
            case "": sb.Append(key1); break; // NodeEnd+数字=环键终点
            case "C": sb.Append('C'); break;
            case "A": sb.Append('F'); break; // A 区终点：键号在 K 前节点（F+数字）
            default: sb.Append(area[0]); break; // B/D/E：键号在 K 前节点
        }
    }

    /// <summary>V折线的反射点：SLL=起点逆时针2格，SLR=顺时针2格。</summary>
    /// <summary>弧线字符：源谱写了 &lt; &gt; p q pp qq s z w ^ 时以源谱为准。
    /// Majdata 语义（MajdataView Assets/Scripts/Notes/TouchSlideDrop.cs AppendSegment）：
    /// &gt; q qq rp z = 顺时针，&lt; p pp rq s w = 逆时针，^ = 最短方向。
    /// 没有原始形状时返回 '\0'，由调用方回退到 seg.Type 映射。</summary>
    private static char ArcCharFor(string? rawShape)
    {
        switch (rawShape)
        {
            case "^": return '^';
            case ">" or "q" or "qq" or "rp" or "z": return '>';
            case "<" or "p" or "pp" or "rq" or "s" or "w": return '<';
        }

        return '\0';
    }

    private static int ReflectKey(int startKey1, int delta) => (startKey1 - 1 + delta + 8) % 8 + 1;

    /// <summary>生成自定义slide code（移植自MaiConverter的slide_to_code/merge_chain_code）。
    /// 单段：start+body+K+终点；多段：每段body带节点、最后K+终点。w/v无法表达返回null。</summary>
    private string? BuildSlideCode(Slide slide)
    {
        var hasD = slide.StartIsDZone || slide.segments.Any(s => s.EndIsDZone ||
            s.RawShape != null && s.RawShape.StartsWith("V", StringComparison.Ordinal) && s.RawShape.EndsWith("d", StringComparison.Ordinal));
        var sb = new StringBuilder(hasD ? "DG2:" : "");
        var currentCmd = '\0';
        void Node(string area, int key, bool dZone = false)
            => AppendPos(sb, ref currentCmd, dZone ? "A" : area, dZone ? (key + 6) % 8 + 1 : key);
        Node(slide.StartArea, slide.Key, slide.StartIsDZone);

        var prevArea = slide.StartArea;
        var prevKey = slide.Key;
        var prevD = slide.StartIsDZone;
        foreach (var seg in slide.segments)
        {
            var sameAsPrev = seg.EndArea == prevArea && seg.EndKey == prevKey && seg.EndIsDZone == prevD;
            prevArea = seg.EndArea;
            prevKey = seg.EndKey;
            prevD = seg.EndIsDZone;
            if (sameAsPrev)
            {
                // 起点 == 终点的段（如 8q8、6>6，出自 8bq8-6>6>Cb[1:8]）：Majdata 的 < > q p 都是以
                // 屏幕中心为圆心的极坐标弧，起终点角度相同 → 角度差恒为 0 的**零长度段**
                // （MajdataView Assets/Scripts/Notes/TouchSlideDrop.cs 用 Mathf.Repeat(start-end, 2π)，
                //   同点必为 0），视觉上就是相邻两段直接相连 → 不输出任何节点；游戏端
                // SlideCodeParser.NodeToNode 对同点节点同样直接忽略。
                // 2026-09 一度输出"弧线 + 同点节点"制造绕中心整圈，与制谱器表现不符（用户反馈
                // "和制谱器里呈现的效果不一样"）→ 已回退。
                continue;
            }
            // 弧线字符一律由 seg.Type 映射（游戏端 NodeA..F + 弧线语法的中转类型）。
            // 2026-09 曾尝试用源谱原始形状（q/qq/p/pp 等）直接当弧线字符，导致大量 SSS slide 方向颠倒
            //（用户反馈"好多方向反了的SSSslide"）→ 已回退：源谱形状语义 ≠ 游戏端 code 的 < / > 语义。
            if (hasD && seg.RawShape != null && seg.RawShape.StartsWith("V", StringComparison.Ordinal))
            {
                Node("", seg.RawShape[1] - '0', seg.RawShape.EndsWith("d", StringComparison.Ordinal));
            }
            else switch (seg.Type)
            {
                case SlideType.SI_ or SlideType.SSL or SlideType.SSR:
                    break; // 无弧线，节点直接续
                case SlideType.SCL:
                    sb.Append('<');
                    break;
                case SlideType.SCR:
                    sb.Append('>');
                    break;
                case SlideType.SUL or SlideType.SXL:
                    sb.Append('>'); // 沿pp相反方向=Bend_R
                    break;
                case SlideType.SUR or SlideType.SXR:
                    sb.Append('<'); // 沿qq相反方向=Bend_L
                    break;
                case SlideType.SV_:
                {
                    // v（V 形折线）到 touch/D 区端点：ma2 的 SV_ 只能用于环键端点，
                    // 自定义 slide code 里没有 V，用"起点→环键折点→终点"两段折线近似（折点取最短方向的中间键）。
                    var diff = (seg.EndKey - seg.StartKey + 8) % 8;
                    var half = diff <= 4 ? (diff + 1) / 2 : -(8 - diff + 1) / 2;
                    var mid = (seg.StartKey - 1 + half + 8) % 8 + 1;
                    AppendPos(sb, ref currentCmd, "", mid);
                    break;
                }
                case SlideType.SLL:
                    AppendPos(sb, ref currentCmd, "", ReflectKey(seg.StartKey, -2)); // V折点
                    break;
                case SlideType.SLR:
                    AppendPos(sb, ref currentCmd, "", ReflectKey(seg.StartKey, 2)); // V折点
                    break;
                default:
                    return null; // v/w 无法表达
            }
            Node(seg.EndArea, seg.EndKey, seg.EndIsDZone);
        }

        sb.Append('K');
        AppendEnd(sb, slide.segments[^1].EndIsDZone ? "A" : slide.segments[^1].EndArea, slide.segments[^1].EndKey);
        return sb.ToString();
    }

    protected virtual MA2Line? AddTouch(Touch touch, int bar, int tick)
    {
        var name = "TTP";
        List<string> extras = [];
        if (touch is TouchHold th)
        {
            name = "THO";
            extras.Add(T(th.Duration.Bar, th.Visual?.ReferenceMotion == true ? 0 : -th.FalseEachIdx).ToString());
        }

        var area = touch.TouchArea[0];
        var key = area != 'C' ? touch.Key - 1 : 0; // 目前，官机还不支持C1和C2分别写touch
        extras.Add(area.ToString());
        extras.Add(touch.IsFirework ? "1" : "0");
        extras.Add(touch.TouchSize);
        
        // AquaMai mod：地雷touch=MNTTP/MNTHO，绝赞touch=BRTTP/BRTHO（kansen），地雷绝赞=MBTTP/MBTHO
        var prefix = "NM";
        if (touch.IsMine && touch.IsBreak) prefix = "MB";
        else if (touch.IsMine) prefix = "MN";
        else if (touch.IsBreak) prefix = "BR";
        if (touch.IsEx)
        {
            if (touch is TouchHold) Warn(Locale.SpecialTouchIn105, touch);
            else extras.Add("XT1");
        }
        return AppendStreamId(new MA2Line(prefix + name, bar, tick, key, string.Join("\t", extras)), touch);
    }

    /// <summary>
    /// touchstar 简写（`B4$` 语法，只有星头没有轨迹）：NMSTP/MNSTP/BRSTP 单行，
    /// 行格式同 AddCustomSlide 的 touch 区星头（TAG\tbar\tgrid\tkey\t{area}\t0\tM1）。
    /// m+b 同时出现时按地雷处理（绝赞地雷 touchstar 游戏端暂未支持，2026-08-26）。
    /// </summary>
    protected virtual MA2Line? AddTouchStar(TouchStar touch, int bar, int tick)
    {
        if (touch.IsMine && touch.IsBreak)
        {
            Warn($"touchstar 同时带 m 和 b 修饰符（{touch.DebuggerDisplay()}）：绝赞地雷 touchstar 暂不支持，按地雷处理", touch);
        }

        var tag = touch.IsMine ? "MNSTP" : touch.IsBreak ? "BRSTP" : "NMSTP";
        var area = touch.TouchArea[0];
        var key = area != 'C' ? touch.Key - 1 : 0;
        return AppendStreamId(new MA2Line(tag, bar, tick, key, $"{area}\t0\tM1"), touch);
    }

    // 生成文件头
    protected void GenerateFileHead(StringBuilder result)
    {
        var bpmStatistics = chart.BpmList.BPM_DEF();
        string head = string.Format(CultureInfo.InvariantCulture, headTemplate, 
            $"{MA2Version / 100}.{MA2Version % 100:D2}.00", IsUtage?1:0, 
            bpmStatistics.Item1, bpmStatistics.Item2,  bpmStatistics.Item3, bpmStatistics.Item4,
            RSL, RSL/4 * chart.ClockCount, Utils.AppVersion);
        result.Append(head);
    }

    // 生成BPM段
    protected void GenerateBPM(StringBuilder result)
    {
        foreach (var bpm in chart.BpmList)
        {
            var (bar, tick) = BT(bpm.Time);
            result.AppendLine(FormattableString.Invariant($"BPM\t{bar}\t{tick}\t{bpm.Bpm:F3}"));
        }
        result.AppendLine($"MET\t0\t0\t4\t{chart.ClockCount}");
        result.AppendLine();
    }

    // 生成时间轴命令段（AquaMai mod）：SVSP/HS/BOUNCE/SPAWN
    protected void GenerateCommands(StringBuilder result)
    {
        if (chart.Commands.Count == 0) return;
        foreach (var cmd in chart.Commands.OrderBy(c => c.Time))
        {
            string? tag = cmd.Kind switch
            {
                "sv" => "SVSP",
                "hs" => "HS",
                "noisezone" => "NZONE",
                "bounce" => "BOUNCE",
                "spawn" => "SPAWN",
                "spawnmode" => "SPAWNMODE",
                "destroy" => "DESTROY",
                "colorv" => "COLORV",
                "sizev" => "SIZEV",
                "alphav" => "ALPHAV",
                "showjudgeinfo" => "SHOWJUDGEINFO",
                "showcomboinfo" => "SHOWCOMBOINFO",
                "showjudgetext" => "SHOWJUDGETEXT",
                "combodisplay" => "COMBODISPLAY",
                "innerbrightness" => "INNERBRIGHTNESS",
                "jline" => "JLINE",
                "judgeline" => "JUDGELINE",
                "judgelineexpand" => "JUDGELINEEXPAND",
                "outerbrightness" => "OUTERBRIGHTNESS",
                "shake" => "SHAKE",
                "flash" => "FLASH",
                "fade" => "FADE",
                "tint" => "TINT",
                "gaussian" => "GAUSSIAN",
                "neon" => "NEON",
                "trail" => "TRAIL",
                "brightness" => "BRIGHTNESS",
                "saturation" => "SATURATION",
                "contrast" => "CONTRAST",
                "rainbow" => "RAINBOW",
                "vignette" => "VIGNETTE",
                "zoom" => "ZOOM",
                "glitch" => "GLITCH",
                "tvnoise" => "TVNOISE",
                "hue" => "HUE",
                "move" => "MOVE",
                "rotate" => "ROTATE",
                "text" => "TEXT",
                "audio" => "AUDIO",
                "pvoverlay" => "PVOVERLAY",
                _ => AquaMai.ChartVisuals.ExtraScreenFilters.Find(cmd.Kind)?.Tag,
            };
            if (tag == null) continue;
            var (bar, tick) = BT(cmd.Time);
            result.AppendLine($"{tag}\t{bar}\t{tick}\t{cmd.Value}");
        }
        result.AppendLine();
    }

    // 生成主体音符段
    protected void GenerateNotes(StringBuilder result)
    {
        // 由于fes星星涉及一个重排序的问题，同时也为了后面统计方便，我们先调用GenerateMA2Lines、把音符转为适合直接写入的表示并放进lines数组中，最后再一块写入StringBuilder。
        for (int noteIdx = 0; noteIdx < chart.Notes.Count; noteIdx++)
        {
            var note = chart.Notes[noteIdx];
            if (noteIdx > 0)
            {
                var distToPrev = note.Time - chart.Notes[noteIdx - 1].Time;
                if (distToPrev > 0 && distToPrev < __1_384) Warn(Locale.NoteTooNear, note);
            }
            
            // Majdata offsets one fake-each separator by 1/128 bar. Native
            // legacy output used one MA2 tick and shortened wait/hold tails.
            var (bar, tick) = note is BorrowedNote || note.Visual?.ReferenceMotion == true
                ? BT(note.Time + new Rational(note.FalseEachIdx, 128)) : BT(note.Time, note.FalseEachIdx);
            if (note is BorrowedNote borrowed)
            {
                // A single native Fake placeholder carries the render metadata.
                // It never expands into slide heads, bars or score notes.
                lines.Add(AppendStreamId(new MA2Line("NMTAP", bar, tick, 0, borrowed.Trajectory.Encode()), note));
            }
            else if (note is Tap tap)
            {
                var l = AddTap(tap, bar, tick);
                if (l != null) lines.Add(l);
            }
            else if (note is TouchStar touchStar)
            {
                var l = AddTouchStar(touchStar, bar, tick);
                if (l != null) lines.Add(l);
            }
            else if (note is Touch touch)
            {
                var l = AddTouch(touch, bar, tick);
                if (l != null) lines.Add(l);
            }
            else if (note is Slide slide)
            {
                var ls = AddSlide(slide, bar, tick);
                foreach (var l in ls) lines.Add(l);
            }
        }
        
        lines = lines.OrderBy(x => x.Bar * RSL + x.Tick).ToList();
        foreach (var l in lines) result.AppendLine(l.ToString());
        result.AppendLine();
    }

    protected void GenerateStatistics(StringBuilder result, Statistics stats)
    {
        // 首先，把MA2中不合规的音符进行转写
        foreach (var (k, v) in statsRewrite())
        {
            if (!stats.Data.ContainsKey(k)) continue;
            stats.Data[v] = stats.Data.GetValueOrDefault(v) + stats.Data.GetValueOrDefault(k);
            stats.Data.Remove(k);
        }
        
        // 统计段
        foreach (var (k, v) in statsNameConversion())
        {
            result.AppendLine($"T_REC_{k}\t{stats.Data.GetValueOrDefault(v)}");
        }
        var totalNum = stats.Total;
        result.AppendLine($"T_REC_ALL\t{totalNum}");

        var statsScoring = stats.ByScoring;
        result.AppendLine($"T_NUM_TAP\t{statsScoring["TAP"] + statsScoring["TOUCH"]}");
        result.AppendLine($"T_NUM_BRK\t{statsScoring["BREAK"]}");
        result.AppendLine($"T_NUM_HLD\t{statsScoring["HOLD"]}");
        result.AppendLine($"T_NUM_SLD\t{statsScoring["SLIDE"]}");
        result.AppendLine($"T_NUM_ALL\t{totalNum}");

        var statsNoteType = stats.ByNoteType;
        var stats_judge = new Dictionary<string, int>
        {
            ["TAP"] = statsNoteType["TAP"] + statsNoteType["STR"] + statsNoteType["TTP"],
            ["HLD"] = stats.T_JUDGE_HLD,
            ["SLD"] = statsNoteType["SLD"],
        };
        foreach (var (k, v) in stats_judge)
        {
            result.AppendLine($"T_JUDGE_{k}\t{v}");
        }
        result.AppendLine($"T_JUDGE_ALL\t{stats_judge.Sum(x=>x.Value)}");
        
        result.AppendLine($"TTM_EACHPAIRS\t{stats.TTM_EACHPAIRS}");
        
        result.AppendLine($"TTM_SCR_TAP\t{(statsScoring["TAP"] + statsScoring["TOUCH"]) * 500}");
        result.AppendLine($"TTM_SCR_BRK\t{statsScoring["BREAK"] * 2600}");
        result.AppendLine($"TTM_SCR_HLD\t{statsScoring["HOLD"] * 1000}");
        result.AppendLine($"TTM_SCR_SLD\t{statsScoring["SLIDE"] * 1500}");
        var theoryScore = stats.OldScore;
        result.AppendLine($"TTM_SCR_ALL\t{theoryScore}");
        
        var score_sss = stats.WeightedNoteCount * 500; // 旧框扣除额外分
        result.AppendLine(FormattableString.Invariant($"TTM_SCR_S\t{Math.Ceiling(score_sss * 0.97 / 50) * 50}"));
        result.AppendLine($"TTM_SCR_SS\t{score_sss}");
        result.AppendLine($"TTM_RAT_ACV\t{(score_sss > 0 ? (long)theoryScore * 10000 / score_sss : 0)}"); // Fake-only charts have no score denominator.
        result.AppendLine();
    }
    
    public (string, List<Alert>) Generate(MaiChart _chart)
    {
        if (chart != null) throw new Exception(Locale.InstanceMultipleUsage);
        chart = _chart;
        if (chart.Notes.Count == 0)
        {
            alerts.Add(new Alert(Error, Locale.NoNotesInChart));
            throw new ConversionException(alerts);
        }
        chart.Sort();
        StringBuilder result = new StringBuilder();
        
        GenerateFileHead(result);
        GenerateBPM(result);
        GenerateCommands(result);
        GenerateNotes(result);
        GenerateStatistics(result, chart.Statistics);
        
        return (result.ToString(), alerts);
    }

    protected virtual Dictionary<string, string> statsRewrite() => new()
    {
        ["BRTTP"] = "BRTAP", ["EXTTP"] = "NMTTP", ["BXTTP"] = "BRTAP",
        ["BRTHO"] = "BRHLD", ["EXTHO"] = "NMTHO", ["BXTHO"] = "BRHLD",
        ["EXSLD"] = "NMSLD", ["BXSLD"] = "BRSLD",
        // 地雷键（AquaMai mod）：判定类别与普通相同
        ["MNTAP"] = "NMTAP", ["MBTAP"] = "BRTAP", ["MXTAP"] = "EXTAP", ["MZTAP"] = "BXTAP",
        ["MNHLD"] = "NMHLD", ["MBHLD"] = "BRHLD", ["MXHLD"] = "EXHLD", ["MZHLD"] = "BXHLD",
        ["MNSTR"] = "NMSTR", ["MBSTR"] = "BRSTR", ["MXSTR"] = "EXSTR", ["MZSTR"] = "BXSTR",
        ["MNTTP"] = "NMTTP", ["MBTTP"] = "BRTAP", ["MNTHO"] = "NMTHO", ["MBTHO"] = "BRHLD",
        ["MNSSS"] = "NMSLD", ["MBSSS"] = "BRSLD",
        ["MNSLD"] = "NMSLD", ["MBSLD"] = "BRSLD", ["MXSLD"] = "EXSLD", ["MZSLD"] = "BXSLD",
        ["MNSI_"] = "NMSLD", ["MBSI_"] = "BRSLD",
        ["MNSCL"] = "NMSLD", ["MBSCL"] = "BRSLD",
        ["MNSCR"] = "NMSLD", ["MBSCR"] = "BRSLD",
        ["MNSUL"] = "NMSLD", ["MBSUL"] = "BRSLD",
        ["MNSUR"] = "NMSLD", ["MBSUR"] = "BRSLD",
        ["MNSXL"] = "NMSLD", ["MBSXL"] = "BRSLD",
        ["MNSXR"] = "NMSLD", ["MBSXR"] = "BRSLD",
        ["MNSLL"] = "NMSLD", ["MBSLL"] = "BRSLD",
        ["MNSLR"] = "NMSLD", ["MBSLR"] = "BRSLD",
        ["MNSSL"] = "NMSLD", ["MBSSL"] = "BRSLD",
        ["MNSSR"] = "NMSLD", ["MBSSR"] = "BRSLD",
        ["MNSV_"] = "NMSLD", ["MBSV_"] = "BRSLD",
        ["MNSF_"] = "NMSLD", ["MBSF_"] = "BRSLD",
        ["NMSSS"] = "NMSLD", ["BRSSS"] = "BRSLD",
    };

    protected virtual Dictionary<string, string> statsNameConversion() => new()
    {
        ["TAP"] = "NMTAP", ["BRK"] = "BRTAP", ["XTP"] = "EXTAP", ["BXX"] = "BXTAP",
        ["HLD"] = "NMHLD", ["XHO"] = "EXHLD", ["BHO"] = "BRHLD", ["BXH"] = "BXHLD",
        ["STR"] = "NMSTR", ["BST"] = "BRSTR", ["XST"] = "EXSTR", ["XBS"] = "BXSTR",
        ["TTP"] = "NMTTP", ["THO"] = "NMTHO", 
        ["SLD"] = "NMSLD", ["BSL"] = "BRSLD",
    };
}
