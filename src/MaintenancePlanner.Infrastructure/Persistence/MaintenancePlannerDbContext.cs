using MaintenancePlanner.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MaintenancePlanner.Infrastructure.Persistence;

public sealed class MaintenancePlannerDbContext(DbContextOptions<MaintenancePlannerDbContext> options)
    : DbContext(options)
{
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<SparePart> SpareParts => Set<SparePart>();
    public DbSet<MaintenanceHistoryRecord> MaintenanceHistoryRecords => Set<MaintenanceHistoryRecord>();
    public DbSet<MaintenanceTaskDefinition> MaintenanceTaskDefinitions => Set<MaintenanceTaskDefinition>();
    public DbSet<Technician> Technicians => Set<Technician>();
    public DbSet<TechnicalSkill> TechnicalSkills => Set<TechnicalSkill>();
    public DbSet<TechnicianAvailability> TechnicianAvailabilities => Set<TechnicianAvailability>();
    public DbSet<MaintenanceWindow> MaintenanceWindows => Set<MaintenanceWindow>();
    public DbSet<PlanningJob> PlanningJobs => Set<PlanningJob>();
    public DbSet<Draft> Drafts => Set<Draft>();
    public DbSet<Reservation> Reservations => Set<Reservation>();
    public DbSet<LockedTaskAssignment> LockedTaskAssignments => Set<LockedTaskAssignment>();
    public DbSet<EditedTaskPlacement> EditedTaskPlacements => Set<EditedTaskPlacement>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Asset>().HasKey(asset => asset.Id);
        modelBuilder.Entity<SparePart>().HasKey(part => part.Id);
        modelBuilder.Entity<MaintenanceWindow>().HasKey(window => window.Id);
        modelBuilder.Entity<PlanningJob>().HasKey(job => job.Id);
        modelBuilder.Entity<PlanningJob>()
            .Property(job => job.ConcurrencyVersion)
            .HasDefaultValue(0)
            .IsConcurrencyToken();
        modelBuilder.Entity<PlanningJob>().Property(job => job.Status).HasConversion<string>();
        modelBuilder.Entity<PlanningJob>().Property(job => job.AttemptCount).HasDefaultValue(0);
        modelBuilder.Entity<PlanningJob>().Property(job => job.MaxAttempts).HasDefaultValue(2);
        modelBuilder.Entity<PlanningJob>().Property(job => job.CurrentStep).HasDefaultValue("Queued");
        modelBuilder.Entity<PlanningJob>().HasIndex(job => job.OperationId).IsUnique();
        modelBuilder.Entity<PlanningJob>()
            .HasIndex(job => new { job.Status, job.CreatedAtUtc });

        modelBuilder.Entity<SparePart>()
            .HasMany(part => part.CompatibleAssets)
            .WithMany()
            .UsingEntity<Dictionary<string, object>>(
                "SparePartAssetCompatibility",
                asset => asset.HasOne<Asset>().WithMany()
                    .HasForeignKey("AssetId")
                    .OnDelete(DeleteBehavior.Restrict),
                part => part.HasOne<SparePart>().WithMany()
                    .HasForeignKey("SparePartId")
                    .OnDelete(DeleteBehavior.Restrict),
                join =>
                {
                    join.ToTable("SparePartAssetCompatibility");
                    join.HasKey("SparePartId", "AssetId");
                    join.HasIndex("AssetId", "SparePartId");
                });

        modelBuilder.Entity<MaintenanceHistoryRecord>().HasKey(record => record.Id);
        modelBuilder.Entity<MaintenanceHistoryRecord>()
            .HasOne(record => record.Asset)
            .WithMany(asset => asset.MaintenanceHistoryRecords)
            .HasForeignKey(record => record.AssetId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<MaintenanceHistoryRecord>()
            .HasIndex(record => new { record.AssetId, record.PerformedAt });

        modelBuilder.Entity<Technician>().HasKey(technician => technician.Id);
        modelBuilder.Entity<Technician>().HasMany(technician => technician.TechnicalSkills)
            .WithOne(skill => skill.Technician)
            .HasForeignKey(skill => skill.TechnicianId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Technician>().HasMany(technician => technician.TechnicianAvailabilities)
            .WithOne(availability => availability.Technician)
            .HasForeignKey(availability => availability.TechnicianId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<MaintenanceTaskDefinition>().HasKey(task => task.Id);
        modelBuilder.Entity<MaintenanceTaskDefinition>()
            .HasOne<Asset>()
            .WithMany()
            .HasForeignKey(task => task.AssetId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<MaintenanceTaskDefinition>()
            .HasOne<SparePart>()
            .WithMany()
            .HasForeignKey(task => task.RequiredPartId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<MaintenanceTaskDefinition>()
            .HasOne<MaintenanceTaskDefinition>()
            .WithMany()
            .HasForeignKey(task => task.PredecessorTaskId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Draft>().HasKey(draft => draft.Id);
        modelBuilder.Entity<Draft>()
            .HasOne(draft => draft.PlanningJob)
            .WithMany()
            .HasForeignKey(draft => draft.PlanningJobId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Draft>()
            .HasMany(draft => draft.Reservations)
            .WithOne(reservation => reservation.Draft)
            .HasForeignKey(reservation => reservation.DraftId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Draft>().Property(draft => draft.Status).HasConversion<string>();
        modelBuilder.Entity<Draft>()
            .HasIndex(draft => draft.PlanningJobId)
            .IsUnique();

        modelBuilder.Entity<LockedTaskAssignment>().HasKey(lock_ => lock_.Id);
        modelBuilder.Entity<LockedTaskAssignment>()
            .HasOne(lock_ => lock_.Draft)
            .WithMany()
            .HasForeignKey(lock_ => lock_.DraftId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<LockedTaskAssignment>()
            .HasIndex(lock_ => new { lock_.DraftId, lock_.TaskId })
            .IsUnique();

        modelBuilder.Entity<EditedTaskPlacement>().HasKey(edit => edit.Id);
        modelBuilder.Entity<EditedTaskPlacement>()
            .HasOne(edit => edit.Draft)
            .WithMany(draft => draft.EditedTaskPlacements)
            .HasForeignKey(edit => edit.DraftId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<EditedTaskPlacement>()
            .HasIndex(edit => new { edit.DraftId, edit.TaskId })
            .IsUnique();

        modelBuilder.Entity<Reservation>().HasKey(reservation => reservation.Id);
        modelBuilder.Entity<Reservation>()
            .HasOne(reservation => reservation.SparePart)
            .WithMany()
            .HasForeignKey(reservation => reservation.SparePartId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Reservation>().Property(reservation => reservation.Status).HasConversion<string>();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        IncrementModifiedPlanningJobVersions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        IncrementModifiedPlanningJobVersions();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void IncrementModifiedPlanningJobVersions()
    {
        foreach (var entry in ChangeTracker.Entries<PlanningJob>()
                     .Where(entry => entry.State == EntityState.Modified))
        {
            var version = entry.Property(job => job.ConcurrencyVersion);
            version.CurrentValue = checked(version.OriginalValue + 1);
        }
    }
}