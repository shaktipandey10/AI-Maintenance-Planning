using System.Text.Json;
using MaintenancePlanner.Application.History;
using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace MaintenancePlanner.Api.Endpoints;

/// <summary>FR09: saved plans and the business-action history of a plan's revisions.</summary>
public static class HistoryEndpoints
{
    /// <summary>
    /// Takes the performer of the request's business actions from the X-Actor-Role header: the role chosen in the
    /// UI's role selector. It is self-declared, not authenticated.
    /// </summary>
    public static WebApplication UseBusinessActorFromHeader(this WebApplication app)
    {
        app.Use((context, next) =>
        {
            context.RequestServices.GetRequiredService<BusinessActor>()
                .SetRole(context.Request.Headers[BusinessActor.HeaderName]);
            return next(context);
        });
        return app;
    }

    public static WebApplication MapHistoryEndpoints(this WebApplication app)
    {
        app.MapGet("/api/drafts", async Task<Ok<IReadOnlyList<SavedPlanResponse>>> (
                MaintenancePlannerDbContext dbContext,
                CancellationToken cancellationToken) =>
            {
                var drafts = await dbContext.Drafts
                    .AsNoTracking()
                    .Select(draft => new
                    {
                        draft.Id,
                        draft.PlanningJobId,
                        draft.Status,
                        draft.CreatedAtUtc,
                        draft.ApprovedAtUtc,
                        draft.PlanningJob!.OriginalJobId,
                        draft.PlanningJob.RequestPayload
                    })
                    .ToListAsync(cancellationToken);

                // SQLite cannot order by DateTimeOffset, so saved plans are ordered newest first here
                IReadOnlyList<SavedPlanResponse> response = drafts
                    .OrderByDescending(draft => draft.CreatedAtUtc)
                    .Select(draft =>
                    {
                        var request = ReadRequest(draft.RequestPayload);
                        return new SavedPlanResponse(
                            draft.Id,
                            draft.PlanningJobId,
                            draft.OriginalJobId,
                            draft.Status.ToString(),
                            draft.CreatedAtUtc,
                            draft.ApprovedAtUtc,
                            request.AssetIds,
                            request.WindowId,
                            request.RequestText);
                    })
                    .ToList();
                return TypedResults.Ok(response);
            })
            .WithName("ListSavedPlans")
            .WithSummary("List saved plans (drafts), newest first")
            .WithDescription("Every saved draft with its status, planning job, the job it replaces (if any) and the request's assets, window and text, so a plan can be opened without knowing its draft ID.")
            .Produces<IReadOnlyList<SavedPlanResponse>>();

        app.MapGet("/api/planning-jobs/{jobId:guid}/history", async Task<Results<Ok<IReadOnlyList<BusinessActionResponse>>, NotFound>> (
                Guid jobId,
                IMaintenancePlannerQueries queries,
                MaintenancePlannerDbContext dbContext,
                CancellationToken cancellationToken) =>
            {
                var chain = await queries.GetJobRevisionChainAsync(jobId, cancellationToken);
                if (chain.Count == 0)
                    return TypedResults.NotFound();

                var jobIds = chain.Select(job => job.Id).ToList();
                var draftIds = await dbContext.Drafts
                    .Where(draft => jobIds.Contains(draft.PlanningJobId))
                    .Select(draft => draft.Id)
                    .ToListAsync(cancellationToken);
                var records = await dbContext.BusinessActionRecords
                    .AsNoTracking()
                    .Where(record =>
                        (record.PlanningJobId != null && jobIds.Contains(record.PlanningJobId.Value)) ||
                        (record.DraftId != null && draftIds.Contains(record.DraftId.Value)))
                    .ToListAsync(cancellationToken);

                IReadOnlyList<BusinessActionResponse> response = records
                    .OrderBy(record => record.PerformedAtUtc)
                    .Select(record => new BusinessActionResponse(
                        record.Id,
                        record.Action,
                        record.PerformedBy,
                        record.PerformedAtUtc,
                        record.PlanningJobId,
                        record.DraftId,
                        record.TaskId,
                        record.Details))
                    .ToList();
                return TypedResults.Ok(response);
            })
            .WithName("GetBusinessActionHistory")
            .WithSummary("Get the business-action history of a plan and all its revisions")
            .WithDescription("Approvals, cancellations, replanning, task progress and task edits recorded for every planning job in the revision chain of the given job and their drafts, oldest first, with who performed each action (the role sent in the X-Actor-Role header) and when.")
            .Produces<IReadOnlyList<BusinessActionResponse>>()
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    private static (IReadOnlyList<string> AssetIds, string? WindowId, string? RequestText) ReadRequest(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return ([], null, null);

            var assetIds = root.TryGetProperty("assetIds", out var assets) && assets.ValueKind == JsonValueKind.Array
                ? assets.EnumerateArray()
                    .Where(asset => asset.ValueKind == JsonValueKind.String)
                    .Select(asset => asset.GetString()!)
                    .ToArray()
                : [];
            return (assetIds, ReadString(root, "windowId"), ReadString(root, "requestText"));
        }
        catch (JsonException)
        {
            return ([], null, null);
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

public sealed record SavedPlanResponse(
    Guid DraftId,
    Guid PlanningJobId,
    Guid? OriginalJobId,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ApprovedAtUtc,
    IReadOnlyList<string> AssetIds,
    string? WindowId,
    string? RequestText);

public sealed record BusinessActionResponse(
    Guid Id,
    string Action,
    string PerformedBy,
    DateTimeOffset PerformedAtUtc,
    Guid? PlanningJobId,
    Guid? DraftId,
    string? TaskId,
    string? Details);
