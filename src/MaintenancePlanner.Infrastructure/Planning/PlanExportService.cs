using System.Globalization;
using System.Text;
using System.Text.Json;
using iTextSharp.text;
using iTextSharp.text.pdf;
using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MaintenancePlanner.Infrastructure.Planning;

public sealed class PlanExportService(MaintenancePlannerDbContext dbContext, IPlanProgressService progressService) : IPlanExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<string> ExportAsJsonAsync(Guid draftId, CancellationToken cancellationToken) =>
        JsonSerializer.Serialize(await BuildExportAsync(draftId, cancellationToken), JsonOptions);

    public async Task<byte[]> ExportAsPdfAsync(Guid draftId, CancellationToken cancellationToken)
    {
        // The PDF is rendered from the same export as the JSON, so both describe the same revision and task data
        var export = await BuildExportAsync(draftId, cancellationToken);
        var jsonExport = JsonSerializer.Serialize(export, JsonOptions);

        using var memoryStream = new MemoryStream();
        var document = new Document(PageSize.A4, 50, 50, 50, 50);
        var writer = PdfWriter.GetInstance(document, memoryStream);
        document.Open();

        // Title
        var titleFont = FontFactory.GetFont(BaseFont.HELVETICA_BOLD, 16);
        var title = new Paragraph("MAINTENANCE PLAN EXPORT", titleFont);
        title.Alignment = Element.ALIGN_CENTER;
        document.Add(title);
        document.Add(new Paragraph(" "));

        // Metadata
        var normalFont = FontFactory.GetFont(BaseFont.HELVETICA, 10);
        document.Add(new Paragraph($"Draft ID: {draftId}", normalFont));
        document.Add(new Paragraph("Status: Approved", normalFont));
        document.Add(new Paragraph($"Planning job ID: {export.PlanningJobId}", normalFont));
        document.Add(new Paragraph(" "));

        // Approved task placements and their progress
        document.Add(new Paragraph("Approved task placements", FontFactory.GetFont(BaseFont.HELVETICA_BOLD, 11)));
        foreach (var placement in export.TaskPlacements)
        {
            document.Add(new Paragraph($"{placement.TaskId}: {placement.Status}", normalFont));
            document.Add(new Paragraph(
                $"{placement.Start.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture)} to " +
                $"{placement.End.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture)} by " +
                string.Join(", ", placement.TechnicianIds),
                normalFont));
        }

        document.Add(new Paragraph(" "));

        // JSON data
        var jsonFont = FontFactory.GetFont(BaseFont.COURIER, 9);
        var jsonText = new Paragraph(jsonExport, jsonFont);
        document.Add(jsonText);

        document.Close();
        writer.Close();

        return memoryStream.ToArray();
    }

    private async Task<PlanExport> BuildExportAsync(Guid draftId, CancellationToken cancellationToken)
    {
        var draft = await dbContext.Drafts.FirstOrDefaultAsync(d => d.Id == draftId, cancellationToken);

        if (draft is null)
            throw new InvalidOperationException("Draft not found");

        if (draft.Status != DraftStatus.Approved)
            throw new InvalidOperationException("Only approved drafts can be exported");

        var progress = await progressService.GetPlanProgressAsync(draftId, cancellationToken);
        var statusesByTaskId = progress.TaskStatuses.ToDictionary(t => t.TaskId, StringComparer.OrdinalIgnoreCase);

        // Task placements come from the approved candidate snapshot persisted at approval, never from a new plan
        var taskPlacements = ReadApprovedPlacements(draft.ApprovedCandidatePayload)
            .Select(placement =>
            {
                statusesByTaskId.TryGetValue(placement.TaskId, out var status);
                return new ExportedTaskPlacement(
                    placement.TaskId,
                    placement.Start,
                    placement.End,
                    placement.TechnicianIds,
                    (status?.Status ?? TaskProgressStatus.NotStarted).ToString(),
                    status?.UpdatedAtUtc);
            })
            .ToList();

        return new PlanExport(
            draft.Id,
            draft.ApprovedAtUtc,
            draft.PlanningJobId,
            new ExportedProgress(
                progress.TotalRequired,
                progress.CompletedQuantity,
                progress.RemainingQuantity,
                progress.TotalRequired > 0 ? (progress.CompletedQuantity / progress.TotalRequired * 100).ToString("F1") : "0"),
            progress.Tasks.Select(t => new ExportedPartProgress(
                t.SparePartId,
                t.RequiredQuantity,
                t.CompletedQuantity,
                t.RemainingQuantity)).ToList(),
            taskPlacements);
    }

    /// <summary>The task placements of an approved candidate snapshot, in plan order; none if it is unreadable.</summary>
    private static IReadOnlyList<(string TaskId, DateTimeOffset Start, DateTimeOffset End, IReadOnlyList<string> TechnicianIds)>
        ReadApprovedPlacements(string? snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot))
            return [];

        try
        {
            using var document = JsonDocument.Parse(snapshot);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("taskPlacements", out var placements) ||
                placements.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return placements.EnumerateArray()
                .Select(placement => (
                    placement.GetProperty("taskId").GetString()!,
                    placement.GetProperty("start").GetDateTimeOffset(),
                    placement.GetProperty("end").GetDateTimeOffset(),
                    (IReadOnlyList<string>)placement.GetProperty("technicianIds").EnumerateArray()
                        .Select(technician => technician.GetString()!)
                        .ToList()))
                .ToList();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return [];
        }
    }

    private sealed record PlanExport(
        Guid DraftId,
        DateTimeOffset? ApprovedAtUtc,
        Guid PlanningJobId,
        ExportedProgress Progress,
        IReadOnlyList<ExportedPartProgress> Tasks,
        IReadOnlyList<ExportedTaskPlacement> TaskPlacements);

    private sealed record ExportedProgress(
        decimal TotalRequired,
        decimal CompletedQuantity,
        decimal RemainingQuantity,
        string PercentComplete);

    private sealed record ExportedPartProgress(
        string SparePartId,
        decimal RequiredQuantity,
        decimal CompletedQuantity,
        decimal RemainingQuantity);

    private sealed record ExportedTaskPlacement(
        string TaskId,
        DateTimeOffset Start,
        DateTimeOffset End,
        IReadOnlyList<string> TechnicianIds,
        string Status,
        DateTimeOffset? StatusUpdatedAtUtc);
}
