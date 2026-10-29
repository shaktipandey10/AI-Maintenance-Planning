using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MaintenancePlanner.Application.Planning;
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

/// <summary>
/// The durable checkpoint stored in PlanningJob.CheckpointPayload once the request interpretation has passed every
/// interpretation-stage check. It belongs to one job and to the exact request payload that was interpreted, and is
/// only used after <see cref="TryRead"/> has validated it.
/// </summary>
public sealed record PlanningInterpretationCheckpoint(
    int SchemaVersion,
    Guid JobId,
    string Stage,
    string RequestFingerprint,
    PlanningInterpretation Interpretation,
    DateTimeOffset SavedAtUtc)
{
    public const int CurrentSchemaVersion = 1;
    public const string InterpretationValidatedStage = "InterpretationValidated";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string FingerprintRequest(string requestPayload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestPayload)));

    public static string Serialize(
        Guid jobId,
        string requestPayload,
        PlanningInterpretation interpretation,
        DateTimeOffset savedAtUtc) =>
        JsonSerializer.Serialize(
            new PlanningInterpretationCheckpoint(
                CurrentSchemaVersion,
                jobId,
                InterpretationValidatedStage,
                FingerprintRequest(requestPayload),
                interpretation,
                savedAtUtc),
            JsonOptions);

    /// <summary>
    /// Reads a checkpoint for <paramref name="jobId"/> and its <paramref name="requestPayload"/>. Returns false with a
    /// reason when the payload is malformed, has an unsupported schema or stage, belongs to another job or request,
    /// or does not hold a complete, resolved interpretation.
    /// </summary>
    public static bool TryRead(
        string checkpointPayload,
        Guid jobId,
        string requestPayload,
        [NotNullWhen(true)] out PlanningInterpretation? interpretation,
        out string error)
    {
        interpretation = null;
        PlanningInterpretationCheckpoint? checkpoint;
        try
        {
            checkpoint = JsonSerializer.Deserialize<PlanningInterpretationCheckpoint>(checkpointPayload, JsonOptions);
        }
        catch (JsonException exception)
        {
            error = $"the checkpoint is not valid checkpoint JSON ({exception.Message})";
            return false;
        }

        if (checkpoint is null)
        {
            error = "the checkpoint is empty";
            return false;
        }

        if (checkpoint.SchemaVersion != CurrentSchemaVersion)
        {
            error = $"checkpoint schema version {checkpoint.SchemaVersion} is not supported";
            return false;
        }

        if (!string.Equals(checkpoint.Stage, InterpretationValidatedStage, StringComparison.Ordinal))
        {
            error = $"checkpoint stage '{checkpoint.Stage}' is not supported";
            return false;
        }

        if (checkpoint.JobId != jobId)
        {
            error = $"the checkpoint belongs to planning job {checkpoint.JobId}";
            return false;
        }

        if (!string.Equals(checkpoint.RequestFingerprint, FingerprintRequest(requestPayload), StringComparison.Ordinal))
        {
            error = "the checkpoint was made for a different request payload";
            return false;
        }

        var saved = checkpoint.Interpretation;
        if (saved is null ||
            saved.ScopeResolution != PlanningScopeResolution.Resolved ||
            string.IsNullOrWhiteSpace(saved.MaintenanceIntent) ||
            string.IsNullOrWhiteSpace(saved.Scope) ||
            saved.ProposedAssetIds is not { Count: > 0 } ||
            saved.ProposedTaskIds is not { Count: > 0 } ||
            saved.MissingInformation is not { Count: 0 } ||
            saved.Assumptions is null ||
            saved.ScopeMappings is not { Count: > 0 })
        {
            error = "the checkpoint does not hold a complete, resolved interpretation";
            return false;
        }

        interpretation = saved;
        error = string.Empty;
        return true;
    }
}
