namespace Tracker.Core;

public static class ShiftValidation
{
    public static ShiftEdit Normalize(ShiftEdit edit, DateTime nowUtc)
    {
        var start = TimeMath.WholeSecond(edit.StartedUtc);
        var end = TimeMath.WholeSecond(edit.EndedUtc);
        if (end <= start) throw new TrackerValidationException("Shift end must be after its start.");
        if (end > nowUtc) throw new TrackerValidationException("A completed shift cannot end in the future.");
        return new(edit.Id, start, end, NormalizeBreaks(edit.Breaks, start, end));
    }

    public static ShiftEdit NormalizeActive(ActiveShiftEdit edit, DateTime nowUtc)
    {
        if (edit.Original.State == ShiftState.Ended)
            throw new TrackerValidationException("This shift has ended. Reopen its editor from history.");
        var start = TimeMath.WholeSecond(edit.StartedUtc);
        // Preserve the ongoing unpaid break when editing a paused shift.
        var end = edit.Original.State == ShiftState.Running ? TimeMath.WholeSecond(nowUtc)
            : edit.Original.WorkPeriods.Max(p => p.EndedUtc ?? p.StartedUtc);
        if (end > nowUtc || nowUtc < edit.Original.LastCheckpointUtc)
            throw new TrackerValidationException("Your computer clock moved backwards. Correct it before editing this shift.");
        if (start > end)
            throw new TrackerValidationException(edit.Original.State == ShiftState.Running
                ? "Shift start cannot be in the future."
                : "Shift start cannot be after the current break began.");
        return new(edit.Original.Id, start, end, NormalizeBreaks(edit.Breaks, start, end));
    }

    private static IReadOnlyList<BreakPeriod> NormalizeBreaks(IReadOnlyList<BreakPeriod> items, DateTime start, DateTime end)
    {
        var breaks = items.Select(b => new BreakPeriod(TimeMath.WholeSecond(b.StartedUtc),
            TimeMath.WholeSecond(b.EndedUtc))).OrderBy(b => b.StartedUtc).ToArray();
        var cursor = start;
        foreach (var pause in breaks)
        {
            if (pause.EndedUtc <= pause.StartedUtc || pause.StartedUtc < start || pause.EndedUtc > end)
                throw new TrackerValidationException("Every break must have a positive duration and stay within the shift.");
            if (pause.StartedUtc < cursor) throw new TrackerValidationException("Breaks cannot overlap.");
            cursor = pause.EndedUtc;
        }
        return breaks;
    }
}
