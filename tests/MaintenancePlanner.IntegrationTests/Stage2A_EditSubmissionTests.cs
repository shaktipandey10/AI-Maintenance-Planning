using System.Text.Json;
using MaintenancePlanner.Application.Edits;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Edits;
using MaintenancePlanner.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using AppTaskEditRequest = MaintenancePlanner.Application.Edits.TaskEditRequest;
using AppTaskEditsSubmissionRequest = MaintenancePlanner.Application.Edits.TaskEditsSubmissionRequest;

namespace MaintenancePlanner.IntegrationTests;

public sealed class Stage2A_EditSubmissionTests
{
    [Fact]
    public async Task SubmitEdit_ToApprovedDraft_ReturnsSuccess()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var service = new TaskEditorService(dbContext);

        // Setup: Create planning job with candidate
        var jobId = Guid.NewGuid();
        var draftId = Guid.NewGuid();
        var planningJob = new PlanningJob
        {
            Id = jobId,
            OperationId = Guid.NewGuid(),
            RequestPayload = "{}",
            ResultPayload = CreateMockResultPayload(),
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
        var request = new AppTaskEditsSubmissionRequest([
            new AppTaskEditRequest("TASK-001",
                new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.FromHours(5.5)),
                new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.FromHours(5.5)),
                ["TECH-002"])
        ]);

        var result = await service.SubmitEditsAsync(draftId, request, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(draftId, result.DraftId);
        Assert.Single(result.EditedTasks);
        Assert.Equal("TASK-001", result.EditedTasks[0].TaskId);
        Assert.Equal("pending", result.EditedTasks[0].ValidationStatus);
    }

    [Fact]
    public async Task SubmitEdit_ToNonApprovedDraft_ThrowsInvalidOperation()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var service = new TaskEditorService(dbContext);

        var jobId = Guid.NewGuid();
        var draftId = Guid.NewGuid();
        var planningJob = new PlanningJob
        {
            Id = jobId,
            OperationId = Guid.NewGuid(),
            RequestPayload = "{}",
            ResultPayload = CreateMockResultPayload(),
            Status = PlanningJobStatus.Succeeded,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await dbContext.PlanningJobs.AddAsync(planningJob);
        await dbContext.SaveChangesAsync();

        var draft = new Draft
        {
            Id = draftId,
            PlanningJobId = jobId,
            Status = DraftStatus.Pending,  // Not approved
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await dbContext.Drafts.AddAsync(draft);
        await dbContext.SaveChangesAsync();

        var request = new AppTaskEditsSubmissionRequest([
            new AppTaskEditRequest("TASK-001",
                DateTimeOffset.UtcNow.AddHours(1),
                DateTimeOffset.UtcNow.AddHours(2),
                ["TECH-002"])
        ]);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.SubmitEditsAsync(draftId, request, CancellationToken.None));
    }

    [Fact]
    public async Task SubmitEdit_ToNonExistentDraft_ThrowsInvalidOperation()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var service = new TaskEditorService(dbContext);

        var request = new AppTaskEditsSubmissionRequest([
            new AppTaskEditRequest("TASK-001",
                DateTimeOffset.UtcNow.AddHours(1),
                DateTimeOffset.UtcNow.AddHours(2),
                ["TECH-002"])
        ]);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.SubmitEditsAsync(Guid.NewGuid(), request, CancellationToken.None));
    }

    [Fact]
    public async Task GetEdits_WithExistingEdits_ReturnsAll()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var service = new TaskEditorService(dbContext);

        var draftId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        // Setup
        var planningJob = new PlanningJob
        {
            Id = jobId,
            OperationId = Guid.NewGuid(),
            RequestPayload = "{}",
            ResultPayload = CreateMockResultPayload(),
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

        // Submit 2 edits
        var request = new AppTaskEditsSubmissionRequest([
            new AppTaskEditRequest("TASK-001",
                new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.FromHours(5.5)),
                new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.FromHours(5.5)),
                ["TECH-002"]),
            new AppTaskEditRequest("TASK-002",
                new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.FromHours(5.5)),
                new DateTimeOffset(2026, 10, 24, 11, 0, 0, TimeSpan.FromHours(5.5)),
                ["TECH-001"])
        ]);
        await service.SubmitEditsAsync(draftId, request, CancellationToken.None);

        // Act
        var edits = await service.GetEditsAsync(draftId, CancellationToken.None);

        // Assert
        Assert.Equal(2, edits.Count);
        Assert.Contains(edits, e => e.TaskId == "TASK-001");
        Assert.Contains(edits, e => e.TaskId == "TASK-002");
    }

    [Fact]
    public async Task GetEdits_NoEdits_ReturnsEmpty()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var service = new TaskEditorService(dbContext);

        var draftId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        var planningJob = new PlanningJob
        {
            Id = jobId,
            OperationId = Guid.NewGuid(),
            RequestPayload = "{}",
            ResultPayload = CreateMockResultPayload(),
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
        var edits = await service.GetEditsAsync(draftId, CancellationToken.None);

        // Assert
        Assert.Empty(edits);
    }

    [Fact]
    public async Task DeleteEdit_ForSingleTask_RemovesOnlyThatEdit()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var service = new TaskEditorService(dbContext);

        var draftId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        // Setup
        var planningJob = new PlanningJob
        {
            Id = jobId,
            OperationId = Guid.NewGuid(),
            RequestPayload = "{}",
            ResultPayload = CreateMockResultPayload(),
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

        // Submit 2 edits
        var request = new AppTaskEditsSubmissionRequest([
            new AppTaskEditRequest("TASK-001",
                new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.FromHours(5.5)),
                new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.FromHours(5.5)),
                ["TECH-002"]),
            new AppTaskEditRequest("TASK-002",
                new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.FromHours(5.5)),
                new DateTimeOffset(2026, 10, 24, 11, 0, 0, TimeSpan.FromHours(5.5)),
                ["TECH-001"])
        ]);
        await service.SubmitEditsAsync(draftId, request, CancellationToken.None);

        // Act
        var deleted = await service.DeleteEditAsync(draftId, "TASK-001", CancellationToken.None);

        // Assert
        Assert.True(deleted);
        var remaining = await service.GetEditsAsync(draftId, CancellationToken.None);
        Assert.Single(remaining);
        Assert.Equal("TASK-002", remaining[0].TaskId);
    }

    [Fact]
    public async Task DeleteEdit_NonExistent_ReturnsFalse()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var service = new TaskEditorService(dbContext);

        var draftId = Guid.NewGuid();

        var deleted = await service.DeleteEditAsync(draftId, "NONEXISTENT", CancellationToken.None);

        Assert.False(deleted);
    }

    [Fact]
    public async Task ClearEdits_NonExistentDraft_ReturnsFalse()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var service = new TaskEditorService(dbContext);

        // Act: Try to clear edits for non-existent draft
        var cleared = await service.ClearEditsAsync(Guid.NewGuid(), CancellationToken.None);

        // Assert: Returns false (no edits to clear)
        Assert.False(cleared);
    }

    [Fact]
    public async Task ClearEdits_WithEdits_RemovesAll()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var service = new TaskEditorService(dbContext);

        var draftId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        // Setup
        var planningJob = new PlanningJob
        {
            Id = jobId,
            OperationId = Guid.NewGuid(),
            RequestPayload = "{}",
            ResultPayload = CreateMockResultPayload(),
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

        // Submit edits
        var request = new AppTaskEditsSubmissionRequest([
            new AppTaskEditRequest("TASK-001",
                new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.FromHours(5.5)),
                new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.FromHours(5.5)),
                ["TECH-002"])
        ]);
        await service.SubmitEditsAsync(draftId, request, CancellationToken.None);

        // Act
        var cleared = await service.ClearEditsAsync(draftId, CancellationToken.None);

        // Assert
        Assert.True(cleared);
        var remaining = await service.GetEditsAsync(draftId, CancellationToken.None);
        Assert.Empty(remaining);
    }

    [Fact]
    public async Task SubmitEdit_WithExistingEdit_UpdatesExisting()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var service = new TaskEditorService(dbContext);

        var draftId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        // Setup
        var planningJob = new PlanningJob
        {
            Id = jobId,
            OperationId = Guid.NewGuid(),
            RequestPayload = "{}",
            ResultPayload = CreateMockResultPayload(),
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

        // Submit first edit
        var firstRequest = new AppTaskEditsSubmissionRequest([
            new AppTaskEditRequest("TASK-001",
                new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.FromHours(5.5)),
                new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.FromHours(5.5)),
                ["TECH-002"])
        ]);
        var firstResult = await service.SubmitEditsAsync(draftId, firstRequest, CancellationToken.None);
        var createdAtUtc = firstResult.EditedTasks[0].NewStart;

        // Act: Submit same task with different times (idempotent update)
        var secondRequest = new AppTaskEditsSubmissionRequest([
            new AppTaskEditRequest("TASK-001",
                new DateTimeOffset(2026, 10, 24, 9, 30, 0, TimeSpan.FromHours(5.5)),
                new DateTimeOffset(2026, 10, 24, 10, 30, 0, TimeSpan.FromHours(5.5)),
                ["TECH-001", "TECH-002"])
        ]);
        var secondResult = await service.SubmitEditsAsync(draftId, secondRequest, CancellationToken.None);

        // Assert: Only one edit exists with updated values
        Assert.Single(secondResult.EditedTasks);
        Assert.Equal("TASK-001", secondResult.EditedTasks[0].TaskId);
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 9, 30, 0, TimeSpan.FromHours(5.5)),
            secondResult.EditedTasks[0].NewStart);
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 10, 30, 0, TimeSpan.FromHours(5.5)),
            secondResult.EditedTasks[0].NewEnd);
        Assert.Equal(2, secondResult.EditedTasks[0].NewTechnicianIds.Count);
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

    private static string CreateMockResultPayload()
    {
        var result = new
        {
            outcome = "CandidatesGenerated",
            result = new
            {
                candidates = new[]
                {
                    new
                    {
                        taskPlacements = new[]
                        {
                            new
                            {
                                taskId = "TASK-001",
                                start = "2026-10-24T08:00:00+05:30",
                                end = "2026-10-24T09:00:00+05:30",
                                technicianIds = new[] { "TECH-001" }
                            },
                            new
                            {
                                taskId = "TASK-002",
                                start = "2026-10-24T09:00:00+05:30",
                                end = "2026-10-24T10:00:00+05:30",
                                technicianIds = new[] { "TECH-001" }
                            }
                        },
                        rationale = "Test candidate",
                        requestedTaskIds = new[] { "TASK-001", "TASK-002" },
                        dependencyTaskIds = new string[] { },
                        evidenceReferences = new string[] { },
                        totalLabourCostAmount = 100m,
                        totalLabourCostCurrency = "USD",
                        completionTimeMinutes = 120,
                        deferralReasons = new object[] { },
                        isLowestCostCandidate = true,
                        costComparabilityWarning = (string?)null
                    }
                }
            }
        };

        return JsonSerializer.Serialize(result);
    }
}
