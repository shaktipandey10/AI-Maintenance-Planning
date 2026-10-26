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

    public async Task<string> ExportAsJsonAsync(Guid draftId, CancellationToken cancellationToken)
    {
        var draft = await dbContext.Drafts.FirstOrDefaultAsync(d => d.Id == draftId, cancellationToken);

        if (draft is null)
            throw new InvalidOperationException("Draft not found");

        if (draft.Status != DraftStatus.Approved)
            throw new InvalidOperationException("Only approved drafts can be exported");

        var progress = await progressService.GetPlanProgressAsync(draftId, cancellationToken);

        var export = new
        {
            DraftId = draft.Id,
            ApprovedAtUtc = draft.ApprovedAtUtc,
            PlanningJobId = draft.PlanningJobId,
            Progress = new
            {
                TotalRequired = progress.TotalRequired,
                CompletedQuantity = progress.CompletedQuantity,
                RemainingQuantity = progress.RemainingQuantity,
                PercentComplete = progress.TotalRequired > 0 ? (progress.CompletedQuantity / progress.TotalRequired * 100).ToString("F1") : "0"
            },
            Tasks = progress.Tasks.Select(t => new
            {
                SparePartId = t.SparePartId,
                RequiredQuantity = t.RequiredQuantity,
                CompletedQuantity = t.CompletedQuantity,
                RemainingQuantity = t.RemainingQuantity
            })
        };

        return JsonSerializer.Serialize(export, JsonOptions);
    }

    public async Task<byte[]> ExportAsPdfAsync(Guid draftId, CancellationToken cancellationToken)
    {
        var jsonExport = await ExportAsJsonAsync(draftId, cancellationToken);

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
        document.Add(new Paragraph(" "));

        // JSON data
        var jsonFont = FontFactory.GetFont(BaseFont.COURIER, 9);
        var jsonText = new Paragraph(jsonExport, jsonFont);
        document.Add(jsonText);

        document.Close();
        writer.Close();

        return memoryStream.ToArray();
    }
}
