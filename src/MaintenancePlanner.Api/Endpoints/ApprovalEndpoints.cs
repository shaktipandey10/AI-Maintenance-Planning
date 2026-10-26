using System.Text.Json;
using MaintenancePlanner.Api.Contracts;
using MaintenancePlanner.Application.Approvals;
using MaintenancePlanner.Application.PlanningJobs;
using MaintenancePlanner.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace MaintenancePlanner.Api.Endpoints;

public static class ApprovalEndpoints
{
    public static WebApplication MapApprovalEndpoints(this WebApplication app)
    {
        app.MapPost("/api/drafts/{planningJobId:guid}/approve",
                async Task<Results<Accepted, Conflict<ApprovalErrorResponse>>> (
                    Guid planningJobId,
                    IDraftApprovalService approvalService,
                    IPlanningJobs planningJobs,
                    CancellationToken cancellationToken) =>
                {
                    var planningJob = await planningJobs.GetAsync(planningJobId, cancellationToken);
                    if (planningJob is null)
                    {
                        var notFoundResponse = new ApprovalErrorResponse("PlanningJob", "PLANNING_JOB_NOT_FOUND", planningJobId.ToString(), "Planning job not found");
                        return TypedResults.Conflict(notFoundResponse);
                    }

                    DraftApprovalResult result;

                    if (planningJob.OriginalJobId.HasValue)
                    {
                        var originalDraft = await approvalService.GetApprovedDraftForJobAsync(planningJob.OriginalJobId.Value, cancellationToken);
                        if (originalDraft is null)
                        {
                            var noDraftResponse = new ApprovalErrorResponse(
                                "PlanningJob",
                                "NO_APPROVED_ORIGINAL_DRAFT",
                                planningJob.OriginalJobId.Value.ToString(),
                                "Original job has no approved draft; cannot create replacement");
                            return TypedResults.Conflict(noDraftResponse);
                        }

                        result = await approvalService.ApproveDraftReplacementAsync(planningJobId, cancellationToken);
                    }
                    else
                    {
                        result = await approvalService.ApproveDraftAsync(planningJobId, cancellationToken);
                    }

                    if (result.Succeeded)
                        return TypedResults.Accepted($"/api/drafts/{result.DraftId}");

                    var errorResponse = result.ConflictData is not null
                        ? new ApprovalErrorResponse(
                            result.ConflictData.ResourceType,
                            result.ConflictData.Reason,
                            result.ConflictData.ResourceId,
                            result.FailureReason ?? "Unknown error")
                        : new ApprovalErrorResponse("Unknown", "UNKNOWN_ERROR", null, "Unknown error");

                    return TypedResults.Conflict(errorResponse);
                })
            .WithName("ApproveDraft")
            .WithSummary("Approve a planning draft and reserve required spare parts")
            .Produces(StatusCodes.Status202Accepted)
            .Produces<ApprovalErrorResponse>(StatusCodes.Status409Conflict);

        app.MapPost("/api/drafts/{draftId:guid}/cancel",
                async Task<Results<NoContent, Conflict<ApprovalErrorResponse>, NotFound>> (
                    Guid draftId,
                    IDraftApprovalService approvalService,
                    CancellationToken cancellationToken) =>
                {
                    var result = await approvalService.CancelDraftAsync(draftId, cancellationToken);

                    if (result.Succeeded)
                        return TypedResults.NoContent();

                    if (result.FailureReason == "Draft not found")
                        return TypedResults.NotFound();

                    var errorResponse = result.ConflictData is not null
                        ? new ApprovalErrorResponse(
                            result.ConflictData.ResourceType,
                            result.ConflictData.Reason,
                            result.ConflictData.ResourceId,
                            result.FailureReason ?? "Unknown error")
                        : new ApprovalErrorResponse("Unknown", "UNKNOWN_ERROR", null, "Unknown error");

                    return TypedResults.Conflict(errorResponse);
                })
            .WithName("CancelDraft")
            .WithSummary("Cancel an approved draft and release reserved spare parts")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ApprovalErrorResponse>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status404NotFound);

        app.MapGet("/api/drafts/{draftId:guid}", async Task<Results<
                Ok<DraftDetailResponse>,
                NotFound>> (
                Guid draftId,
                MaintenancePlannerDbContext dbContext,
                CancellationToken cancellationToken) =>
            {
                var draft = await dbContext.Drafts
                    .Include(d => d.PlanningJob)
                    .Include(d => d.Reservations)
                    .FirstOrDefaultAsync(d => d.Id == draftId, cancellationToken);

                if (draft is null)
                    return TypedResults.NotFound();

                var planningJob = draft.PlanningJob;
                if (planningJob is null)
                    return TypedResults.NotFound();

                CandidateResponse? selectedCandidate = null;
                InterpretationResponse? interpretation = null;

                // Parse candidates from result payload if available
                if (!string.IsNullOrEmpty(planningJob.ResultPayload))
                {
                    try
                    {
                        using var resultDoc = JsonDocument.Parse(planningJob.ResultPayload);
                        var outcome = GetStringProperty(resultDoc.RootElement, "outcome");

                        if (outcome == "CandidatesGenerated")
                        {
                            var result = resultDoc.RootElement.GetProperty("result");
                            var candidates = result.GetProperty("candidates").EnumerateArray().ToList();

                            // Return first candidate as the selected one
                            if (candidates.Count > 0)
                            {
                                var c = candidates[0];
                                selectedCandidate = new CandidateResponse(
                                    Rationale: GetStringProperty(c, "rationale") ?? "",
                                    RequestedTaskIds: GetStringArrayProperty(c, "requestedTaskIds"),
                                    DependencyTaskIds: GetStringArrayProperty(c, "dependencyTaskIds"),
                                    TaskPlacements: c.TryGetProperty("taskPlacements", out var taskPlacementsElem)
                                        ? taskPlacementsElem.Deserialize<List<object>>() ?? []
                                        : [],
                                    EvidenceReferences: GetStringArrayProperty(c, "evidenceReferences"),
                                    TotalLabourCostAmount: c.TryGetProperty("totalLabourCostAmount", out var costAmount)
                                        ? costAmount.GetDecimal()
                                        : 0m,
                                    TotalLabourCostCurrency: GetStringProperty(c, "totalLabourCostCurrency") ?? "UNKNOWN",
                                    CompletionTimeMinutes: c.TryGetProperty("completionTimeMinutes", out var time)
                                        ? time.GetInt64()
                                        : 0L,
                                    DeferralReasons: c.TryGetProperty("deferralReasons", out var deferralElem)
                                        ? deferralElem.EnumerateArray()
                                            .Select(d => new DeferralReasonResponse(
                                                TaskId: GetStringProperty(d, "taskId") ?? "",
                                                Reason: GetStringProperty(d, "reason") ?? ""))
                                            .ToList()
                                        : [],
                                    IsLowestCostCandidate: c.TryGetProperty("isLowestCostCandidate", out var isLowest)
                                        ? isLowest.GetBoolean()
                                        : false,
                                    CostComparabilityWarning: GetStringProperty(c, "costComparabilityWarning"));
                            }

                            // Extract interpretation if available
                            if (result.TryGetProperty("interpretation", out var interpretationElement))
                            {
                                var interp = interpretationElement;
                                interpretation = new InterpretationResponse(
                                    MaintenanceIntent: GetStringProperty(interp, "maintenanceIntent") ?? "",
                                    Scope: GetStringProperty(interp, "scope") ?? "",
                                    ProposedAssetIds: GetStringArrayProperty(interp, "proposedAssetIds"),
                                    ProposedTaskIds: GetStringArrayProperty(interp, "proposedTaskIds"));
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        // If result parsing fails, continue with null candidate and interpretation
                    }
                }

                // Map reservations
                var reservations = draft.Reservations
                    .Select(r => new ReservationResponse(
                        ReservationId: r.Id,
                        SparePartId: r.SparePartId,
                        QuantityReserved: r.QuantityReserved,
                        Status: r.Status.ToString(),
                        CreatedAtUtc: r.CreatedAtUtc,
                        ConfirmedAtUtc: r.ConfirmedAtUtc))
                    .ToList();

                var response = new DraftDetailResponse(
                    DraftId: draft.Id,
                    PlanningJobId: draft.PlanningJobId,
                    Status: draft.Status.ToString(),
                    CreatedAtUtc: draft.CreatedAtUtc,
                    ApprovedAtUtc: draft.ApprovedAtUtc,
                    SelectedCandidate: selectedCandidate,
                    Reservations: reservations,
                    Interpretation: interpretation);

                return TypedResults.Ok(response);
            })
            .WithName("GetDraftDetail")
            .WithSummary("Get draft details with selected candidate and reservations")
            .WithDescription("Returns complete draft information including the selected maintenance candidate, spare part reservations, and interpretation details.")
            .Produces<DraftDetailResponse>()
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    /// <summary>Safely extract a string property from a JsonElement, handling cases where the property might be an object or null.</summary>
    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        // If it's already a string, return it
        if (property.ValueKind == System.Text.Json.JsonValueKind.String)
            return property.GetString();

        // If it's null, return null
        if (property.ValueKind == System.Text.Json.JsonValueKind.Null)
            return null;

        // If it's an object, try to extract common string fields
        if (property.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            // Return the JSON representation as a fallback
            return property.GetRawText();
        }

        // For other types (numbers, arrays, etc.), return string representation
        return property.GetRawText();
    }

    /// <summary>Safely extract a string array property from a JsonElement.</summary>
    private static IReadOnlyList<string> GetStringArrayProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return [];

        if (property.ValueKind != System.Text.Json.JsonValueKind.Array)
            return [];

        try
        {
            return property.EnumerateArray()
                .Select(item =>
                {
                    if (item.ValueKind == System.Text.Json.JsonValueKind.String)
                        return item.GetString() ?? "";
                    return item.GetRawText();
                })
                .ToList();
        }
        catch
        {
            return [];
        }
    }
}

public sealed record ApprovalErrorResponse(
    string ResourceType,
    string Reason,
    string? ResourceId = null,
    string Message = "");
