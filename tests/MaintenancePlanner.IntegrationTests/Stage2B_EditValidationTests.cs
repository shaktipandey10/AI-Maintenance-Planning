using System.Text.Json;
using MaintenancePlanner.Application.Edits;
using MaintenancePlanner.Application.PlanningJobs;
using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Edits;
using MaintenancePlanner.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MaintenancePlanner.IntegrationTests;

public sealed class Stage2B_EditValidationTests
{
    [Fact]
    public async Task ValidateEdits_ValidEdit_Succeeds()
    {
        // Use a real db context with sample data
        var (dbContext, queries) = await CreateTestContextWithData();
        var service = new EditValidationService(dbContext, queries);

        // Create setup: planning job + draft + valid edit
        var jobId = Guid.NewGuid();
        var draftId = Guid.NewGuid();
        var planningJob = new PlanningJob
        {
            Id = jobId,
            OperationId = Guid.NewGuid(),
            RequestPayload = JsonSerializer.Serialize(new { assetIds = new[] { "ASSET-001" }, windowId = "WINDOW-001" }),
            ResultPayload = "{}",
            Status = PlanningJobStatus.Succeeded,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await dbContext.PlanningJobs.AddAsync(planningJob);
        await dbContext.SaveChangesAsync();

        var draft = new Draft
        {
            Id = draftId,
            PlanningJobId = jobId,
            Status = DraftStatus.Approved,
            ApprovedAtUtc = DateTimeOffset.UtcNow,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await dbContext.Drafts.AddAsync(draft);
        await dbContext.SaveChangesAsync();

        // Add a valid edit (timing within window, valid technician with skills)
        var edit = new EditedTaskPlacement
        {
            Id = Guid.NewGuid(),
            DraftId = draftId,
            TaskId = "TASK-001",
            OriginalStart = new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.FromHours(5.5)),
            OriginalEnd = new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.FromHours(5.5)),
            OriginalTechnicianIds = JsonSerializer.Serialize(new[] { "TECH-001" }),
            NewStart = new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.FromHours(5.5)),
            NewEnd = new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.FromHours(5.5)),
            NewTechnicianIds = JsonSerializer.Serialize(new[] { "TECH-001" }),
            ValidationStatus = "pending",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await dbContext.EditedTaskPlacements.AddAsync(edit);
        await dbContext.SaveChangesAsync();

        // Act
        var result = await service.ValidateEditsAsync(draftId, CancellationToken.None);

        // Assert
        Assert.True(result.IsValid);
        Assert.Empty(result.Violations);
        Assert.Equal(1, result.ValidEditsCount);
        Assert.Equal(0, result.InvalidEditsCount);

        // Verify edit was updated
        var updatedEdit = await dbContext.EditedTaskPlacements.FirstOrDefaultAsync(e => e.Id == edit.Id);
        Assert.NotNull(updatedEdit);
        Assert.Equal("valid", updatedEdit.ValidationStatus);
        Assert.Null(updatedEdit.ValidationViolations);
    }

    [Fact]
    public async Task ValidateEdits_EditOutsideWindow_ReturnsViolation()
    {
        var (dbContext, queries) = await CreateTestContextWithData();
        var service = new EditValidationService(dbContext, queries);

        var jobId = Guid.NewGuid();
        var draftId = Guid.NewGuid();
        var planningJob = new PlanningJob
        {
            Id = jobId,
            OperationId = Guid.NewGuid(),
            RequestPayload = JsonSerializer.Serialize(new { assetIds = new[] { "ASSET-001" }, windowId = "WINDOW-001" }),
            ResultPayload = "{}",
            Status = PlanningJobStatus.Succeeded,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await dbContext.PlanningJobs.AddAsync(planningJob);
        await dbContext.SaveChangesAsync();

        var draft = new Draft
        {
            Id = draftId,
            PlanningJobId = jobId,
            Status = DraftStatus.Approved,
            ApprovedAtUtc = DateTimeOffset.UtcNow,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await dbContext.Drafts.AddAsync(draft);
        await dbContext.SaveChangesAsync();

        // Add edit with timing OUTSIDE window (window is 8:00-18:00, edit is 7:00-8:00)
        var edit = new EditedTaskPlacement
        {
            Id = Guid.NewGuid(),
            DraftId = draftId,
            TaskId = "TASK-001",
            OriginalStart = new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.FromHours(5.5)),
            OriginalEnd = new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.FromHours(5.5)),
            OriginalTechnicianIds = JsonSerializer.Serialize(new[] { "TECH-001" }),
            NewStart = new DateTimeOffset(2026, 10, 24, 7, 0, 0, TimeSpan.FromHours(5.5)), // Before window
            NewEnd = new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.FromHours(5.5)),
            NewTechnicianIds = JsonSerializer.Serialize(new[] { "TECH-001" }),
            ValidationStatus = "pending",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await dbContext.EditedTaskPlacements.AddAsync(edit);
        await dbContext.SaveChangesAsync();

        // Act
        var result = await service.ValidateEditsAsync(draftId, CancellationToken.None);

        // Assert
        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Violations);
        Assert.True(result.Violations.Any(v => v.Code == "WINDOW_EXCEEDED"));
        Assert.Equal(0, result.ValidEditsCount);
        Assert.Equal(1, result.InvalidEditsCount);
    }

    [Fact]
    public async Task ValidateEdits_TechnicianUnavailable_ReturnsViolation()
    {
        var (dbContext, queries) = await CreateTestContextWithData();
        var service = new EditValidationService(dbContext, queries);

        var jobId = Guid.NewGuid();
        var draftId = Guid.NewGuid();
        var planningJob = new PlanningJob
        {
            Id = jobId,
            OperationId = Guid.NewGuid(),
            RequestPayload = JsonSerializer.Serialize(new { assetIds = new[] { "ASSET-001" }, windowId = "WINDOW-001" }),
            ResultPayload = "{}",
            Status = PlanningJobStatus.Succeeded,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await dbContext.PlanningJobs.AddAsync(planningJob);
        await dbContext.SaveChangesAsync();

        var draft = new Draft
        {
            Id = draftId,
            PlanningJobId = jobId,
            Status = DraftStatus.Approved,
            ApprovedAtUtc = DateTimeOffset.UtcNow,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await dbContext.Drafts.AddAsync(draft);
        await dbContext.SaveChangesAsync();

        // Add edit with technician assigned outside their available time
        // TECH-001 is available 8:00-10:00, but edit requires 16:00-17:00
        var edit = new EditedTaskPlacement
        {
            Id = Guid.NewGuid(),
            DraftId = draftId,
            TaskId = "TASK-001",
            OriginalStart = new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.FromHours(5.5)),
            OriginalEnd = new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.FromHours(5.5)),
            OriginalTechnicianIds = JsonSerializer.Serialize(new[] { "TECH-001" }),
            NewStart = new DateTimeOffset(2026, 10, 24, 16, 0, 0, TimeSpan.FromHours(5.5)), // Outside availability
            NewEnd = new DateTimeOffset(2026, 10, 24, 17, 0, 0, TimeSpan.FromHours(5.5)),
            NewTechnicianIds = JsonSerializer.Serialize(new[] { "TECH-001" }),
            ValidationStatus = "pending",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await dbContext.EditedTaskPlacements.AddAsync(edit);
        await dbContext.SaveChangesAsync();

        // Act
        var result = await service.ValidateEditsAsync(draftId, CancellationToken.None);

        // Assert
        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Violations);
        Assert.True(result.Violations.Any(v => v.Code == "TECHNICIAN_UNAVAILABLE"));
    }

    [Fact]
    public async Task ValidateEdits_MissingTask_ReturnsViolation()
    {
        var (dbContext, queries) = await CreateTestContextWithData();
        var service = new EditValidationService(dbContext, queries);

        var jobId = Guid.NewGuid();
        var draftId = Guid.NewGuid();
        var planningJob = new PlanningJob
        {
            Id = jobId,
            OperationId = Guid.NewGuid(),
            RequestPayload = JsonSerializer.Serialize(new { assetIds = new[] { "ASSET-001" }, windowId = "WINDOW-001" }),
            ResultPayload = "{}",
            Status = PlanningJobStatus.Succeeded,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await dbContext.PlanningJobs.AddAsync(planningJob);
        await dbContext.SaveChangesAsync();

        var draft = new Draft
        {
            Id = draftId,
            PlanningJobId = jobId,
            Status = DraftStatus.Approved,
            ApprovedAtUtc = DateTimeOffset.UtcNow,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await dbContext.Drafts.AddAsync(draft);
        await dbContext.SaveChangesAsync();

        // Add edit for non-existent task
        var edit = new EditedTaskPlacement
        {
            Id = Guid.NewGuid(),
            DraftId = draftId,
            TaskId = "NONEXISTENT-TASK",
            OriginalStart = new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.FromHours(5.5)),
            OriginalEnd = new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.FromHours(5.5)),
            OriginalTechnicianIds = JsonSerializer.Serialize(new[] { "TECH-001" }),
            NewStart = new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.FromHours(5.5)),
            NewEnd = new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.FromHours(5.5)),
            NewTechnicianIds = JsonSerializer.Serialize(new[] { "TECH-001" }),
            ValidationStatus = "pending",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await dbContext.EditedTaskPlacements.AddAsync(edit);
        await dbContext.SaveChangesAsync();

        // Act
        var result = await service.ValidateEditsAsync(draftId, CancellationToken.None);

        // Assert
        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Violations);
        Assert.True(result.Violations.Any(v => v.Code == "TASK_NOT_FOUND"));
    }

    [Fact]
    public async Task ValidateEdits_NoEdits_ReturnsSuccess()
    {
        var (dbContext, queries) = await CreateTestContextWithData();
        var service = new EditValidationService(dbContext, queries);

        var jobId = Guid.NewGuid();
        var draftId = Guid.NewGuid();
        var planningJob = new PlanningJob
        {
            Id = jobId,
            OperationId = Guid.NewGuid(),
            RequestPayload = "{}",
            ResultPayload = "{}",
            Status = PlanningJobStatus.Succeeded,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await dbContext.PlanningJobs.AddAsync(planningJob);
        await dbContext.SaveChangesAsync();

        var draft = new Draft
        {
            Id = draftId,
            PlanningJobId = jobId,
            Status = DraftStatus.Approved,
            ApprovedAtUtc = DateTimeOffset.UtcNow,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await dbContext.Drafts.AddAsync(draft);
        await dbContext.SaveChangesAsync();

        // Act
        var result = await service.ValidateEditsAsync(draftId, CancellationToken.None);

        // Assert
        Assert.True(result.IsValid);
        Assert.Empty(result.Violations);
        Assert.Equal(0, result.ValidEditsCount);
        Assert.Equal(0, result.InvalidEditsCount);
    }

    [Fact]
    public async Task ValidateEdits_NonApprovedDraft_ThrowsException()
    {
        var (dbContext, queries) = await CreateTestContextWithData();
        var service = new EditValidationService(dbContext, queries);

        var jobId = Guid.NewGuid();
        var draftId = Guid.NewGuid();
        var planningJob = new PlanningJob
        {
            Id = jobId,
            OperationId = Guid.NewGuid(),
            RequestPayload = "{}",
            ResultPayload = "{}",
            Status = PlanningJobStatus.Succeeded,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await dbContext.PlanningJobs.AddAsync(planningJob);
        await dbContext.SaveChangesAsync();

        var draft = new Draft
        {
            Id = draftId,
            PlanningJobId = jobId,
            Status = DraftStatus.Pending, // Not approved
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await dbContext.Drafts.AddAsync(draft);
        await dbContext.SaveChangesAsync();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.ValidateEditsAsync(draftId, CancellationToken.None));
    }

    [Fact]
    public async Task ValidateEdits_PartUnavailable_ReturnsViolation()
    {
        var (dbContext, queries) = await CreateTestContextWithData();
        var service = new EditValidationService(dbContext, queries);

        var jobId = Guid.NewGuid();
        var draftId = Guid.NewGuid();
        var planningJob = new PlanningJob
        {
            Id = jobId,
            OperationId = Guid.NewGuid(),
            RequestPayload = JsonSerializer.Serialize(new { assetIds = new[] { "ASSET-001" }, windowId = "WINDOW-001" }),
            ResultPayload = "{}",
            Status = PlanningJobStatus.Succeeded,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await dbContext.PlanningJobs.AddAsync(planningJob);
        await dbContext.SaveChangesAsync();

        var draft = new Draft
        {
            Id = draftId,
            PlanningJobId = jobId,
            Status = DraftStatus.Approved,
            ApprovedAtUtc = DateTimeOffset.UtcNow,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await dbContext.Drafts.AddAsync(draft);
        await dbContext.SaveChangesAsync();

        // Add edit (TASK-002 requires PART-001 but we'll reduce availability)
        var edit = new EditedTaskPlacement
        {
            Id = Guid.NewGuid(),
            DraftId = draftId,
            TaskId = "TASK-002",
            OriginalStart = new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.FromHours(5.5)),
            OriginalEnd = new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.FromHours(5.5)),
            OriginalTechnicianIds = JsonSerializer.Serialize(new[] { "TECH-002" }),
            NewStart = new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.FromHours(5.5)),
            NewEnd = new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.FromHours(5.5)),
            NewTechnicianIds = JsonSerializer.Serialize(new[] { "TECH-002" }),
            ValidationStatus = "pending",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await dbContext.EditedTaskPlacements.AddAsync(edit);

        // Reduce spare part availability to trigger violation
        var part = dbContext.SpareParts.FirstOrDefault(p => p.Id == "PART-001");
        if (part != null)
        {
            part.ReservedQuantity = part.OnHandQuantity; // All reserved
            dbContext.SpareParts.Update(part);
        }

        await dbContext.SaveChangesAsync();

        // Act
        var result = await service.ValidateEditsAsync(draftId, CancellationToken.None);

        // Assert
        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Violations);
        Assert.True(result.Violations.Any(v => v.Code == "PART_UNAVAILABLE"));
    }

    [Fact]
    public async Task ValidateEdits_ApprovedScheduleUnchanged()
    {
        var (dbContext, queries) = await CreateTestContextWithData();
        var service = new EditValidationService(dbContext, queries);

        var jobId = Guid.NewGuid();
        var draftId = Guid.NewGuid();
        var planningJob = new PlanningJob
        {
            Id = jobId,
            OperationId = Guid.NewGuid(),
            RequestPayload = JsonSerializer.Serialize(new { assetIds = new[] { "ASSET-001" }, windowId = "WINDOW-001" }),
            ResultPayload = "{}",
            Status = PlanningJobStatus.Succeeded,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await dbContext.PlanningJobs.AddAsync(planningJob);
        await dbContext.SaveChangesAsync();

        var draft = new Draft
        {
            Id = draftId,
            PlanningJobId = jobId,
            Status = DraftStatus.Approved,
            ApprovedAtUtc = DateTimeOffset.UtcNow,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await dbContext.Drafts.AddAsync(draft);
        await dbContext.SaveChangesAsync();

        var edit = new EditedTaskPlacement
        {
            Id = Guid.NewGuid(),
            DraftId = draftId,
            TaskId = "TASK-001",
            OriginalStart = new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.FromHours(5.5)),
            OriginalEnd = new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.FromHours(5.5)),
            OriginalTechnicianIds = JsonSerializer.Serialize(new[] { "TECH-001" }),
            NewStart = new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.FromHours(5.5)),
            NewEnd = new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.FromHours(5.5)),
            NewTechnicianIds = JsonSerializer.Serialize(new[] { "TECH-001" }),
            ValidationStatus = "pending",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await dbContext.EditedTaskPlacements.AddAsync(edit);
        await dbContext.SaveChangesAsync();

        // Verify locked task assignment hasn't changed
        var locksBeforeValidation = await dbContext.LockedTaskAssignments
            .Where(l => l.DraftId == draftId)
            .ToListAsync();

        // Act
        await service.ValidateEditsAsync(draftId, CancellationToken.None);

        // Assert
        var locksAfterValidation = await dbContext.LockedTaskAssignments
            .Where(l => l.DraftId == draftId)
            .ToListAsync();

        Assert.Equal(locksBeforeValidation.Count, locksAfterValidation.Count);
        Assert.True(locksBeforeValidation.SequenceEqual(locksAfterValidation, new LockComparer()));
    }

    private static async Task<(MaintenancePlannerDbContext, IMaintenancePlannerQueries)> CreateTestContextWithData()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
            .UseSqlite(connection)
            .Options;

        var dbContext = new MaintenancePlannerDbContext(options);
        dbContext.Database.EnsureCreated();

        // Seed test data
        // Window: 8:00-18:00 on 2026-10-24
        var window = new MaintenanceWindow
        {
            Id = "WINDOW-001",
            Name = "Test Window",
            Start = new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.FromHours(5.5)),
            End = new DateTimeOffset(2026, 10, 24, 18, 0, 0, TimeSpan.FromHours(5.5)),
            TimeZoneId = "Asia/Kolkata"
        };
        await dbContext.MaintenanceWindows.AddAsync(window);

        // Asset
        var asset = new Asset { Id = "ASSET-001", PlantId = "PLANT-001", Name = "Test Asset", Model = "MODEL-01", Priority = 1 };
        await dbContext.Assets.AddAsync(asset);

        // Spare Part
        var part = new SparePart { Id = "PART-001", Name = "Test Part", Unit = "unit", OnHandQuantity = 10, ReservedQuantity = 0 };
        await dbContext.SpareParts.AddAsync(part);

        // Technicians with skills and availability
        var tech1 = new Technician { Id = "TECH-001", Name = "Tech 1", HourlyRate = 100 };
        var skill1 = new TechnicalSkill { TechnicianId = "TECH-001", SkillCode = "SKILL-A" };
        var avail1 = new TechnicianAvailability
        {
            TechnicianId = "TECH-001",
            AvailableFrom = new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.FromHours(5.5)),
            AvailableTo = new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.FromHours(5.5))
        };
        tech1.TechnicalSkills.Add(skill1);
        tech1.TechnicianAvailabilities.Add(avail1);
        await dbContext.Technicians.AddAsync(tech1);

        var tech2 = new Technician { Id = "TECH-002", Name = "Tech 2", HourlyRate = 120 };
        var skill2 = new TechnicalSkill { TechnicianId = "TECH-002", SkillCode = "SKILL-B" };
        var avail2 = new TechnicianAvailability
        {
            TechnicianId = "TECH-002",
            AvailableFrom = new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.FromHours(5.5)),
            AvailableTo = new DateTimeOffset(2026, 10, 24, 11, 0, 0, TimeSpan.FromHours(5.5))
        };
        tech2.TechnicalSkills.Add(skill2);
        tech2.TechnicianAvailabilities.Add(avail2);
        await dbContext.Technicians.AddAsync(tech2);

        // Task definitions
        var task1 = new MaintenanceTaskDefinition
        {
            Id = "TASK-001",
            AssetId = "ASSET-001",
            Name = "Task 1",
            DurationMinutes = 60,
            RequiredSkills = "SKILL-A",
            RequiredCrewSize = 1,
            RequiredPartId = null,
            RequiredPartQuantity = 0,
            PredecessorTaskId = null,
            SourceDocumentId = "DOC-001",
            SourceSection = "Section 1"
        };
        await dbContext.MaintenanceTaskDefinitions.AddAsync(task1);

        var task2 = new MaintenanceTaskDefinition
        {
            Id = "TASK-002",
            AssetId = "ASSET-001",
            Name = "Task 2",
            DurationMinutes = 60,
            RequiredSkills = "SKILL-B",
            RequiredCrewSize = 1,
            RequiredPartId = "PART-001",
            RequiredPartQuantity = 5,
            PredecessorTaskId = "TASK-001",
            SourceDocumentId = "DOC-001",
            SourceSection = "Section 2"
        };
        await dbContext.MaintenanceTaskDefinitions.AddAsync(task2);

        await dbContext.SaveChangesAsync();

        var queries = new TestMaintenancePlannerQueries(dbContext);
        return (dbContext, queries);
    }

    private class LockComparer : IEqualityComparer<LockedTaskAssignment>
    {
        public bool Equals(LockedTaskAssignment? x, LockedTaskAssignment? y)
        {
            if (x == null || y == null) return x == y;
            return x.Id == y.Id && x.DraftId == y.DraftId && x.TaskId == y.TaskId;
        }

        public int GetHashCode(LockedTaskAssignment obj) => obj.Id.GetHashCode();
    }
}

// Test implementation of IMaintenancePlannerQueries
internal sealed class TestMaintenancePlannerQueries(MaintenancePlannerDbContext dbContext) : IMaintenancePlannerQueries
{
    public Task<IReadOnlyList<AssetReadModel>> GetAssetsAsync(CancellationToken cancellationToken) =>
        Task.FromResult((IReadOnlyList<AssetReadModel>)dbContext.Assets
            .AsNoTracking()
            .Select(a => new AssetReadModel(a.Id, a.PlantId, a.Name, a.Model, a.Priority))
            .ToList());

    public Task<IReadOnlyList<TechnicianReadModel>> GetTechniciansAsync(CancellationToken cancellationToken) =>
        Task.FromResult((IReadOnlyList<TechnicianReadModel>)dbContext.Technicians
            .AsNoTracking()
            .Include(t => t.TechnicalSkills)
            .Include(t => t.TechnicianAvailabilities)
            .Select(t => new TechnicianReadModel(
                t.Id,
                t.Name,
                t.HourlyRate,
                t.TechnicalSkills.Select(s => s.SkillCode).ToList(),
                t.TechnicianAvailabilities.Select(a => new TechnicianAvailabilityReadModel(a.AvailableFrom, a.AvailableTo)).ToList()))
            .ToList());

    public Task<IReadOnlyList<SparePartReadModel>> GetSparePartsAsync(CancellationToken cancellationToken) =>
        Task.FromResult((IReadOnlyList<SparePartReadModel>)dbContext.SpareParts
            .AsNoTracking()
            .Select(p => new SparePartReadModel(
                p.Id,
                p.Name,
                p.Unit,
                p.OnHandQuantity,
                p.ReservedQuantity,
                p.OnHandQuantity - p.ReservedQuantity))
            .ToList());

    public Task<IReadOnlyList<MaintenanceWindowReadModel>> GetMaintenanceWindowsAsync(CancellationToken cancellationToken) =>
        Task.FromResult((IReadOnlyList<MaintenanceWindowReadModel>)dbContext.MaintenanceWindows
            .AsNoTracking()
            .Select(w => new MaintenanceWindowReadModel(w.Id, w.Name, w.Start, w.End, w.TimeZoneId))
            .ToList());

    public Task<IReadOnlyList<MaintenanceTaskReadModel>?> GetTaskCatalogueAsync(string assetId, CancellationToken cancellationToken) =>
        Task.FromResult((IReadOnlyList<MaintenanceTaskReadModel>?)dbContext.MaintenanceTaskDefinitions
            .AsNoTracking()
            .Where(t => t.AssetId == assetId)
            .Select(t => new MaintenanceTaskReadModel(
                t.Id,
                t.AssetId,
                t.Name,
                t.DurationMinutes,
                t.RequiredSkills,
                t.RequiredCrewSize,
                t.RequiredPartId,
                t.RequiredPartQuantity,
                t.PredecessorTaskId,
                t.SourceDocumentId,
                t.SourceSection))
            .ToList());

    public Task<IReadOnlyList<MaintenanceHistoryReadModel>> GetMaintenanceHistoryAsync(IReadOnlyCollection<string> assetIds, CancellationToken cancellationToken) =>
        Task.FromResult((IReadOnlyList<MaintenanceHistoryReadModel>)[]);

    public Task<IReadOnlyList<SparePartReadModel>> GetCompatibleSparePartsAsync(string assetId, CancellationToken cancellationToken) =>
        Task.FromResult((IReadOnlyList<SparePartReadModel>)[]);

    public Task<IReadOnlyList<LockedTaskAssignmentReadModel>> GetLockedTaskAssignmentsAsync(Guid draftId, CancellationToken cancellationToken) =>
        Task.FromResult((IReadOnlyList<LockedTaskAssignmentReadModel>)dbContext.LockedTaskAssignments
            .Where(l => l.DraftId == draftId)
            .Select(l => new LockedTaskAssignmentReadModel(l.TaskId, l.LockedTechnicianId))
            .ToList());

    public Task<IReadOnlyList<PlanningJobReadModel>> GetJobRevisionChainAsync(Guid jobId, CancellationToken cancellationToken) =>
        Task.FromResult((IReadOnlyList<PlanningJobReadModel>)[]);
}
