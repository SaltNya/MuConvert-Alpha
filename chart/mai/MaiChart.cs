using MuConvert.chart;
using Rationals;

namespace MuConvert.mai;

public class MaiChart: BaseChart<Note>
{
    public string DefaultTouchSize = "M1";

    /**
     * 时间轴命令（AquaMai mod）：<SV*2>、<HS*1.2>、<BOUNCE*8:1>、<SPAWN*1.225> 等。
     * Kind 为小写（sv/hs/bounce/spawn），Value 为 * 号后的原文。
     */
    public List<(Rational Time, string Kind, string Value)> Commands = [];
    public bool HasVisualCommands;

    /**
     * 获得谱面开始的时刻（即谱面中第一个音符的开始时刻）。
     * 
     * 返回的Duration可以理解成“从谱面开头到出现第一个音符所经过的时长”。
     * 所以同样的，它也有Bar、InvariantBar、Seconds的不同形态，因此使用Duration的形式存储。
     */
    public new Duration StartTime => new(new PseudoNote(this)) {Bar = Notes[0].Time};

    public override int TotalNotes => Statistics.Total;

    public bool IsDxChart => Notes.Any(note => // 判定DX谱的标准：存在
        note is Touch || note.IsEx || (note.IsBreak && note is not Tap) || // Touch 或者 保护套 或者 非Tap/Star的绝赞
        note is Slide { segments.Count: > 1 }); // 星星段数大于1（fes星星）

    public Statistics Statistics => new(this);

    protected override IEnumerable<Note> SortNotes()
    {
        return Notes.OrderBy(note => note.Time).ThenBy(n=>n.FalseEachIdx);
    }

    /// <summary>整体平移时，时间轴命令（SV/HS/BOUNCE/SPAWN）也要跟着平移。</summary>
    public override void Shift(Rational offset, decimal? bpm = null)
    {
        var realOffset = _calcOffsetForShift(offset, bpm ?? StartBpm);
        base.Shift(offset, bpm);
        Commands = Commands
            .Select(c => (Time: c.Time + realOffset, Kind: c.Kind, Value: c.Value))
            .Where(c => c.Time >= 0)
            .ToList();
    }
}
