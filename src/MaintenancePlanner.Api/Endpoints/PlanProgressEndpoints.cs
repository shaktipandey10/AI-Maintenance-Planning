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
                            t.RemainingQuantity)).ToArray());
                    return TypedResults.Ok(response);
                })
            .WithName("GetPlanProgress")
            .WithSummary("Get plan progress and remaining work")
            .WithDescription("Returns total required, completed, and remaining quantities for all spare parts in the plan.")
            .Produces<PlanProgressResponse>()
            .Produces(StatusCodes.Status404NotFound);

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
}
