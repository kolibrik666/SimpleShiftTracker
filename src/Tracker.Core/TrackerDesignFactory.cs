using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Tracker.Core;

internal sealed class TrackerDesignFactory : IDesignTimeDbContextFactory<TrackerDbContext>
{
    public TrackerDesignFactory() { }
    public TrackerDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<TrackerDbContext>().UseSqlite("Data Source=tracker-design.db").Options);
}
