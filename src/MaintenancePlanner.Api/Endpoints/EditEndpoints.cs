using MaintenancePlanner.Api.Contracts;
using MaintenancePlanner.Application.Edits;
using MaintenancePlanner.Application.Locks;
using MaintenancePlanner.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AppTaskEditRequest = MaintenancePlanner.Application.Edits.TaskEditRequest;
using AppTaskEditsSubmissionRequest = MaintenancePlanner.Application.Edits.TaskEditsSubmissionRequest;
using AppEditedTaskPlacementResponse = MaintenancePlanner.Application.Edits.EditedTaskPlacementResponse;
using AppTaskEditsResponse = MaintenancePlanner.Application.Edits.TaskEditsResponse;
using AppEditValidationResult = MaintenancePlanner.Application.Edits.EditValidationResult;

namespace MaintenancePlanner.Api.Endpoints;

public static class EditEndpoints
{
    public static WebApplication MapEditEndpoints(this WebApplication app)
    {
        app.MapPost("/api/drafts/{draftId:guid}/task-edits",
                async Task<Results<Ok<AppTaskEditsResponse>, NotFound, Conflict>> (
                    Guid draftId,
                    AppTaskEditsSubmissionRequest request,
                    ITaskEditorService editorService,
                    MaintenancePlannerDbContext dbContext,
                    CancellationToken cancellationToken) =>
                {
                    var draft = await dbContext.Drafts.FirstOrDefaultAsync(d => d.Id == draftId, cancellationToken);
                    if (draft is null)
                        return TypedResults.NotFound();

                    try
                    {
                        var result = await editorService.SubmitEditsAsync(draftId, request, cancellationToken);
                        return TypedResults.Ok(result);
                    }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("not in approved state"))
                    {
                        return TypedResults.Conflict();
                    }
                })
            .WithName("SubmitTaskEdits")
            .WithSummary("Submit task edits for a draft")
            .Produces<AppTaskEditsResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        app.MapGet("/api/drafts/{draftId:guid}/task-edits",
                async Task<Results<Ok<IReadOnlyList<AppEditedTaskPlacementResponse>>, NotFound>> (
                    Guid draftId,
                    ITaskEditorService editorService,
                    MaintenancePlannerDbContext dbContext,
                    CancellationToken cancellationToken) =>
                {
                    var draft = await dbContext.Drafts.FirstOrDefaultAsync(d => d.Id == draftId, cancellationToken);
                    if (draft is null)
                        return TypedResults.NotFound();

                    var edits = await editorService.GetEditsAsync(draftId, cancellationToken);
                    return TypedResults.Ok(edits);
                })
            .WithName("GetTaskEdits")
            .WithSummary("Get all task edits for a draft")
            .Produces<IReadOnlyList<AppEditedTaskPlacementResponse>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        app.MapDelete("/api/drafts/{draftId:guid}/task-edits/{taskId}",
                async Task<Results<NoContent, NotFound>> (
                    Guid draftId,
                    string taskId,
                    ITaskEditorService editorService,
                    MaintenancePlannerDbContext dbContext,
                    CancellationToken cancellationToken) =>
                {
                    var draft = await dbContext.Drafts.FirstOrDefaultAsync(d => d.Id == draftId, cancellationToken);
                    if (draft is null)
                        return TypedResults.NotFound();

                    var deleted = await editorService.DeleteEditAsync(draftId, taskId, cancellationToken);
                    return deleted ? TypedResults.NoContent() : TypedResults.NotFound();
                })
            .WithName("DeleteTaskEdit")
            .WithSummary("Delete a task edit from a draft")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);

        app.MapDelete("/api/drafts/{draftId:guid}/task-edits",
                async Task<Results<NoContent, NotFound>> (
                    Guid draftId,
                    ITaskEditorService editorService,
                    MaintenancePlannerDbContext dbContext,
                    CancellationToken cancellationToken) =>
                {
                    var draft = await dbContext.Drafts.FirstOrDefaultAsync(d => d.Id == draftId, cancellationToken);
                    if (draft is null)
                        return TypedResults.NotFound();

                    await editorService.ClearEditsAsync(draftId, cancellationToken);
                    return TypedResults.NoContent();
                })
            .WithName("ClearTaskEdits")
            .WithSummary("Delete all task edits from a draft")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/drafts/{draftId:guid}/task-edits/validate",
                async Task<Results<Ok<AppEditValidationResult>, NotFound, Conflict>> (
                    Guid draftId,
                    IEditValidationService validationService,
                    MaintenancePlannerDbContext dbContext,
                    CancellationToken cancellationToken) =>
                {
                    var draft = await dbContext.Drafts.FirstOrDefaultAsync(d => d.Id == draftId, cancellationToken);
                    if (draft is null)
                        return TypedResults.NotFound();

                    try
                    {
                        var result = await validationService.ValidateEditsAsync(draftId, cancellationToken);
                        return TypedResults.Ok(result);
                    }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("not in approved state"))
                    {
                        return TypedResults.Conflict();
                    }
                })
            .WithName("ValidateTaskEdits")
            .WithSummary("Validate all task edits for a draft")
            .Produces<AppEditValidationResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        // FR05: a reviewable revision built from the edits, approved through POST /api/drafts/{jobId}/approve
        app.MapPost("/api/drafts/{draftId:guid}/task-edits/revision",
                async Task<Results<Accepted<ReplanAcceptedResponse>, BadRequest<string>, NotFound, Conflict<EditRevisionResult>>> (
                    Guid draftId,
                    HttpRequest httpRequest,
                    [FromServices] IEditRevisionService revisionService,
                    CancellationToken cancellationToken) =>
                {
                    if (!Guid.TryParse(httpRequest.Headers["Idempotency-Key"].SingleOrDefault(), out var operationId) ||
                        operationId == Guid.Empty)
                    {
                        return TypedResults.BadRequest("Provide a non-empty GUID in the Idempotency-Key header.");
                    }

                    var result = await revisionService.CreateRevisionAsync(draftId, operationId, cancellationToken);
                    if (result.Succeeded)
                    {
                        var statusUrl = $"/api/planning-jobs/{result.PlanningJobId}";
                        return TypedResults.Accepted(statusUrl, new ReplanAcceptedResponse(result.PlanningJobId!.Value, statusUrl));
                    }

                    return result.FailureCode == EditRevisionResult.DraftNotFound
                        ? TypedResults.NotFound()
                        : TypedResults.Conflict(result);
                })
            .WithName("CreateRevisionFromTaskEdits")
            .WithSummary("Create a reviewable revision of an approved draft from its task edits")
            .WithDescription("Requires an Idempotency-Key header (GUID). Validates every edit and the whole edited plan against current data, task locks and task progress (completed tasks are left out, tasks in progress keep their placement). On success returns 202 with the revision's planning job, a replacement of the draft's job with the edited plan as its only candidate: review it with GET /api/planning-jobs/{newJobId}/candidates and approve it with POST /api/drafts/{newJobId}/approve, which supersedes the draft. Returns 409 with every violation when the edited plan is not valid; nothing is created.")
            .Produces<ReplanAcceptedResponse>(StatusCodes.Status202Accepted)
            .Produces<string>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces<EditRevisionResult>(StatusCodes.Status409Conflict);

        app.MapGet("/api/drafts/{draftId:guid}/locks",
                async Task<Results<Ok<IReadOnlyList<TaskLockResponse>>, NotFound>> (
                    Guid draftId,
                    [FromServices] ITaskLockService lockService,
                    MaintenancePlannerDbContext dbContext,
                    CancellationToken cancellationToken) =>
                {
                    if (!await dbContext.Drafts.AnyAsync(d => d.Id == draftId, cancellationToken))
                        return TypedResults.NotFound();

                    IReadOnlyList<TaskLockResponse> locks = (await lockService.GetLocksAsync(draftId, cancellationToken))
                        .Select(entry => new TaskLockResponse(entry.Key, entry.Value))
                        .OrderBy(entry => entry.TaskId, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    return TypedResults.Ok(locks);
                })
            .WithName("GetTaskLocks")
            .WithSummary("Get the technician locks of a draft's tasks")
            .Produces<IReadOnlyList<TaskLockResponse>>()
            .Produces(StatusCodes.Status404NotFound);

        app.MapPut("/api/drafts/{draftId:guid}/locks/{taskId}",
                async Task<Results<NoContent, NotFound<TaskLockChangeResult>, Conflict<TaskLockChangeResult>, BadRequest<string>>> (
                    Guid draftId,
                    string taskId,
                    TaskLockRequest request,
                    [FromServices] ITaskLockService lockService,
                    CancellationToken cancellationToken) =>
                {
                    if (string.IsNullOrWhiteSpace(request.TechnicianId))
                        return TypedResults.BadRequest("technicianId is required");

                    return ToLockResult(await lockService.LockAsync(draftId, taskId, request.TechnicianId.Trim(), cancellationToken));
                })
            .WithName("LockTask")
            .WithSummary("Lock a task of an approved draft to a technician")
            .WithDescription("Body {\"technicianId\": \"TECH-001\"}. Replanning and edit revisions keep that technician on the task. A lock that cannot be honoured with current data is rejected with 409 and the specific conflict (LOCKED_TECHNICIAN_NOT_FOUND, LOCKED_TECHNICIAN_UNQUALIFIED, LOCKED_TECHNICIAN_UNAVAILABLE); only an approved draft's locks can change (409 DRAFT_NOT_APPROVED), and not those of started work (409 TASK_IN_PROGRESS, TASK_ALREADY_COMPLETED).")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<TaskLockChangeResult>(StatusCodes.Status404NotFound)
            .Produces<TaskLockChangeResult>(StatusCodes.Status409Conflict)
            .Produces<string>(StatusCodes.Status400BadRequest);

        app.MapDelete("/api/drafts/{draftId:guid}/locks/{taskId}",
                async Task<Results<NoContent, NotFound<TaskLockChangeResult>, Conflict<TaskLockChangeResult>, BadRequest<string>>> (
                    Guid draftId,
                    string taskId,
                    [FromServices] ITaskLockService lockService,
                    CancellationToken cancellationToken) =>
                    ToLockResult(await lockService.UnlockAsync(draftId, taskId, cancellationToken)))
            .WithName("UnlockTask")
            .WithSummary("Remove the technician lock of a task of an approved draft")
            .WithDescription("Removing a lock that does not exist succeeds and changes nothing. Rejected with 409 for a draft that is not approved (DRAFT_NOT_APPROVED) or a task in progress or completed (TASK_IN_PROGRESS, TASK_ALREADY_COMPLETED).")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<TaskLockChangeResult>(StatusCodes.Status404NotFound)
            .Produces<TaskLockChangeResult>(StatusCodes.Status409Conflict);

        return app;
    }

    private static Results<NoContent, NotFound<TaskLockChangeResult>, Conflict<TaskLockChangeResult>, BadRequest<string>> ToLockResult(
        TaskLockChangeResult result) =>
        result.Succeeded
            ? TypedResults.NoContent()
            : result.FailureCode is TaskLockChangeResult.DraftNotFound or TaskLockChangeResult.TaskNotInPlan
                ? TypedResults.NotFound(result)
                : TypedResults.Conflict(result);
}

public sealed record TaskLockRequest(string? TechnicianId);

public sealed record TaskLockResponse(string TaskId, string TechnicianId);
