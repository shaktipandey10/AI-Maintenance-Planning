using MaintenancePlanner.Application.Imports;
using MaintenancePlanner.Infrastructure.Imports;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MaintenancePlanner.Api.Endpoints;

public static class ImportEndpoints
{
    public static WebApplication MapImportEndpoints(this WebApplication app)
    {
        // The literal "manuals" route takes precedence over the {dataset} route below
        app.MapPost("/api/imports/manuals",
                async Task<Results<Ok<ImportReport>, UnprocessableEntity<ImportReport>, BadRequest<ImportReport>>> (
                    HttpRequest request,
                    IManualImportService importService,
                    CancellationToken cancellationToken) =>
                {
                    var (form, file, problem) = await ReadUploadAsync(request, "manuals", ManualImportService.MaximumPdfBytes, cancellationToken);
                    if (problem is not null)
                        return TypedResults.BadRequest(problem);

                    if (!string.Equals(Path.GetExtension(file!.FileName), ".pdf", StringComparison.OrdinalIgnoreCase))
                        return TypedResults.BadRequest(ImportReport.Rejected("manuals", "pdf", file.FileName, "Manuals must be uploaded as .pdf files."));

                    await using var content = file.OpenReadStream();
                    var report = await importService.ImportPdfAsync(
                        file.FileName,
                        form!["documentId"].FirstOrDefault(),
                        form["version"].FirstOrDefault(),
                        content,
                        cancellationToken);
                    return report.Imported ? TypedResults.Ok(report) : TypedResults.UnprocessableEntity(report);
                })
            .WithName("ImportManual")
            .WithSummary("Import a text-based PDF manual")
            .WithDescription("multipart/form-data with 'file' (.pdf), 'documentId' and 'version'. Every page must have extractable text and the manual must contain at least one 'Section:' heading. Returns 200 with the import report when imported, 422 with every problem when rejected (nothing is saved), 400 for a malformed upload.")
            .Produces<ImportReport>(StatusCodes.Status200OK)
            .Produces<ImportReport>(StatusCodes.Status422UnprocessableEntity)
            .Produces<ImportReport>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/imports/{dataset}",
                async Task<Results<Ok<ImportReport>, UnprocessableEntity<ImportReport>, BadRequest<ImportReport>>> (
                    string dataset,
                    HttpRequest request,
                    IReferenceDataImportService importService,
                    CancellationToken cancellationToken) =>
                {
                    if (!ImportDatasets.TryParse(dataset, out var importDataset))
                    {
                        return TypedResults.BadRequest(ImportReport.Rejected(dataset, "unknown", null,
                            $"Unknown dataset '{dataset}'. Supported datasets: {string.Join(", ", ImportDatasets.Slugs)}, manuals."));
                    }

                    var (_, file, problem) = await ReadUploadAsync(request, dataset, ReferenceDataImportService.MaximumFileBytes, cancellationToken);
                    if (problem is not null)
                        return TypedResults.BadRequest(problem);

                    ImportFormat? format = Path.GetExtension(file!.FileName).ToLowerInvariant() switch
                    {
                        ".csv" => ImportFormat.Csv,
                        ".json" => ImportFormat.Json,
                        _ => null
                    };
                    if (format is null)
                    {
                        return TypedResults.BadRequest(ImportReport.Rejected(ImportDatasets.Slug(importDataset), "unknown",
                            file.FileName, "Structured data must be uploaded as a .csv or .json file."));
                    }

                    await using var content = file.OpenReadStream();
                    var report = await importService.ImportAsync(importDataset, format.Value, file.FileName, content, cancellationToken);
                    return report.Imported ? TypedResults.Ok(report) : TypedResults.UnprocessableEntity(report);
                })
            .WithName("ImportDataset")
            .WithSummary("Import assets, spare parts, technicians, technician availability, tasks or maintenance history")
            .WithDescription("multipart/form-data with 'file' (.csv with a header row, or .json with an array of objects). Datasets: assets, spare-parts, technicians, technician-availability, tasks, maintenance-history. Imports are all-or-nothing and insert-only. Returns 200 with the import report when every record was imported, 422 listing every invalid record and reason when rejected (nothing is saved), 400 for a malformed upload.")
            .Produces<ImportReport>(StatusCodes.Status200OK)
            .Produces<ImportReport>(StatusCodes.Status422UnprocessableEntity)
            .Produces<ImportReport>(StatusCodes.Status400BadRequest);

        return app;
    }

    private static async Task<(IFormCollection? Form, IFormFile? File, ImportReport? Problem)> ReadUploadAsync(
        HttpRequest request,
        string dataset,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
            return (null, null, ImportReport.Rejected(dataset, "unknown", null, "Send multipart/form-data with the file in a 'file' field."));

        var form = await request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file");
        if (file is null)
            return (form, null, ImportReport.Rejected(dataset, "unknown", null, "The 'file' field is missing."));
        if (file.Length > maximumBytes)
        {
            return (form, file, ImportReport.Rejected(dataset, "unknown", file.FileName,
                $"The file exceeds {maximumBytes / (1024 * 1024)} MB."));
        }

        return (form, file, null);
    }
}
