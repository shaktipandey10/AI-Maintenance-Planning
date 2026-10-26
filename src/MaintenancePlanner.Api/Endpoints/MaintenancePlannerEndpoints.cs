using MaintenancePlanner.Api.Contracts;
using MaintenancePlanner.Application.Queries;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MaintenancePlanner.Api.Endpoints;

public static class MaintenancePlannerEndpoints
{
    public static WebApplication MapMaintenancePlannerEndpoints(this WebApplication app)
    {
        app.MapGet("/api/assets", async (
                IMaintenancePlannerQueries queries,
                CancellationToken cancellationToken) =>
                TypedResults.Ok((await queries.GetAssetsAsync(cancellationToken))
                    .Select(asset => new AssetResponse(asset.Id, asset.PlantId, asset.Name, asset.Model, asset.Priority))
                    .ToArray()))
            .WithName("GetAssets")
            .WithSummary("List seeded assets")
            .Produces<IReadOnlyList<AssetResponse>>();

        app.MapGet("/api/technicians", async (
                IMaintenancePlannerQueries queries,
                CancellationToken cancellationToken) =>
                TypedResults.Ok((await queries.GetTechniciansAsync(cancellationToken))
                    .Select(technician => new TechnicianResponse(
                        technician.Id,
                        technician.Name,
                        technician.HourlyRate,
                        technician.Skills,
                        technician.Availability.Select(availability => new TechnicianAvailabilityResponse(
                            availability.AvailableFrom,
                            availability.AvailableTo)).ToArray()))
                    .ToArray()))
            .WithName("GetTechnicians")
            .WithSummary("List technicians, skills, and availability")
            .Produces<IReadOnlyList<TechnicianResponse>>();

        app.MapGet("/api/spareparts", async (
                IMaintenancePlannerQueries queries,
                CancellationToken cancellationToken) =>
                TypedResults.Ok((await queries.GetSparePartsAsync(cancellationToken))
                    .Select(part => new SparePartResponse(
                        part.Id,
                        part.Name,
                        part.Unit,
                        part.OnHandQuantity,
                        part.ReservedQuantity,
                        part.AvailableQuantity))
                    .ToArray()))
            .WithName("GetSpareParts")
            .WithSummary("List spare parts and stock")
            .Produces<IReadOnlyList<SparePartResponse>>();

        app.MapGet("/api/maintenance-windows", async (
                IMaintenancePlannerQueries queries,
                CancellationToken cancellationToken) =>
                TypedResults.Ok((await queries.GetMaintenanceWindowsAsync(cancellationToken))
                    .Select(window => new MaintenanceWindowResponse(
                        window.Id,
                        window.Name,
                        window.Start,
                        window.End,
                        window.TimeZoneId))
                    .ToArray()))
            .WithName("GetMaintenanceWindows")
            .WithSummary("List maintenance windows")
            .Produces<IReadOnlyList<MaintenanceWindowResponse>>();

        app.MapGet("/api/task-catalogue/{assetId}", async Task<Results<Ok<IReadOnlyList<MaintenanceTaskResponse>>, NotFound>> (
                string assetId,
                IMaintenancePlannerQueries queries,
                CancellationToken cancellationToken) =>
            {
                var tasks = await queries.GetTaskCatalogueAsync(assetId, cancellationToken);
                return tasks is null
                    ? TypedResults.NotFound()
                    : TypedResults.Ok<IReadOnlyList<MaintenanceTaskResponse>>(tasks.Select(task =>
                        new MaintenanceTaskResponse(
                            task.Id,
                            task.AssetId,
                            task.Name,
                            task.DurationMinutes,
                            task.RequiredSkills,
                            task.RequiredCrewSize,
                            task.RequiredPartId,
                            task.RequiredPartQuantity,
                            task.PredecessorTaskId,
                            task.SourceDocumentId,
                            task.SourceSection)).ToArray());
            })
            .WithName("GetTaskCatalogueByAsset")
            .WithSummary("Get an asset's ordered task catalogue")
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }

}