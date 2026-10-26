using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MaintenancePlanner.Infrastructure.Persistence;

public sealed class MaintenancePlannerDbContextFactory : IDesignTimeDbContextFactory<MaintenancePlannerDbContext>
{
    public MaintenancePlannerDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        return new MaintenancePlannerDbContext(options);
    }
}