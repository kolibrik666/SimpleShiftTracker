using Microsoft.EntityFrameworkCore;

namespace Tracker.Core;

public sealed class TrackerService(TrackerDatabase database, TimeProvider? clock = null)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly object gate = new();
    public DateTime NowUtc => TimeMath.WholeSecond(clock.GetUtcNow().UtcDateTime);

    public TrackerSettings GetSettings()
    {
        using var db = database.Open();
        var settings = db.Settings.Single();
        return new(settings.DefaultHourlyRateCents, settings.TimeZoneId);
    }

    public void SetHourlyRate(decimal euros)
    {
        if (euros <= 0 || euros > long.MaxValue / 100m || decimal.Round(euros, 2) != euros)
            throw new TrackerValidationException("Enter a positive hourly rate with at most two decimal places.");
        lock (gate)
        {
            using var db = database.Open();
            db.Settings.Single().DefaultHourlyRateCents = (long)(euros * 100);
            db.SaveChanges();
        }
    }

    public ShiftSnapshot? GetActiveShift()
    {
        using var db = database.Open();
        return db.Shifts.AsNoTracking().Include(s => s.WorkIntervals)
            .SingleOrDefault(s => s.EndedUtc == null)?.Snapshot();
    }

    public ShiftSnapshot StartShift()
    {
        lock (gate)
        {
            using var db = database.Open();
            using var transaction = db.Database.BeginTransaction();
            if (db.Shifts.Any(s => s.EndedUtc == null))
                throw new TrackerValidationException("Resume or end your unfinished shift before starting another.");
            var now = NowUtc;
            if (db.Shifts.Any(s => s.EndedUtc > now))
                throw new TrackerValidationException("Your computer clock is earlier than recorded work. Correct it before starting a shift.");
            var shift = new ShiftEntity
            {
                Id = Guid.NewGuid(), StartedUtc = now, LastCheckpointUtc = now,
                HourlyRateCents = db.Settings.Single().DefaultHourlyRateCents,
                WorkIntervals = [new WorkIntervalEntity { StartedUtc = now }]
            };
            db.Shifts.Add(shift);
            db.SaveChanges();
            transaction.Commit();
            return shift.Snapshot();
        }
    }

    public ShiftSnapshot PauseShift() => ChangeActive((shift, now) =>
    {
        var open = shift.WorkIntervals.SingleOrDefault(p => p.EndedUtc == null);
        if (open is not null) open.EndedUtc = Later(open.StartedUtc, Later(shift.LastCheckpointUtc, now));
    });

    public ShiftSnapshot ResumeShift() => ChangeActive((shift, now) =>
    {
        if (shift.WorkIntervals.All(p => p.EndedUtc != null))
        {
            var lastEnd = shift.WorkIntervals.Max(p => p.EndedUtc ?? p.StartedUtc);
            if (now < lastEnd)
                throw new TrackerValidationException("Your computer clock moved backwards. Correct it before resuming.");
            shift.WorkIntervals.Add(new WorkIntervalEntity { StartedUtc = now });
        }
    });

    public ShiftSnapshot EndShift() => ChangeActive((shift, now) =>
    {
        var lastEnd = shift.WorkIntervals.Max(p => p.EndedUtc ?? p.StartedUtc);
        if (now < lastEnd || now < shift.LastCheckpointUtc)
            throw new TrackerValidationException("Your computer clock moved backwards. Correct it before ending the shift.");
        foreach (var open in shift.WorkIntervals.Where(p => p.EndedUtc == null))
            open.EndedUtc = Later(open.StartedUtc, now);
        shift.EndedUtc = Later(shift.StartedUtc, now);
    });

    private ShiftSnapshot ChangeActive(Action<ShiftEntity, DateTime> change)
    {
        lock (gate)
        {
            using var db = database.Open();
            using var transaction = db.Database.BeginTransaction();
            var shift = db.Shifts.Include(s => s.WorkIntervals).SingleOrDefault(s => s.EndedUtc == null)
                ?? throw new TrackerValidationException("There is no unfinished shift.");
            var now = NowUtc;
            change(shift, now);
            shift.LastCheckpointUtc = Later(shift.LastCheckpointUtc, now);
            db.SaveChanges();
            transaction.Commit();
            return shift.Snapshot();
        }
    }

    // Only the desktop host calls this on startup; a paused shift remains unfinished.
    public bool RecoverInterruptedShift()
    {
        lock (gate)
        {
            using var db = database.Open();
            var shift = db.Shifts.Include(s => s.WorkIntervals).SingleOrDefault(s => s.EndedUtc == null);
            var open = shift?.WorkIntervals.SingleOrDefault(p => p.EndedUtc == null);
            if (shift is null || open is null) return false;
            open.EndedUtc = Later(open.StartedUtc, shift.LastCheckpointUtc);
            db.SaveChanges();
            return true;
        }
    }

    // A missed suspend notification or a clock discontinuity must not bill an offline gap.
    public bool Checkpoint()
    {
        lock (gate)
        {
            using var db = database.Open();
            var shift = db.Shifts.Include(s => s.WorkIntervals).SingleOrDefault(s => s.EndedUtc == null);
            var open = shift?.WorkIntervals.SingleOrDefault(p => p.EndedUtc == null);
            if (shift is null || open is null) return false;
            var now = NowUtc;
            var gap = now - shift.LastCheckpointUtc;
            var recovered = gap > TimeSpan.FromSeconds(45) || gap < TimeSpan.Zero;
            if (recovered) open.EndedUtc = Later(open.StartedUtc, shift.LastCheckpointUtc);
            else shift.LastCheckpointUtc = now;
            db.SaveChanges();
            return recovered;
        }
    }

    public ShiftSnapshot SaveShift(ShiftEdit edit)
    {
        edit = ShiftValidation.Normalize(edit, NowUtc);
        var start = edit.StartedUtc;
        var end = edit.EndedUtc;

        lock (gate)
        {
            using var db = database.Open();
            using var transaction = db.Database.BeginTransaction();
            ShiftEntity shift;
            if (edit.Id is { } id)
            {
                shift = db.Shifts.Include(s => s.WorkIntervals).SingleOrDefault(s => s.Id == id)
                    ?? throw new TrackerValidationException("This shift no longer exists.");
                if (shift.EndedUtc is null)
                    throw new TrackerValidationException("End this shift before editing its times.");
            }
            else
            {
                shift = new ShiftEntity { Id = Guid.NewGuid(), HourlyRateCents = db.Settings.Single().DefaultHourlyRateCents };
                db.Shifts.Add(shift);
            }
            if (db.Shifts.Any(s => s.Id != shift.Id && s.StartedUtc < end &&
                (s.EndedUtc == null || s.EndedUtc > start)))
                throw new TrackerValidationException("This time overlaps another shift. Correct that shift first.");
            shift.StartedUtc = start;
            shift.EndedUtc = end;
            shift.LastCheckpointUtc = end;
            db.WorkIntervals.RemoveRange(shift.WorkIntervals);
            shift.WorkIntervals.Clear();
            var cursor = start;
            foreach (var pause in edit.Breaks)
            {
                if (pause.StartedUtc > cursor)
                    shift.WorkIntervals.Add(new() { StartedUtc = cursor, EndedUtc = pause.StartedUtc });
                cursor = pause.EndedUtc;
            }
            if (cursor < end) shift.WorkIntervals.Add(new() { StartedUtc = cursor, EndedUtc = end });
            db.SaveChanges();
            transaction.Commit();
            return shift.Snapshot();
        }
    }

    public void DeleteShift(Guid id)
    {
        lock (gate)
        {
            using var db = database.Open();
            var shift = db.Shifts.SingleOrDefault(s => s.Id == id)
                ?? throw new TrackerValidationException("This shift no longer exists.");
            if (shift.EndedUtc is null)
                throw new TrackerValidationException("End this shift before deleting it.");
            db.Shifts.Remove(shift);
            db.SaveChanges();
        }
    }

    public MonthlyReport GetMonthlyReport(int year, int month, TimeZoneInfo zone)
    {
        var localStart = new DateTime(year, month, 1);
        var start = TimeZoneInfo.ConvertTimeToUtc(localStart, zone);
        var end = TimeZoneInfo.ConvertTimeToUtc(localStart.AddMonths(1), zone);
        var now = NowUtc;
        using var db = database.Open();
        var shifts = db.Shifts.AsNoTracking().Include(s => s.WorkIntervals)
            .Where(s => s.StartedUtc < end && (s.EndedUtc == null || s.EndedUtc >= start))
            .OrderByDescending(s => s.StartedUtc).ToList();
        var rows = new List<ShiftReportRow>();
        foreach (var shift in shifts.Select(s => s.Snapshot()))
        {
            var periodStart = Later(start, shift.StartedUtc);
            var periodEnd = Earlier(end, shift.EndedUtc ?? now);
            if (periodEnd < periodStart) continue;
            if (periodEnd == start && shift.StartedUtc < start) continue;
            var worked = shift.WorkPeriods.Sum(p => TimeMath.Seconds(Later(periodStart, p.StartedUtc),
                Earlier(periodEnd, p.EndedUtc ?? now)));
            rows.Add(new(shift, periodStart, periodEnd, worked,
                TimeMath.Seconds(periodStart, periodEnd) - worked,
                TimeMath.Earnings(worked, shift.HourlyRateCents)));
        }
        return new(year, month, now, rows);
    }

    private static DateTime Later(DateTime a, DateTime b) => a > b ? a : b;
    private static DateTime Earlier(DateTime a, DateTime b) => a < b ? a : b;
}
