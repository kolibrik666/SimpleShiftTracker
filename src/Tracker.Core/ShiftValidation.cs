namespace Tracker.Core;

public static class ShiftValidation
{
    public static ShiftEdit Normalize(ShiftEdit edit, DateTime nowUtc)
    {
        var start = TimeMath.WholeSecond(edit.StartedUtc);
        var end = TimeMath.WholeSecond(edit.EndedUtc);
        if (end <= start) throw new TrackerValidationException("Shift end must be after its start.");
        if (end > nowUtc) throw new TrackerValidationException("A completed shift cannot end in the future.");
        var breaks = edit.Breaks.Select(b => new BreakPeriod(TimeMath.WholeSecond(b.StartedUtc),
            TimeMath.WholeSecond(b.EndedUtc))).OrderBy(b => b.StartedUtc).ToArray();
        var cursor = start;
        foreach (var pause in breaks)
        {
            if (pause.EndedUtc <= pause.StartedUtc || pause.StartedUtc < start || pause.EndedUtc > end)
                throw new TrackerValidationException("Every break must have a positive duration and stay within the shift.");
            if (pause.StartedUtc < cursor) throw new TrackerValidationException("Breaks cannot overlap.");
            cursor = pause.EndedUtc;
        }
        return new(edit.Id, start, end, breaks);
    }
}
