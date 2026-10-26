using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Persistence;
using MaintenancePlanner.Infrastructure.Seeding;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MaintenancePlanner.IntegrationTests;

public sealed class EditedTaskPlacementPersistenceTests
{
    [Fact]
    public async Task EditedTaskPlacement_CanBeCreatedAndRetrievedInSameContext()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var draftId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        // Create planning job first (required by FK)
        var job = new PlanningJob
        {
            Id = jobId,
            OperationId = Guid.NewGuid(),
            RequestPayload = "{}",
            Status = Domain.Entities.PlanningJobStatus.Succeeded,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await dbContext.PlanningJobs.AddAsync(job);
        await dbContext.SaveChangesAsync();

        // Create draft
        var draft = new Draft
        {
            Id = draftId,
            PlanningJobId = jobId,
            Status = DraftStatus.Pending,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await dbContext.Drafts.AddAsync(draft);
        await dbContext.SaveChangesAsync();

        // Create edited task placement
        var edit = new EditedTaskPlacement
        {
            Id = Guid.NewGuid(),
            DraftId = draftId,
            TaskId = "TEST-TASK-001",
            OriginalStart = new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.FromHours(5.5)),
            OriginalEnd = new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.FromHours(5.5)),
            OriginalTechnicianIds = "[\"TECH-001\"]",
            NewStart = new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.FromHours(5.5)),
            NewEnd = new DateTimeOffset(2026, 10, 24, 9, 30, 0, TimeSpan.FromHours(5.5)),
            NewTechnicianIds = "[\"TECH-001\",\"TECH-002\"]",
            ValidationStatus = "pending",
            ValidationViolations = null,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        await dbContext.EditedTaskPlacements.AddAsync(edit);
        await dbContext.SaveChangesAsync();

        // Retrieve and verify from same context
        var retrieved = await dbContext.EditedTaskPlacements
            .Where(e => e.DraftId == draftId && e.TaskId == "TEST-TASK-001")
            .FirstOrDefaultAsync();

        Assert.NotNull(retrieved);
        Assert.Equal(draftId, retrieved.DraftId);
        Assert.Equal("TEST-TASK-001", retrieved.TaskId);
        Assert.Equal("[\"TECH-001\",\"TECH-002\"]", retrieved.NewTechnicianIds);
        Assert.Equal("pending", retrieved.ValidationStatus);
    }

    [Fact]
    public async Task EditedTaskPlacement_CompositeUniqueIndex_ConstrainsTaskPerDraft()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var draftId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        // Create planning job first (required by FK)
        var job = new PlanningJob
        {
            Id = jobId,
            OperationId = Guid.NewGuid(),
            RequestPayload = "{}",
            Status = Domain.Entities.PlanningJobStatus.Succeeded,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await dbContext.PlanningJobs.AddAsync(job);
        await dbContext.SaveChangesAsync();

        // Create draft
        var draft = new Draft
        {
            Id = draftId,
            PlanningJobId = jobId,
            Status = DraftStatus.Pending,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await dbContext.Drafts.AddAsync(draft);
        await dbContext.SaveChangesAsync();

        // Create first edit
        var edit1 = new EditedTaskPlacement
        {
            Id = Guid.NewGuid(),
            DraftId = draftId,
            TaskId = "TEST-TASK-001",
            OriginalStart = DateTimeOffset.UtcNow,
            OriginalEnd = DateTimeOffset.UtcNow.AddHours(1),
            OriginalTechnicianIds = "[]",
            NewStart = DateTimeOffset.UtcNow,
            NewEnd = DateTimeOffset.UtcNow.AddHours(1),
            NewTechnicianIds = "[]",
            ValidationStatus = "pending",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await dbContext.EditedTaskPlacements.AddAsync(edit1);
        await dbContext.SaveChangesAsync();

        // Attempt to create duplicate (same draftId + taskId)
        var edit2 = new EditedTaskPlacement
        {
            Id = Guid.NewGuid(),
            DraftId = draftId,
            TaskId = "TEST-TASK-001", // Same task in same draft
            OriginalStart = DateTimeOffset.UtcNow,
            OriginalEnd = DateTimeOffset.UtcNow.AddHours(1),
            OriginalTechnicianIds = "[]",
            NewStart = DateTimeOffset.UtcNow,
            NewEnd = DateTimeOffset.UtcNow.AddHours(2),
            NewTechnicianIds = "[]",
            ValidationStatus = "valid",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await dbContext.EditedTaskPlacements.AddAsync(edit2);

        // Expect constraint violation
        var exception = await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await dbContext.SaveChangesAsync());
        Assert.NotNull(exception);
    }

    [Fact]
    public async Task EditedTaskPlacement_CascadeDelete_OnDraftDelete()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var draftId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        // Create planning job first (required by FK)
        var job = new PlanningJob
        {
            Id = jobId,
            OperationId = Guid.NewGuid(),
            RequestPayload = "{}",
            Status = Domain.Entities.PlanningJobStatus.Succeeded,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await dbContext.PlanningJobs.AddAsync(job);
        await dbContext.SaveChangesAsync();

        // Create draft with edit
        var draft = new Draft
        {
            Id = draftId,
            PlanningJobId = jobId,
            Status = DraftStatus.Pending,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await dbContext.Drafts.AddAsync(draft);
        await dbContext.SaveChangesAsync();

        var edit = new EditedTaskPlacement
        {
            Id = Guid.NewGuid(),
            DraftId = draftId,
            TaskId = "TEST-TASK-001",
            OriginalStart = DateTimeOffset.UtcNow,
            OriginalEnd = DateTimeOffset.UtcNow.AddHours(1),
            OriginalTechnicianIds = "[]",
            NewStart = DateTimeOffset.UtcNow,
            NewEnd = DateTimeOffset.UtcNow.AddHours(1),
            NewTechnicianIds = "[]",
            ValidationStatus = "pending",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await dbContext.EditedTaskPlacements.AddAsync(edit);
        await dbContext.SaveChangesAsync();

        // Delete draft
        dbContext.Drafts.Remove(draft);
        await dbContext.SaveChangesAsync();

        // Verify edit is also deleted (cascade) within same context
        var editsForDraft = await dbContext.EditedTaskPlacements
            .Where(e => e.DraftId == draftId)
            .CountAsync();

        Assert.Equal(0, editsForDraft);
    }

    [Fact]
    public async Task EditedTaskPlacement_UpdatedAtUtc_TracksModifications()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var draftId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        // Create planning job first (required by FK)
        var job = new PlanningJob
        {
            Id = jobId,
            OperationId = Guid.NewGuid(),
            RequestPayload = "{}",
            Status = Domain.Entities.PlanningJobStatus.Succeeded,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await dbContext.PlanningJobs.AddAsync(job);
        await dbContext.SaveChangesAsync();

        // Create draft
        var draft = new Draft
        {
            Id = draftId,
            PlanningJobId = jobId,
            Status = DraftStatus.Pending,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await dbContext.Drafts.AddAsync(draft);
        await dbContext.SaveChangesAsync();

        // Create edit
        var createdTime = DateTimeOffset.UtcNow;
        var editId = Guid.NewGuid();
        var edit = new EditedTaskPlacement
        {
            Id = editId,
            DraftId = draftId,
            TaskId = "TEST-TASK-001",
            OriginalStart = DateTimeOffset.UtcNow,
            OriginalEnd = DateTimeOffset.UtcNow.AddHours(1),
            OriginalTechnicianIds = "[]",
            NewStart = DateTimeOffset.UtcNow,
            NewEnd = DateTimeOffset.UtcNow.AddHours(1),
            NewTechnicianIds = "[]",
            ValidationStatus = "pending",
            CreatedAtUtc = createdTime,
            UpdatedAtUtc = null
        };
        await dbContext.EditedTaskPlacements.AddAsync(edit);
        await dbContext.SaveChangesAsync();

        // Update validation status within same context
        var retrieved = await dbContext.EditedTaskPlacements
            .Where(e => e.Id == editId)
            .FirstOrDefaultAsync();

        Assert.NotNull(retrieved);
        retrieved.ValidationStatus = "valid";
        retrieved.UpdatedAtUtc = DateTimeOffset.UtcNow.AddMilliseconds(10);
        dbContext.EditedTaskPlacements.Update(retrieved);
        await dbContext.SaveChangesAsync();

        // Verify timestamps from same context
        var final = await dbContext.EditedTaskPlacements
            .Where(e => e.Id == editId)
            .FirstOrDefaultAsync();

        Assert.NotNull(final);
        Assert.Equal(createdTime, final.CreatedAtUtc);
        Assert.NotNull(final.UpdatedAtUtc);
        Assert.True(final.UpdatedAtUtc > final.CreatedAtUtc);
    }

    private static MaintenancePlannerDbContext CreateInMemoryDbContext()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
            .UseSqlite(connection)
            .Options;

        var dbContext = new MaintenancePlannerDbContext(options);
        dbContext.Database.EnsureCreated();

        return dbContext;
    }
}
