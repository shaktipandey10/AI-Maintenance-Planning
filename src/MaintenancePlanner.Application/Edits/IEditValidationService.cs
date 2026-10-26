namespace MaintenancePlanner.Application.Edits;

public sealed record EditValidationRequest(
    Guid DraftId,
    IReadOnlyList<Guid> EditIds);

public sealed record ValidationViolation(
    string TaskId,
    string Code,
    string Message);

public sealed record EditValidationResult(
    Guid DraftId,
    bool IsValid,
    IReadOnlyList<ValidationViolation> Violations,
    int ValidEditsCount,
    int InvalidEditsCount);

public interface IEditValidationService
{
    /// <summary>
    /// Validate all edits for a draft without invoking the AI interpreter.
    /// Checks: window constraints, technician availability and skills, spare parts availability.
    /// Persists validation results to EditedTaskPlacement records.
    /// </summary>
    Task<EditValidationResult> ValidateEditsAsync(
        Guid draftId,
        CancellationToken cancellationToken);
}
