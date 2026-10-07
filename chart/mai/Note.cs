using System.Diagnostics;
using MuConvert.chart;
using MuConvert.utils;
using Rationals;

namespace MuConvert.mai;

/**
 * maimai的Note基类
 */
public abstract class Note: BaseNote
{
    public MaiChart Chart { get; internal set; }
    protected int _key;
    
    public bool IsBreak;
    public bool IsEx;
    public bool IsMine; // 地雷键（AquaMai mod）
    public bool IsFake; // Render-only: no input, judgment, score or hit effect.
    public bool IgnoreSV;
    public AquaMai.ChartVisuals.VisualNote? Visual;
    public string? Skin;

    public int FalseEachIdx = 0; // 如果>0，表示这是一个伪双押，数字越大、延后的时刻越多

    // 可重叠音符流（@{N}）内的局部 SV/HS 命令输出为"流类型化曲线"（ma2 行
    // `SVSP <bar> <grid> s1=50.0`，类型键 s1/s2/...，AquaMai mod 读取）；
    // 流内音符行尾附加 `s1` 标记字段，游戏端把该音符的曲线类型键设为 s1 →
    // 只吃本流曲线、与主谱（全局/普通类型）完全隔离。null = 不在流内。
    public string? StreamId;

    public Rational TimeInSecond => Chart.ToSecond(Time);
    
    public virtual Duration Duration
    {
        get => new(this);
        set => throw new InvalidOperationException(Locale.NoDuration);
    }
    
    public virtual int Key
    {
        get => _key;
        set
        {
            if (value is < 1 or > 8) throw new ArgumentException(string.Format(Locale.InvalidKey, value));
            _key = value;
        }
    }

    protected Note(MaiChart chart, Rational time)
    {
        Chart = chart;
        Time = time;
    }
    
    public virtual string Modifiers => (IsMine ? "m" : "") + (IsBreak ? "b" : "") + (IsEx ? "x" : "") + (IgnoreSV ? "c" : "");

    // 当前音符落在了哪些BPM区间内、分别有多长。
    public List<(int bpmIdx, decimal bpm, Rational start, Rational len)> BpmRanges =>
        StatisticsUtils.CalcBpmRanges(Time, EndTime, Chart);
    
    internal virtual string DebuggerDisplay() => "";

    public override Rational EndTime => Time + Duration.Bar;
}

[DebuggerDisplay("{DebuggerDisplay(),nq}")]
public class Tap(MaiChart chart, Rational time) : Note(chart, time)
{
    public bool IsDZone;
    public bool IsFirework; // f on Tap/Star/Hold; separate from rotation ($$).
    public Tap(Tap inTake): this(inTake.Chart, inTake.Time) // 拷贝构造函数
    {
        IsBreak = inTake.IsBreak;
        IsEx = inTake.IsEx;
        IsMine = inTake.IsMine;
        IsFake = inTake.IsFake;
        IgnoreSV = inTake.IgnoreSV;
        IsFirework = inTake.IsFirework;
        Visual = inTake.Visual;
        Skin = inTake.Skin;
        IsDZone = inTake.IsDZone;
        FalseEachIdx = inTake.FalseEachIdx;
        StreamId = inTake.StreamId;
        Key = inTake.Key;
    }

    internal override string DebuggerDisplay() => $"{Key}{Modifiers}";
    public override string Modifiers => base.Modifiers + (IsFirework ? "f" : "");
}

[DebuggerDisplay("{DebuggerDisplay(),nq}")]
public class Hold : Tap
{
    public override Duration Duration { get; set; }

    public Hold(MaiChart chart, Rational time) : base(chart, time) { Duration = new Duration(this); }
    
    internal override string DebuggerDisplay() => $"{Key}h{Modifiers}{Duration.DebuggerDisplay()}";
}

[DebuggerDisplay("{DebuggerDisplay(),nq}")]
public class Touch(MaiChart chart, Rational time) : Note(chart, time)
{
    private TouchSeries _touchSeries;

    public bool IsFirework;
    public string TouchSize = chart.DefaultTouchSize;
    public float CustomRadius; // Visual radius only; TouchArea still selects physical judgment.

    public string TouchArea
    {
        get => _touchSeries.ToString() + (Key == 0 ? "" : Key);
        set
        {
            if (value == "C") // 只有一个C字母的情况
            {
                _touchSeries = TouchSeries.C;
                _key = 0;
                return;
            }
            // 两个字符，字母+数字的情况
            if (value.Length != 2 || 
                !Enum.TryParse<TouchSeries>(value[..1], out var s) ||
                !int.TryParse(value[1..2], out var k) || 
                k < 1 || k > 8 || (s == TouchSeries.C && k > 2)
                ) throw new ArgumentException(string.Format(Locale.InvalidTouchArea, value));
            _touchSeries = s;
            _key = k;
        }
    }

    public override string Modifiers => base.Modifiers + (IsFirework ? "f" : "");
    
    internal override string DebuggerDisplay() => $"{TouchArea}{Modifiers}";
}

[DebuggerDisplay("{DebuggerDisplay(),nq}")]
public class TouchHold : Touch
{
    public override Duration Duration { get; set; }

    public TouchHold(MaiChart chart, Rational time) : base(chart, time) { Duration = new Duration(this); }

    internal override string DebuggerDisplay() => $"{TouchArea}h{Modifiers}{Duration.DebuggerDisplay()}";
}

/// <summary>
/// touchstar 简写（AquaMai mod 扩展语法 `B4$`）：只有星头、没有 slide 轨迹的 touch 区五瓣星。
/// 输出 NMSTP/MNSTP/BRSTP 单行（行格式同 AddCustomSlide 的 touch 区星头：TAG\tbar\tgrid\tkey\t{area}\t0\tM1）。
/// 与 slide 星头 touchstar（B1&gt;8[4:1] 等的 NMSTP）在游戏端是同一类音符。
/// </summary>
[DebuggerDisplay("{DebuggerDisplay(),nq}")]
public class TouchStar(MaiChart chart, Rational time) : Touch(chart, time);

// 仅用于内部实现某些trick时使用的“伪音符”。用户在正常的谱面中是不会看到这个的。
internal class PseudoNote(MaiChart chart) : Note(chart, 0);
