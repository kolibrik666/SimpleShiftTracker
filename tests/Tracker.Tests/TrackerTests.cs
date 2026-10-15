using Microsoft.Data.Sqlite;
using Tracker.Core;

namespace Tracker.Tests;

public sealed class TrackerTests : IDisposable
{
    private readonly string directory = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
    private readonly FakeClock clock = new(new DateTimeOffset(2027, 1, 15, 18, 0, 0, TimeSpan.Zero));
    private readonly TrackerDatabase database;
    private readonly TrackerService service;
    private static readonly TimeZoneInfo Zone = LocalTimes.FindZone("Europe/Bratislava");

    public TrackerTests()
    {
        database = new(Path.Combine(directory, "tracker.db"));
        database.Initialize();
        service = new(database, clock);
    }

    private static DateTime Utc(int year, int month, int day, int hour = 0, int minute = 0, int second = 0) =>
        new(year, month, day, hour, minute, second, DateTimeKind.Utc);

    private ShiftSnapshot Add(DateTime start, DateTime end, params BreakPeriod[] breaks) =>
        service.SaveShift(new(null, start, end, breaks));

    [Fact]
    public void FreshDatabaseMigratesAndCanReopenWithoutLosingData()
    {
        var shift = Add(Utc(2026, 10, 5, 7), Utc(2026, 10, 5, 15));
        database.Initialize();
        var reopened = new TrackerService(new TrackerDatabase(database.DatabasePath), clock);
        Assert.Equal(1000, reopened.GetSettings().DefaultHourlyRateCents);
        Assert.Equal(shift.Id, Assert.Single(reopened.GetMonthlyReport(2026, 10, Zone).Rows).Shift.Id);
        Assert.Equal(DateTimeKind.Utc, shift.StartedUtc.Kind);
    }

    [Fact]
    public void ExclusiveStartupRecoversAnAbandonedMigrationLockWithoutLosingShifts()
    {
        var shift = Add(Utc(2026, 10, 5, 7), Utc(2026, 10, 5, 15));
        using (var connection = new SqliteConnection($"Data Source={database.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO __EFMigrationsLock (Id, Timestamp) VALUES (1, '2026-10-12T00:00:00Z')";
            command.ExecuteNonQuery();
        }
        database.Initialize(hasExclusiveStartupLock: true);
        Assert.Equal(shift.Id, Assert.Single(service.GetMonthlyReport(2026, 10, Zone).Rows).Shift.Id);
        Assert.Equal(80m, service.GetMonthlyReport(2026, 10, Zone).Earnings);
    }

    [Fact]
    public void EightHoursWithHalfHourBreakEarnsSeventyFiveEuros()
    {
        Add(Utc(2026, 10, 5, 7), Utc(2026, 10, 5, 15),
            new BreakPeriod(Utc(2026, 10, 5, 11), Utc(2026, 10, 5, 11, 30)));
        var report = service.GetMonthlyReport(2026, 10, Zone);
        Assert.Equal("07:30:00", TimeMath.Duration(report.WorkedSeconds));
        Assert.Equal(75m, report.Earnings);
        Assert.Equal(1800, Assert.Single(report.Rows).BreakSeconds);
    }

    [Fact]
    public void MultiplePausesExcludeBreaksAndEndWhilePausedWorks()
    {
        service.StartShift();
        clock.Advance(60); service.PauseShift();
        service.PauseShift(); // Idempotent repeated pause.
        clock.Advance(300); service.ResumeShift();
        service.ResumeShift(); // Does not create a second open interval.
        clock.Advance(120); service.PauseShift();
        clock.Advance(600);
        var ended = service.EndShift();
        Assert.Equal(180, ended.WorkedSeconds(service.NowUtc));
        Assert.Equal(2, ended.WorkPeriods.Count);
        Assert.Equal(2, ended.GetBreaks().Count);
        Assert.Null(service.GetActiveShift());
        Assert.Throws<TrackerValidationException>(() => service.EndShift());
    }

    [Fact]
    public void CannotStartTwoUnfinishedShifts()
    {
        service.StartShift();
        Assert.Throws<TrackerValidationException>(() => service.StartShift());
        service.PauseShift();
        Assert.Throws<TrackerValidationException>(() => service.StartShift());
    }

    [Fact]
    public void ActiveShiftIsLiveAndRunningTimeIsNotRoundedToMinutes()
    {
        service.StartShift();
        clock.Advance(3723);
        var report = service.GetMonthlyReport(2027, 1, Zone);
        Assert.Equal("01:02:03", TimeMath.Duration(report.WorkedSeconds));
        Assert.Equal(10.34m, TimeMath.RoundedMoney(report.Earnings));
        Assert.Equal(ShiftState.Running, Assert.Single(report.Rows).Shift.State);
    }

    [Fact]
    public void HourlyWageChangesApplyOnlyToNewShifts()
    {
        var first = Add(Utc(2026, 10, 5, 7), Utc(2026, 10, 5, 9));
        service.SetHourlyRate(12m);
        var second = Add(Utc(2026, 10, 6, 7), Utc(2026, 10, 6, 10));
        service.SaveShift(new(first.Id, first.StartedUtc, first.EndedUtc!.Value, []));
        Assert.Equal(1000, first.HourlyRateCents);
        Assert.Equal(1200, second.HourlyRateCents);
        Assert.Equal(56m, service.GetMonthlyReport(2026, 10, Zone).Earnings);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1.001)]
    public void RejectsInvalidWages(decimal wage) =>
        Assert.Throws<TrackerValidationException>(() => service.SetHourlyRate(wage));

    [Fact]
    public void GracefulPauseIsRestoredWithoutCountingOfflineTime()
    {
        service.StartShift();
        clock.Advance(60); service.PauseShift();
        clock.Advance(86400);
        var reopened = new TrackerService(database, clock);
        Assert.False(reopened.RecoverInterruptedShift());
        Assert.Equal(ShiftState.Paused, reopened.GetActiveShift()!.State);
        Assert.Equal(60, reopened.GetActiveShift()!.WorkedSeconds(reopened.NowUtc));
        reopened.ResumeShift(); clock.Advance(30);
        Assert.Equal(90, reopened.EndShift().WorkedSeconds(reopened.NowUtc));
    }

    [Fact]
    public void CrashRecoveryStopsAtLastPersistedCheckpoint()
    {
        service.StartShift();
        clock.Advance(15); Assert.False(service.Checkpoint());
        clock.Advance(86400);
        var reopened = new TrackerService(database, clock);
        Assert.True(reopened.RecoverInterruptedShift());
        Assert.False(reopened.RecoverInterruptedShift());
        Assert.Equal(ShiftState.Paused, reopened.GetActiveShift()!.State);
        Assert.Equal(15, reopened.GetActiveShift()!.WorkedSeconds(reopened.NowUtc));
    }

    [Fact]
    public void MissedSleepEventAndClockReversalPauseAtCheckpoint()
    {
        service.StartShift();
        clock.Advance(15); service.Checkpoint();
        clock.Advance(3600);
        Assert.True(service.Checkpoint());
        Assert.Equal(15, service.GetActiveShift()!.WorkedSeconds(service.NowUtc));
        service.ResumeShift();
        clock.Advance(-30);
        Assert.True(service.Checkpoint());
        Assert.Equal(ShiftState.Paused, service.GetActiveShift()!.State);
    }

    [Fact]
    public void PausingAfterClockReversalPreservesAlreadyCheckpointedWork()
    {
        service.StartShift();
        clock.Advance(15);
        service.Checkpoint();
        clock.Advance(-5);
        Assert.Throws<TrackerValidationException>(() => service.EndShift());
        var paused = service.PauseShift();
        Assert.Equal(15, paused.WorkedSeconds(service.NowUtc));
        Assert.Throws<TrackerValidationException>(() => service.ResumeShift());
        clock.Advance(5);
        service.ResumeShift();
        clock.Advance(1);
        Assert.Equal(16, service.EndShift().WorkedSeconds(service.NowUtc));
    }

    [Fact]
    public void StartingAfterClockReversalCannotOverlapRecordedWork()
    {
        service.StartShift();
        clock.Advance(10);
        service.EndShift();
        clock.Advance(-1);
        Assert.Throws<TrackerValidationException>(() => service.StartShift());
        clock.Advance(1);
        Assert.Equal(ShiftState.Running, service.StartShift().State);
    }

    [Fact]
    public void EditingBreaksRecalculatesSummaryAndDeleteRemovesShift()
    {
        var shift = Add(Utc(2026, 10, 5, 7), Utc(2026, 10, 5, 15));
        var edited = service.SaveShift(new(shift.Id, shift.StartedUtc, shift.EndedUtc!.Value,
            [new(Utc(2026, 10, 5, 11), Utc(2026, 10, 5, 12))]));
        Assert.Equal(7 * 3600, edited.WorkedSeconds(service.NowUtc));
        Assert.Equal(70m, service.GetMonthlyReport(2026, 10, Zone).Earnings);
        service.DeleteShift(shift.Id);
        Assert.Empty(service.GetMonthlyReport(2026, 10, Zone).Rows);
    }

    [Fact]
    public void InvalidEditsAreAtomicAndAdjacentShiftsAreAllowed()
    {
        var first = Add(Utc(2026, 10, 5, 7), Utc(2026, 10, 5, 15));
        Add(Utc(2026, 10, 5, 15), Utc(2026, 10, 5, 16));
        Assert.Throws<TrackerValidationException>(() =>
            service.SaveShift(new(first.Id, first.StartedUtc, Utc(2026, 10, 5, 15, 30), [])));
        Assert.Equal(9 * 3600, service.GetMonthlyReport(2026, 10, Zone).WorkedSeconds);
        Assert.Throws<TrackerValidationException>(() => Add(first.StartedUtc, first.StartedUtc));
        Assert.Throws<TrackerValidationException>(() => Add(service.NowUtc, service.NowUtc.AddHours(1)));
    }

    [Fact]
    public void RejectsInvalidAndOverlappingBreaks()
    {
        var start = Utc(2026, 10, 5, 7);
        var end = start.AddHours(8);
        Assert.Throws<TrackerValidationException>(() => Add(start, end, new BreakPeriod(start.AddSeconds(-1), start.AddMinutes(10))));
        Assert.Throws<TrackerValidationException>(() => Add(start, end, new BreakPeriod(end.AddMinutes(-10), end.AddSeconds(1))));
        Assert.Throws<TrackerValidationException>(() => Add(start, end, new BreakPeriod(start, start)));
        Assert.Throws<TrackerValidationException>(() => Add(start, end,
            new(start.AddHours(1), start.AddHours(2)), new(start.AddMinutes(90), start.AddHours(3))));
        Assert.Empty(service.GetMonthlyReport(2026, 10, Zone).Rows);
    }

    [Fact]
    public void CannotEditDeleteOrOverlapUnfinishedShift()
    {
        var shift = service.StartShift();
        clock.Advance(60);
        Assert.Throws<TrackerValidationException>(() => service.DeleteShift(shift.Id));
        Assert.Throws<TrackerValidationException>(() =>
            service.SaveShift(new(shift.Id, shift.StartedUtc, service.NowUtc, [])));
        Assert.Throws<TrackerValidationException>(() => Add(shift.StartedUtc, service.NowUtc));
    }

    [Fact]
    public void MidnightMonthAndYearBoundariesSplitWorkedSeconds()
    {
        // Bratislava midnight is 23:00 UTC in winter.
        Add(Utc(2026, 12, 31, 22), Utc(2027, 1, 1, 0));
        Assert.Equal(3600, service.GetMonthlyReport(2026, 12, Zone).WorkedSeconds);
        Assert.Equal(3600, service.GetMonthlyReport(2027, 1, Zone).WorkedSeconds);
    }

    [Fact]
    public void BreakAcrossMonthBoundaryIsClippedCorrectly()
    {
        Add(Utc(2026, 9, 30, 21), Utc(2026, 9, 30, 23),
            new BreakPeriod(Utc(2026, 9, 30, 21, 45), Utc(2026, 9, 30, 22, 15)));
        Assert.Equal(2700, service.GetMonthlyReport(2026, 9, Zone).WorkedSeconds);
        Assert.Equal(2700, service.GetMonthlyReport(2026, 10, Zone).WorkedSeconds);
    }

    [Fact]
    public void MovingShiftToAnotherMonthRefreshesBothMonths()
    {
        var shift = Add(Utc(2026, 9, 5, 7), Utc(2026, 9, 5, 8));
        service.SaveShift(new(shift.Id, Utc(2026, 10, 5, 7), Utc(2026, 10, 5, 8), []));
        Assert.Empty(service.GetMonthlyReport(2026, 9, Zone).Rows);
        Assert.Equal(3600, service.GetMonthlyReport(2026, 10, Zone).WorkedSeconds);
    }

    [Fact]
    public void DaylightSavingUsesActualElapsedSecondsAndRequiresOffsetWhenAmbiguous()
    {
        Add(Utc(2026, 3, 29, 0, 30), Utc(2026, 3, 29, 2, 30));
        Assert.Equal(7200, service.GetMonthlyReport(2026, 3, Zone).WorkedSeconds);
        Assert.Throws<TrackerValidationException>(() => LocalTimes.Resolve(new(2026, 3, 29, 2, 30, 0), Zone));
        var local = new DateTime(2026, 10, 25, 2, 30, 0);
        Assert.Throws<TrackerValidationException>(() => LocalTimes.Resolve(local, Zone));
        var early = LocalTimes.Resolve(local, Zone, TimeSpan.FromHours(2));
        var late = LocalTimes.Resolve(local, Zone, TimeSpan.FromHours(1));
        Assert.Equal(3600, TimeMath.Seconds(early, late));
    }

    [Fact]
    public void CsvIncludesExactSecondsRatesTotalsAndTimeZone()
    {
        Add(Utc(2026, 10, 5, 7), Utc(2026, 10, 5, 8, 2, 3));
        var csv = CsvExporter.Generate(service.GetMonthlyReport(2026, 10, Zone), Zone);
        Assert.Contains("\"01:02:03\"", csv);
        Assert.Contains("\"10.34\"", csv);
        Assert.Contains("\"10.00\"", csv);
        Assert.Contains("\"TOTAL\"", csv);
        Assert.Contains("2026-10-05 09:00:00 +02:00", csv);
    }

    [Fact]
    public void TotalsDoNotWrapAfterTwentyFourHours()
    {
        Add(Utc(2026, 10, 5, 7), Utc(2026, 10, 7, 7));
        Assert.Equal("48:00:00", TimeMath.Duration(service.GetMonthlyReport(2026, 10, Zone).WorkedSeconds));
    }

    [Fact]
    public void DatabaseConstraintAlsoPreventsDuplicateUnfinishedShifts()
    {
        service.StartShift();
        using var connection = new SqliteConnection($"Data Source={database.DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Shifts (Id, StartedUtc, HourlyRateCents, LastCheckpointUtc) SELECT $id, StartedUtc, HourlyRateCents, LastCheckpointUtc FROM Shifts LIMIT 1";
        command.Parameters.AddWithValue("$id", Guid.NewGuid());
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }

    [Fact]
    public void StorageErrorsDoNotOverwriteOrResetExistingDatabase()
    {
        var invalid = Path.Combine(directory, "invalid.db");
        File.WriteAllText(invalid, "Existing file must be preserved");
        Assert.Throws<SqliteException>(() => new TrackerDatabase(invalid).Initialize());
        Assert.Equal("Existing file must be preserved", File.ReadAllText(invalid));
    }

    public void Dispose()
    {
        // This unique directory is created exclusively by this test fixture.
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(int seconds) => now = now.AddSeconds(seconds);
    }
}
