namespace Tracker.Core;

public enum ShiftState { Running, Paused, Ended }

public sealed record WorkPeriod(DateTime StartedUtc, DateTime? EndedUtc);
public sealed record BreakPeriod(DateTime StartedUtc, DateTime EndedUtc);

public sealed record ShiftSnapshot(
    Guid Id,
    DateTime StartedUtc,
    DateTime? EndedUtc,
    long HourlyRateCents,
    DateTime LastCheckpointUtc,
    IReadOnlyList<WorkPeriod> WorkPeriods)
{
    public ShiftState State => EndedUtc.HasValue ? ShiftState.Ended
        : WorkPeriods.Any(p => p.EndedUtc is null) ? ShiftState.Running : ShiftState.Paused;

    public long WorkedSeconds(DateTime asOfUtc) => WorkPeriods.Sum(p => TimeMath.Seconds(
        p.StartedUtc, p.EndedUtc ?? asOfUtc));

    public IReadOnlyList<BreakPeriod> GetBreaks()
    {
        var result = new List<BreakPeriod>();
        var cursor = StartedUtc;
        foreach (var period in WorkPeriods.OrderBy(p => p.StartedUtc))
        {
            if (period.StartedUtc > cursor)
                result.Add(new(cursor, period.StartedUtc));
            cursor = period.EndedUtc ?? cursor;
        }
        if (EndedUtc is { } end && end > cursor)
            result.Add(new(cursor, end));
        return result;
    }
}

public sealed record ShiftEdit(Guid? Id, DateTime StartedUtc, DateTime EndedUtc,
    IReadOnlyList<BreakPeriod> Breaks);

public sealed record ActiveShiftEdit(ShiftSnapshot Original, DateTime StartedUtc,
    IReadOnlyList<BreakPeriod> Breaks);

public sealed record TrackerSettings(long DefaultHourlyRateCents, string TimeZoneId);

public sealed record ShiftReportRow(ShiftSnapshot Shift, DateTime PeriodStartUtc,
    DateTime PeriodEndUtc, long WorkedSeconds, long BreakSeconds, decimal Earnings);

public sealed record MonthlyReport(int Year, int Month, DateTime GeneratedUtc,
    IReadOnlyList<ShiftReportRow> Rows)
{
    public long WorkedSeconds => Rows.Sum(r => r.WorkedSeconds);
    public decimal Earnings => Rows.Sum(r => r.Earnings);
}

public sealed class TrackerValidationException(string message) : Exception(message);

public static class TimeMath
{
    public static DateTime WholeSecond(DateTime utc)
    {
        if (utc.Kind != DateTimeKind.Utc)
            throw new TrackerValidationException("Times must include a UTC conversion.");
        return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    }

    public static long Seconds(DateTime start, DateTime end) =>
        Math.Max(0, (end.Ticks - start.Ticks) / TimeSpan.TicksPerSecond);

    public static decimal Earnings(long seconds, long rateCents) => seconds * (decimal)rateCents / 360000m;

    public static string Duration(long seconds) =>
        $"{seconds / 3600:00}:{seconds % 3600 / 60:00}:{seconds % 60:00}";

    public static decimal RoundedMoney(decimal amount) => decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
}
