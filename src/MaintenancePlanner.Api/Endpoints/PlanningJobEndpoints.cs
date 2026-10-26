using System.Text.Json;
using MaintenancePlanner.Api.Contracts;
using MaintenancePlanner.Application.Approvals;
using MaintenancePlanner.Application.PlanningJobs;
using MaintenancePlanner.Application.Queries;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MaintenancePlanner.Api.Endpoints;

public static class PlanningJobEndpoints
{
    private static readonly JsonSerializerOptions RequestSerializationOptions =
        new(JsonSerializerDefaults.Web);

    public static WebApplication MapPlanningJobEndpoints(this WebApplication app)
    {
        app.MapPost("/api/planning-jobs", async Task<Results<
                Accepted<PlanningJobAcceptedResponse>,
                ProblemHttpResult>> (
                CreatePlanningJobRequest requestPayload,
                HttpRequest httpRequest,
                IPlanningJobs planningJobs,
                CancellationToken cancellationToken) =>
            {
                var operationIdValue = httpRequest.Headers["Idempotency-Key"].SingleOrDefault();
                if (!Guid.TryParse(operationIdValue, out var operationId) || operationId == Guid.Empty)
                {
                    return TypedResults.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "Invalid idempotency key",
                        detail: "Provide a non-empty GUID in the Idempotency-Key header.");
                }

                var validationErrors = new List<string>();
                if (string.IsNullOrWhiteSpace(requestPayload.RequestText))
                {
                    validationErrors.Add("RequestText is required and cannot be blank.");
                }

                if (requestPayload.AssetIds is null || requestPayload.AssetIds.Count == 0)
                {
                    validationErrors.Add("AssetIds must contain at least one asset ID.");
                }
                else if (requestPayload.AssetIds.Any(string.IsNullOrWhiteSpace))
                {
                    validationErrors.Add("AssetIds cannot contain blank asset IDs.");
                }

                if (validationErrors.Count > 0)
                {
                    return TypedResults.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "Invalid planning job request",
                        detail: string.Join(" ", validationErrors));
                }

                var serializedRequest = JsonSerializer.Serialize(
                    new PlanningJobRequestPayload(
                        requestPayload.RequestText,
                        requestPayload.AssetIds,
                        requestPayload.WindowId),
                    RequestSerializationOptions);
                var creation = await planningJobs.CreateAsync(
                    operationId,
                    serializedRequest,
                    cancellationToken);
                if (creation.PayloadConflict)
                {
                    return TypedResults.Problem(
                        statusCode: StatusCodes.Status409Conflict,
                        title: "Idempotency key conflict",
                        detail: "This Idempotency-Key has already been used with a different planning request.");
                }

                var job = creation.Job;
                var statusUrl = $"/api/planning-jobs/{job.Id}";

                return TypedResults.Accepted(
                    statusUrl,
                    new PlanningJobAcceptedResponse(job.Id, statusUrl));
            })
            .WithName("CreatePlanningJob")
            .WithSummary("Queue a planning job")
            .WithDescription("Persists a planning request and returns its status URL. Reuse the same Idempotency-Key to safely retry the same request.")
            .Produces<PlanningJobAcceptedResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        app.MapGet("/api/planning-jobs/{id:guid}", async Task<Results<
                Ok<PlanningJobStatusResponse>,
                NotFound>> (
                Guid id,
                IPlanningJobs planningJobs,
                CancellationToken cancellationToken) =>
            {
                var job = await planningJobs.GetAsync(id, cancellationToken);
                return job is null
                    ? TypedResults.NotFound()
                    : TypedResults.Ok(new PlanningJobStatusResponse(
                        job.Id,
                        job.Status.ToString(),
                        job.AttemptCount,
                        job.MaxAttempts,
                        job.CurrentStep,
                        job.ResultPayload,
                        job.ClarificationPayload,
                        job.FailureCode,
                        job.CreatedAtUtc,
                        job.StartedAtUtc,
                        job.CompletedAtUtc,
                        job.ErrorMessage));
            })
            .WithName("GetPlanningJobStatus")
            .WithSummary("Get planning job status")
            .WithDescription("Returns the persisted status, timestamps, and error message for a planning job.")
            .Produces<PlanningJobStatusResponse>()
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/planning-jobs/{id:guid}/clarify", async Task<Results<
                Ok<PlanningJobStatusResponse>,
                BadRequest<string>,
                NotFound>> (
                Guid id,
                SubmitClarificationRequest clarification,
                IPlanningJobs planningJobs,
                CancellationToken cancellationToken) =>
            {
                var job = await planningJobs.GetAsync(id, cancellationToken);
                if (job is null)
                    return TypedResults.NotFound();

                // Parse current result to check if clarification is actually needed
                if (string.IsNullOrEmpty(job.ResultPayload))
                    return TypedResults.BadRequest("Job has not yet generated a result requiring clarification.");

                using var resultDoc = JsonDocument.Parse(job.ResultPayload);
                var outcome = resultDoc.RootElement.GetProperty("outcome").GetString();
                if (outcome != "ClarificationRequired")
                    return TypedResults.BadRequest($"Job outcome is '{outcome}', not 'ClarificationRequired'. Clarification is not applicable.");

                // Validate that clarification answers provide necessary missing information
                if (string.IsNullOrWhiteSpace(clarification.WindowId))
                    return TypedResults.BadRequest("WindowId is required to resolve clarification.");

                // Update request with clarification answers and reset to Queued for resumption
                var currentRequest = JsonSerializer.Deserialize<PlanningJobRequestPayload>(
                    job.RequestPayload,
                    RequestSerializationOptions);
                if (currentRequest is null)
                    return TypedResults.BadRequest("Could not parse original request; cannot resume planning.");

                var updatedRequest = new PlanningJobRequestPayload(
                    currentRequest.RequestText,
                    currentRequest.AssetIds,
                    clarification.WindowId); // Use clarified window

                var serializedRequest = JsonSerializer.Serialize(updatedRequest, RequestSerializationOptions);
                var success = await planningJobs.UpdateAndResetAsync(id, serializedRequest, cancellationToken);
                if (!success)
                    return TypedResults.BadRequest("Could not update job; it may have been processed already.");

                // Fetch updated job to return current state
                var updatedJob = await planningJobs.GetAsync(id, cancellationToken);
                if (updatedJob is null)
                    return TypedResults.BadRequest("Job was updated but could not be retrieved.");

                return TypedResults.Ok(new PlanningJobStatusResponse(
                    updatedJob.Id,
                    updatedJob.Status.ToString(),
                    updatedJob.AttemptCount,
                    updatedJob.MaxAttempts,
                    updatedJob.CurrentStep,
                    updatedJob.ResultPayload,
                    updatedJob.ClarificationPayload,
                    updatedJob.FailureCode,
                    updatedJob.CreatedAtUtc,
                    updatedJob.StartedAtUtc,
                    updatedJob.CompletedAtUtc,
                    updatedJob.ErrorMessage));
            })
            .WithName("SubmitClarification")
            .WithSummary("Submit clarification answers")
            .WithDescription("Resume a planning job that requires clarification by providing the missing mandatory information.")
            .Produces<PlanningJobStatusResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/planning-jobs/{originalJobId:guid}/replan", async Task<Results<
                Accepted<ReplanAcceptedResponse>,
                NotFound,
                Conflict<string>>> (
                Guid originalJobId,
                CreatePlanningJobRequest revisedRequest,
                HttpRequest httpRequest,
                IPlanningJobs planningJobs,
                IDraftApprovalService approvalService,
                CancellationToken cancellationToken) =>
            {
                var originalJob = await planningJobs.GetAsync(originalJobId, cancellationToken);
                if (originalJob is null)
                    return TypedResults.NotFound();

                var existingDraft = await approvalService.GetApprovedDraftForJobAsync(originalJobId, cancellationToken);
                if (existingDraft is null)
                    return TypedResults.Conflict("Original job has no approved draft; cannot create replacement plan.");

                var operationIdValue = httpRequest.Headers["Idempotency-Key"].SingleOrDefault();
                if (!Guid.TryParse(operationIdValue, out var operationId) || operationId == Guid.Empty)
                {
                    return TypedResults.Conflict(
                        "Provide a non-empty GUID in the Idempotency-Key header.");
                }

                var validationErrors = new List<string>();
                if (string.IsNullOrWhiteSpace(revisedRequest.RequestText))
                {
                    validationErrors.Add("RequestText is required and cannot be blank.");
                }

                if (revisedRequest.AssetIds is null || revisedRequest.AssetIds.Count == 0)
                {
                    validationErrors.Add("AssetIds must contain at least one asset ID.");
                }
                else if (revisedRequest.AssetIds.Any(string.IsNullOrWhiteSpace))
                {
                    validationErrors.Add("AssetIds cannot contain blank asset IDs.");
                }

                if (validationErrors.Count > 0)
                {
                    return TypedResults.Conflict(string.Join(" ", validationErrors));
                }

                var serializedRequest = JsonSerializer.Serialize(
                    new PlanningJobRequestPayload(
                        revisedRequest.RequestText,
                        revisedRequest.AssetIds,
                        revisedRequest.WindowId),
                    RequestSerializationOptions);

                var creation = await planningJobs.CreateReplacementAsync(
                    operationId,
                    originalJobId,
                    serializedRequest,
                    cancellationToken);

                if (creation.PayloadConflict)
                {
                    return TypedResults.Conflict(
                        "This Idempotency-Key has already been used with a different planning request.");
                }

                var newJob = creation.Job;
                var statusUrl = $"/api/planning-jobs/{newJob.Id}";

                return TypedResults.Accepted(
                    statusUrl,
                    new ReplanAcceptedResponse(newJob.Id, statusUrl));
            })
            .WithName("ReplanApprovedJob")
            .WithSummary("Create a replacement plan for an approved job")
            .WithDescription("Create a new planning job as a revision of an already-approved plan. The new job inherits the original job ID reference.")
            .Produces<ReplanAcceptedResponse>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound)
            .Produces<string>(StatusCodes.Status409Conflict);

        app.MapGet("/api/planning-jobs/{jobId:guid}/revisions", async Task<Results<
                Ok<IReadOnlyList<PlanningJobRevisionResponse>>,
                NotFound>> (
                Guid jobId,
                IMaintenancePlannerQueries queries,
                CancellationToken cancellationToken) =>
            {
                var revisions = await queries.GetJobRevisionChainAsync(jobId, cancellationToken);
                if (revisions.Count == 0)
                    return TypedResults.NotFound();

                var response = revisions.Select(job => new PlanningJobRevisionResponse(
                    job.Id,
                    job.Status.ToString(),
                    job.CreatedAtUtc,
                    job.OriginalJobId)).ToList();

                return TypedResults.Ok<IReadOnlyList<PlanningJobRevisionResponse>>(response);
            })
            .WithName("GetJobRevisions")
            .WithSummary("Get the complete revision chain for a planning job")
            .WithDescription("Returns the requested job and all its related revisions in the chain, ordered by creation time. If the requested job is a revision, returns the complete chain from root through that revision and any existing descendants. Each entry includes its parent job ID to show the revision hierarchy.")
            .Produces<IReadOnlyList<PlanningJobRevisionResponse>>()
            .Produces(StatusCodes.Status404NotFound);

        app.MapGet("/api/planning-jobs/{jobId:guid}/candidates", async Task<Results<
                Ok<PlanningJobCandidatesResponse>,
                NotFound,
                BadRequest<string>>> (
                Guid jobId,
                IPlanningJobs planningJobs,
                CancellationToken cancellationToken) =>
            {
                var job = await planningJobs.GetAsync(jobId, cancellationToken);
                if (job is null)
                    return TypedResults.NotFound();

                if (job.Status == Domain.Entities.PlanningJobStatus.Queued ||
                    job.Status == Domain.Entities.PlanningJobStatus.Running)
                {
                    return TypedResults.BadRequest("Job is still processing. Check status at " + $"/api/planning-jobs/{jobId}");
                }

                if (job.Status == Domain.Entities.PlanningJobStatus.Failed)
                {
                    return TypedResults.BadRequest($"Job failed: {job.ErrorMessage ?? job.FailureCode ?? "Unknown error"}");
                }

                if (string.IsNullOrEmpty(job.ResultPayload))
                {
                    return TypedResults.BadRequest("Job has no result available.");
                }

                try
                {
                    using var resultDoc = JsonDocument.Parse(job.ResultPayload);
                    var outcome = resultDoc.RootElement.GetProperty("outcome").GetString() ?? "Unknown";

                    if (outcome == "ClarificationRequired")
                    {
                        return TypedResults.BadRequest("Job requires clarification. Submit clarification at " + $"/api/planning-jobs/{jobId}/clarify");
                    }

                    if (outcome != "CandidatesGenerated")
                    {
                        return TypedResults.BadRequest($"Job outcome is '{outcome}'. No candidates available.");
                    }

                    var result = resultDoc.RootElement.GetProperty("result");
                    var candidates = result.GetProperty("candidates").EnumerateArray()
                        .Select(c => new CandidateResponse(
                            Rationale: c.GetProperty("rationale").GetString() ?? "",
                            RequestedTaskIds: c.GetProperty("requestedTaskIds").EnumerateArray()
                                .Select(t => t.GetString() ?? "").ToList(),
                            DependencyTaskIds: c.GetProperty("dependencyTaskIds").EnumerateArray()
                                .Select(t => t.GetString() ?? "").ToList(),
                            TaskPlacements: c.GetProperty("taskPlacements").Deserialize<List<object>>() ?? [],
                            EvidenceReferences: c.TryGetProperty("evidenceReferences", out var evidenceElem)
                                ? evidenceElem.ValueKind == System.Text.Json.JsonValueKind.Array
                                    ? evidenceElem.EnumerateArray()
                                        .Select(e => e.ValueKind == System.Text.Json.JsonValueKind.String
                                            ? e.GetString() ?? ""
                                            : e.GetProperty("sourceId").GetString() ?? "")
                                        .ToList()
                                    : []
                                : [],
                            TotalLabourCostAmount: c.TryGetProperty("totalLabourCostAmount", out var costAmount)
                                ? costAmount.GetDecimal()
                                : 0m,
                            TotalLabourCostCurrency: c.TryGetProperty("totalLabourCostCurrency", out var currency)
                                ? currency.GetString() ?? "UNKNOWN"
                                : "UNKNOWN",
                            CompletionTimeMinutes: c.TryGetProperty("completionTimeMinutes", out var time)
                                ? time.GetInt64()
                                : 0L,
                            DeferralReasons: c.TryGetProperty("deferralReasons", out var deferralElem)
                                ? deferralElem.EnumerateArray()
                                    .Select(d => new DeferralReasonResponse(
                                        TaskId: d.GetProperty("taskId").GetString() ?? "",
                                        Reason: d.GetProperty("reason").GetString() ?? ""))
                                    .ToList()
                                : [],
                            IsLowestCostCandidate: c.TryGetProperty("isLowestCostCandidate", out var isLowest)
                                ? isLowest.GetBoolean()
                                : false,
                            CostComparabilityWarning: c.TryGetProperty("costComparabilityWarning", out var warning)
                                ? warning.GetString()
                                : null))
                        .ToList();

                    InterpretationResponse? interpretation = null;
                    if (result.TryGetProperty("interpretation", out var interpretationElement))
                    {
                        var interp = interpretationElement;
                        interpretation = new InterpretationResponse(
                            MaintenanceIntent: interp.GetProperty("maintenanceIntent").GetString() ?? "",
                            Scope: interp.GetProperty("scope").GetString() ?? "",
                            ProposedAssetIds: interp.GetProperty("proposedAssetIds").EnumerateArray()
                                .Select(a => a.GetString() ?? "").ToList(),
                            ProposedTaskIds: interp.GetProperty("proposedTaskIds").EnumerateArray()
                                .Select(t => t.GetString() ?? "").ToList());
                    }

                    var response = new PlanningJobCandidatesResponse(
                        JobId: job.Id,
                        Status: job.Status.ToString(),
                        Outcome: outcome,
                        Candidates: candidates,
                        Interpretation: interpretation);

                    return TypedResults.Ok(response);
                }
                catch (JsonException ex)
                {
                    return TypedResults.BadRequest($"Failed to parse job result: {ex.Message}");
                }
            })
            .WithName("GetCandidates")
            .WithSummary("Get candidates for a completed planning job")
            .WithDescription("Returns all candidates generated by a completed planning job, with task placements, evidence references, and interpretation details.")
            .Produces<PlanningJobCandidatesResponse>()
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return app;
    }
}
