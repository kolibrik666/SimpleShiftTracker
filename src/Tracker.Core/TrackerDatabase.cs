using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Tracker.Core;

internal sealed class ShiftEntity
{
    public Guid Id { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? EndedUtc { get; set; }
    public long HourlyRateCents { get; set; }
    public DateTime LastCheckpointUtc { get; set; }
    public List<WorkIntervalEntity> WorkIntervals { get; set; } = [];

    public ShiftSnapshot Snapshot() => new(Id, StartedUtc, EndedUtc, HourlyRateCents,
        LastCheckpointUtc, WorkIntervals.OrderBy(p => p.StartedUtc)
            .Select(p => new WorkPeriod(p.StartedUtc, p.EndedUtc)).ToArray());
}

internal sealed class WorkIntervalEntity
{
    public long Id { get; set; }
    public Guid ShiftId { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? EndedUtc { get; set; }
}

internal sealed class SettingsEntity
{
    public int Id { get; set; } = 1;
    public long DefaultHourlyRateCents { get; set; } = 1000;
    public string TimeZoneId { get; set; } = "Europe/Bratislava";
}

internal sealed class TrackerDbContext(DbContextOptions<TrackerDbContext> options) : DbContext(options)
{
    public DbSet<ShiftEntity> Shifts => Set<ShiftEntity>();
    public DbSet<WorkIntervalEntity> WorkIntervals => Set<WorkIntervalEntity>();
    public DbSet<SettingsEntity> Settings => Set<SettingsEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureModel(modelBuilder);

    internal static void ConfigureModel(ModelBuilder modelBuilder)
    {
        var utc = new ValueConverter<DateTime, DateTime>(
            value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        var shift = modelBuilder.Entity<ShiftEntity>();
        shift.ToTable("Shifts", table =>
        {
            table.HasCheckConstraint("CK_Shifts_Rate", "HourlyRateCents > 0");
            table.HasCheckConstraint("CK_Shifts_Times", "EndedUtc IS NULL OR EndedUtc >= StartedUtc");
        });
        shift.HasKey(s => s.Id);
        shift.Property(s => s.StartedUtc).HasConversion(utc);
        shift.Property(s => s.EndedUtc).HasConversion(utc);
        shift.Property(s => s.LastCheckpointUtc).HasConversion(utc);
        shift.HasIndex(s => s.StartedUtc);
        shift.HasMany(s => s.WorkIntervals).WithOne().HasForeignKey(p => p.ShiftId)
            .OnDelete(DeleteBehavior.Cascade);

        var interval = modelBuilder.Entity<WorkIntervalEntity>();
        interval.ToTable("WorkIntervals", table =>
            table.HasCheckConstraint("CK_Intervals_Times", "EndedUtc IS NULL OR EndedUtc >= StartedUtc"));
        interval.HasKey(p => p.Id);
        interval.Property(p => p.StartedUtc).HasConversion(utc);
        interval.Property(p => p.EndedUtc).HasConversion(utc);
        interval.HasIndex(p => p.ShiftId).IsUnique().HasFilter("EndedUtc IS NULL")
            .HasDatabaseName("IX_WorkIntervals_OneOpen");

        modelBuilder.Entity<SettingsEntity>().ToTable("Settings", table =>
            table.HasCheckConstraint("CK_Settings_Rate", "DefaultHourlyRateCents > 0"));
        modelBuilder.Entity<SettingsEntity>().HasKey(s => s.Id);
    }
}

public sealed class TrackerDatabase
{
    private readonly DbContextOptions<TrackerDbContext> options;
    public string DatabasePath { get; }

    public TrackerDatabase(string databasePath)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        options = new DbContextOptionsBuilder<TrackerDbContext>()
            .UseSqlite(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                ForeignKeys = true,
                DefaultTimeout = 5,
                Pooling = false
            }.ToString()).Options;
    }

    internal TrackerDbContext Open() => new(options);

    public void Initialize(bool hasExclusiveStartupLock = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        using var db = Open();
        // EF's SQLite migration lock survives process termination. The desktop host
        // may clear it only after acquiring its exclusive instance mutex.
        if (hasExclusiveStartupLock && File.Exists(DatabasePath))
        {
            db.Database.OpenConnection();
            using var lookup = db.Database.GetDbConnection().CreateCommand();
            lookup.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '__EFMigrationsLock'";
            if (Convert.ToInt64(lookup.ExecuteScalar()) > 0)
            {
                using var clearLock = db.Database.GetDbConnection().CreateCommand();
                clearLock.CommandText = "DELETE FROM __EFMigrationsLock";
                clearLock.ExecuteNonQuery();
            }
            db.Database.CloseConnection();
        }
        db.Database.Migrate();
        if (!db.Settings.Any())
        {
            db.Settings.Add(new SettingsEntity());
            db.SaveChanges();
        }
    }
}
