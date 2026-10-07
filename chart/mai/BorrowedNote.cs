using AquaMai.ChartVisuals;
using Rationals;
namespace MuConvert.mai;

// Always Fake. Carrier kind selects the picture; its borrowed route determines
// motion and lifetime. The carrier's own Hold duration does not extend it.
public sealed class BorrowedNote : Note
{
    public readonly BorrowedTrajectory Trajectory;
    public override Duration Duration { get; set; }
    public BorrowedNote(MaiChart chart, Rational time, BorrowedTrajectory trajectory) : base(chart, time)
    {
        Trajectory = trajectory; Key = 1; IsFake = true;
        Duration = new Duration(this) { Seconds = (Rational)(decimal)trajectory.Seconds };
    }
    internal override string DebuggerDisplay() => Trajectory.Source;
}
