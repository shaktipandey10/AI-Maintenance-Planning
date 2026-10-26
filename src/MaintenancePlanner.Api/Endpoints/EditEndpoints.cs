using MaintenancePlanner.Application.Edits;
using MaintenancePlanner.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
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

        return app;
    }
}
