using MaintenancePlanner.Api.Contracts;
using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace MaintenancePlanner.Api.Endpoints;

public static class PlanProgressEndpoints
{
    public static WebApplication MapPlanProgressEndpoints(this WebApplication app)
    {
        app.MapPost("/api/drafts/{draftId:guid}/mark-task-complete",
                async Task<Results<Ok<TaskCompletionResponse>, NotFound, BadRequest<string>, Conflict<string>>> (
                    Guid draftId,
                    string sparePartId,
                    IPlanProgressService progressService,
                    MaintenancePlannerDbContext dbContext,
                    CancellationToken cancellationToken) =>
                {
                    if (string.IsNullOrWhiteSpace(sparePartId))
                        return TypedResults.BadRequest("sparePartId is required");

                    var draft = await dbContext.Drafts.FirstOrDefaultAsync(d => d.Id == draftId, cancellationToken);
                    if (draft is null)
                        return TypedResults.NotFound();

                    if (draft.Status != DraftStatus.Approved)
                        return TypedResults.Conflict("Draft must be approved to mark tasks complete");

                    var result = await progressService.MarkTaskCompleteAsync(draftId, sparePartId, cancellationToken);
                    return TypedResults.Ok(new TaskCompletionResponse(result.Succeeded, result.FailureReason));
                })
            .WithName("MarkTaskComplete")
            .WithSummary("Mark a spare part task as complete")
            .WithDescription("Marks all incomplete reservations for a spare part as completed and persists the completion time.")
            .Produces<TaskCompletionResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .Produces<string>(StatusCodes.Status409Conflict);

        app.MapGet("/api/drafts/{draftId:guid}/progress",
                async Task<Results<Ok<PlanProgressResponse>, NotFound>> (
                    Guid draftId,
                    IPlanProgressService progressService,
                    MaintenancePlannerDbContext dbContext,
                    CancellationToken cancellationToken) =>
                {
                    var draft = await dbContext.Drafts.FirstOrDefaultAsync(d => d.Id == draftId, cancellationToken);
                    if (draft is null)
                        return TypedResults.NotFound();

                    var progress = await progressService.GetPlanProgressAsync(draftId, cancellationToken);
                    var response = new PlanProgressResponse(
                        progress.DraftId,
                        progress.TotalRequired,
                        progress.CompletedQuantity,
                        progress.RemainingQuantity,
                        progress.Tasks.Select(t => new TaskProgressResponse(
                            t.SparePartId,
                            t.RequiredQuantity,
                            t.CompletedQuantity,
                            t.RemainingQuantity)).ToArray())
                    {
                        TaskStatuses = progress.TaskStatuses
                            .Select(t => new TaskStatusResponse(t.TaskId, t.Status.ToString(), t.UpdatedAtUtc))
                            .ToArray()
                    };
                    return TypedResults.Ok(response);
                })
            .WithName("GetPlanProgress")
            .WithSummary("Get plan progress and remaining work")
            .WithDescription("Returns total required, completed, and remaining quantities for all spare parts in the plan, and the status of every planned task.")
            .Produces<PlanProgressResponse>()
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/drafts/{draftId:guid}/tasks/{taskId}/progress",
                async Task<Results<
                    Ok<TaskStatusUpdateResponse>,
                    BadRequest<string>,
                    NotFound<TaskStatusUpdateResponse>,
                    Conflict<TaskStatusUpdateResponse>>> (
                    Guid draftId,
                    string taskId,
                    TaskStatusUpdateRequest request,
                    IPlanProgressService progressService,
                    CancellationToken cancellationToken) =>
                {
                    if (!TryParseTaskStatus(request.Status, out var status))
                        return TypedResults.BadRequest("status must be one of NotStarted, InProgress or Completed");

                    var result = await progressService.UpdateTaskStatusAsync(draftId, taskId, status, cancellationToken);
                    if (result.Succeeded)
                        return TypedResults.Ok(new TaskStatusUpdateResponse(true, taskId, status.ToString()));

                    var rejection = new TaskStatusUpdateResponse(false, taskId, null, result.FailureCode, result.FailureReason);
                    return result.FailureCode is TaskStatusUpdateResult.DraftNotFound or TaskStatusUpdateResult.TaskNotInPlan
                        ? TypedResults.NotFound(rejection)
                        : TypedResults.Conflict(rejection);
                })
            .WithName("UpdateTaskProgress")
            .WithSummary("Move one planned task to its next progress status")
            .WithDescription("Body {\"status\": \"InProgress\" | \"Completed\"}. Tasks move forward only, NotStarted → InProgress → Completed; skipped, backward and repeated transitions return 409 INVALID_TASK_TRANSITION. Only tasks placed in an approved draft can be updated (404 TASK_NOT_IN_PLAN, 409 DRAFT_NOT_APPROVED).")
            .Produces<TaskStatusUpdateResponse>(StatusCodes.Status200OK)
            .Produces<string>(StatusCodes.Status400BadRequest)
            .Produces<TaskStatusUpdateResponse>(StatusCodes.Status404NotFound)
            .Produces<TaskStatusUpdateResponse>(StatusCodes.Status409Conflict);

        app.MapGet("/api/drafts/{draftId:guid}/export/json",
                async Task<Results<FileContentHttpResult, NotFound, Conflict<string>>> (
                    Guid draftId,
                    IPlanExportService exportService,
                    MaintenancePlannerDbContext dbContext,
                    CancellationToken cancellationToken) =>
                {
                    var draft = await dbContext.Drafts.FirstOrDefaultAsync(d => d.Id == draftId, cancellationToken);
                    if (draft is null)
                        return TypedResults.NotFound();

                    if (draft.Status != DraftStatus.Approved)
                        return TypedResults.Conflict("Only approved drafts can be exported");

                    try
                    {
                        var json = await exportService.ExportAsJsonAsync(draftId, cancellationToken);
                        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                        return TypedResults.File(bytes, "application/json", $"plan-{draftId:N}.json");
                    }
                    catch (InvalidOperationException ex)
                    {
                        return TypedResults.Conflict(ex.Message);
                    }
                })
            .WithName("ExportPlanJson")
            .WithSummary("Export plan as JSON")
            .WithDescription("Returns the approved draft as structured JSON including progress and remaining work.")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces<string>(StatusCodes.Status409Conflict);

        app.MapGet("/api/drafts/{draftId:guid}/export/pdf",
                async Task<Results<FileContentHttpResult, NotFound, Conflict<string>>> (
                    Guid draftId,
                    IPlanExportService exportService,
                    MaintenancePlannerDbContext dbContext,
                    CancellationToken cancellationToken) =>
                {
                    var draft = await dbContext.Drafts.FirstOrDefaultAsync(d => d.Id == draftId, cancellationToken);
                    if (draft is null)
                        return TypedResults.NotFound();

                    if (draft.Status != DraftStatus.Approved)
                        return TypedResults.Conflict("Only approved drafts can be exported");

                    try
                    {
                        var pdf = await exportService.ExportAsPdfAsync(draftId, cancellationToken);
                        return TypedResults.File(pdf, "application/pdf", $"plan-{draftId:N}.pdf");
                    }
                    catch (InvalidOperationException ex)
                    {
                        return TypedResults.Conflict(ex.Message);
                    }
                })
            .WithName("ExportPlanPdf")
            .WithSummary("Export plan as PDF")
            .WithDescription("Returns the approved draft as a PDF document including progress and remaining work.")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces<string>(StatusCodes.Status409Conflict);

        return app;
    }

    // Accepts the status names only (case-insensitive), not their numeric values.
    private static bool TryParseTaskStatus(string? value, out TaskProgressStatus status)
    {
        status = default;
        return !string.IsNullOrWhiteSpace(value) &&
               !value.Trim().All(char.IsDigit) &&
               Enum.TryParse(value.Trim(), ignoreCase: true, out status) &&
               Enum.IsDefined(status);
    }
}
