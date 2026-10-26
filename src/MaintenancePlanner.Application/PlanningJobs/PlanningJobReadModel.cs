using System.Text.Json;
using MaintenancePlanner.Domain.Entities;

namespace MaintenancePlanner.Application.PlanningJobs;

public sealed record PlanningJobReadModel(
    Guid Id,
    PlanningJobStatus Status,
    int AttemptCount,
    int MaxAttempts,
    string CurrentStep,
    string? ResultPayload,
    string? ClarificationPayload,
    string? FailureCode,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? ErrorMessage,
    string RequestPayload = "",
    Guid? OriginalJobId = null);

public sealed record ClaimedPlanningJob(
    Guid Id,
    string RequestPayload,
    int AttemptCount,
    int MaxAttempts,
    int ConcurrencyVersion,
    Guid LeaseToken,
    string? CheckpointPayload);

public sealed record PlanningJobProcessingResult(string ResultPayload);

public sealed record PlanningJobRequestPayload(
    string? RequestText,
    IReadOnlyList<string?>? AssetIds,
    string? WindowId);

public sealed record PlanningJobResultEnvelope(
    string Outcome,
    JsonElement Result);

public sealed record PlanningJobCreationResult(
    PlanningJobReadModel Job,
    bool PayloadConflict);

public sealed record PlanningJobCheckpoint(
    string Step,
    JsonElement Data);

public sealed record PlanningJobInterpretationCheckpoint(
    string InterpretationPayload,
    DateTimeOffset SavedAtUtc);
