namespace MaintenancePlanner.Application.Planning;

public interface IPlanExportService
{
    Task<string> ExportAsJsonAsync(Guid draftId, CancellationToken cancellationToken);
    Task<byte[]> ExportAsPdfAsync(Guid draftId, CancellationToken cancellationToken);
}
