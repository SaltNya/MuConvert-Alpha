using System.Diagnostics;
using MuConvert.utils;
using Rationals;

namespace MuConvert.mai;

public class Star : Tap
{
    // Only standalone authored $ / $$ heads use this display mode. Slide heads
    // and native STR records without metadata keep the game's rotation logic.
    public bool IsForcedStar;
    public bool IsFakeRotate;
    public Star(MaiChart chart, Rational time): base(chart, time) {}
    
    public Star(Tap inTake): base(inTake)
    {
        if (inTake is Star star) { IsForcedStar = star.IsForcedStar; IsFakeRotate = star.IsFakeRotate; }
    }
}

/**
 * 一个Slide表示一整根星星，包括1-3-5-7这种分段fes星星。但不包括1-3*-5这种同头星星，同头星星会被表示成两个slide。
 * 对于同头星星，仅有第一根设置Head属性，后面的星星不设置Head属性但设置SharedHeadWith指向第一根。
 * StartArea/EndArea（""=按键环，A/B/C/D/E=touch区）用于AquaMai mod的NMSSS自定义slide。
 */
[DebuggerDisplay("{DebuggerDisplay(),nq}")]
public class Slide : Note
{
    public Tap? OwnHead // 属于自己的星星头。同头星星的情况下，只有在SharedHeadWith=null的那一根星星上应该设置此项，其他的都直接通过SharedHeadWith链过去即可。
    {
        get => Children.FirstOrDefault() as Tap;
        set => Children.SetFirst(value);
    } 
    // PS: 根据simai语法，星星头既可以是普通的星星形状(1-5)，也可以是Tap形状的(1@-5)，也可以没有(1?-5或1!-5)
    public Slide? SharedHeadWith;
    public string? HeadStreamId; // Touch star heads may use a different typed curve from their slide body.
    public bool NoHead; // 无头标记（? / !）：按键起点=去星头但保留Key；touch区起点=不输出touchstar
    public string StartArea = ""; // 起点区域：""=按键环，A/B/C/D/E=touch区（B1-5这种）
    public bool StartIsDZone; // Preserve K+d (radius 4.8) separately from touch D (radius 4.1).
    /// <summary>Majdata/AquaMai 自定义滑条 code 直通（Majdata 的 SC shape，如 3Q5K7 / 7Q1K3）：
    /// 这类写法里 Q5/P5 的 5 是 Orbit 圆编号（不是键位）、K 是终点命令，无法用 SlideSegment 建模，
    /// 于是整段 code 原样保存，生成时直接写进 ma2 自定义滑条（NMSSS/BRSSS）的第 7 列。null=普通 slide。</summary>
    public string? RawCustomCode;
    public bool HeadIsMine; // 星星头自己的修饰符（touch区起点）：Cm- → MNSTP；body 的 m（如 C-A4m）不影响星头类型（2026-08-25 用户规则）
    public bool HeadIsBreak; // 星星头自己的修饰符（touch区起点）：Cb- → BRSTP
    public bool HeadIsFake; // Independent from IsFake (the slide body).
    public bool HeadIsFirework; // Touch head only; numeric heads keep Tap.IsFirework.
    public AquaMai.ChartVisuals.VisualNote? HeadVisual;
    public List<SlideSegment> segments = new();
    // P/Q selectors are TouchSlide middle nodes even when both ends are keys.
    // A raw legacy SC code such as 3Q5K7 continues to use its original parser.
    public bool HasSelectableOrbit => RawCustomCode == null && segments.Any(s =>
        s.RawShape?.Length > 1 && s.RawShape[0] is 'P' or 'Q');
    internal string GetTouchPathExpression()
    {
        static string Position(string area, int key, bool dZone) => dZone ? key.ToString(System.Globalization.CultureInfo.InvariantCulture) + "d" : area == "C" ? "C" :
            area.Length != 0 ? area + key : key.ToString(System.Globalization.CultureInfo.InvariantCulture) + (dZone ? "d" : "");
        var expression = new System.Text.StringBuilder(Position(StartArea, Key, StartIsDZone));
        foreach (var segment in segments)
            expression.Append(segment.RawShape ?? segment.Type.ToSimai(segment.StartKey))
                .Append(Position(segment.EndArea, segment.EndKey, segment.EndIsDZone));
        return expression.ToString();
    }
    public Duration WaitTime;
    internal int startKeyOverride = -1; // 链段续写（*5-3 带显式起点键）的段起点；-1=未指定（同头 *-6 起点=星头键）

    public Slide(MaiChart chart, Rational time) : base(chart, time)
    {
       WaitTime = new Duration(this) { InvariantBar = new Rational(1, 4) };
    }

    public override int Key
    {
        get => OwnHead?.Key ?? SharedHeadWith?.Key ?? _key;
        set
        {
            Utils.Assert(OwnHead == null && SharedHeadWith?.Key == null, "尝试为有头星星手动设置星星头");
            if (value < 0 || value > 8) throw new ArgumentException(string.Format(Locale.InvalidKey, value));
            _key = value; // 允许0：C区起点
        }
    }
    
    // 所有的SharedHeadWith的连接关系，会构成一棵树，其中只有树根节点有OwnHead且SharedHeadWith为null。
    public Slide SharedHeadWithRoot => SharedHeadWith != null ? SharedHeadWith.SharedHeadWithRoot : this;

    public int EndKey => startKeyOverride >= 0 ? startKeyOverride : (segments.Count > 0 ? segments.Last().EndKey : Key);
    
    /// <summary>code 直通（RawCustomCode）没有 segment，总时长只能存这里。</summary>
    public Duration? RawDuration;

    public override Duration Duration
    {
        get
        {
            if (segments.Count == 0) return RawDuration ?? new Duration(this); // code 直通：时长没有 segment 可分摊
            Duration? result = null;
            foreach (var s in segments)
            {
                if (s.Duration != null)
                {
                    if (result == null) result = s.Duration;
                    else result += s.Duration;
                }
            }

            return result ?? new Duration(this);
        }
        set
        {
            for (int i = 0; i < segments.Count - 1; i++)
            {
                segments[i].Duration = null;
            }
            if (segments.Count == 0)
            { // code 直通（3Q5K7 这类）：没有 segment，直接存总时长
                RawDuration = value;
                return;
            }
            segments.Last().Duration = value;
        }
    }

    internal override string DebuggerDisplay()
    {
        string result;
        if (SharedHeadWith != null) result = "*";
        else if (OwnHead != null)
        {
            result = OwnHead.DebuggerDisplay();
            if (!(OwnHead is Star)) result += "@"; // Tap形状的头
        }
        else result = Key + "?"; // 无头

        var segStart = Key;
        foreach (var s in segments)
        {
            result += s.Type.ToSimai(segStart) + s.EndKey;
            if (s.Duration != null) result += s.Duration.DebuggerDisplay();
            segStart = s.EndKey;
        }

        result += Modifiers;
        return result;
    }

    public override Rational EndTime => base.EndTime + WaitTime.Bar;
}

public class SlideSegment(Slide slide)
{
    public SlideType Type;
    public int EndKey;
    public string EndArea = ""; // ""=按键环，A/B/C/D/E=touch区
    public bool EndIsDZone;
    public Duration? Duration;
    /// <summary>源谱里该段的原始形状字符（Majdata 语法，如 "q"、">"、"pp"、"^"）。
    /// seg.Type 会被起点位置影响（如 ">" 起点在右半会编码成 SCL），同点折返段靠它还原绕行方向。</summary>
    public string? RawShape;
    
    public int StartKey
    {
        get
        {
            var idx = slide.segments.IndexOf(this);
            if (idx > 0) return slide.segments[idx-1].EndKey;
            return slide.startKeyOverride >= 0 ? slide.startKeyOverride : slide.Key;
        }
    }

    public string StartArea
    {
        get
        {
            var idx = slide.segments.IndexOf(this);
            return idx > 0 ? slide.segments[idx-1].EndArea : slide.StartArea;
        }
    }
}

