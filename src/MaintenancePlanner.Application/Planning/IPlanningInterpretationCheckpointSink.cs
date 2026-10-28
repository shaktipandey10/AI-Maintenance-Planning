namespace MaintenancePlanner.Application.Planning;

/// <summary>
/// Receives the request interpretation once it has passed every interpretation-stage check, so a planning job can
/// persist it and later resume without interpreting the request again.
/// </summary>
public interface IPlanningInterpretationCheckpointSink
{
    Task SaveValidatedInterpretationAsync(
        PlanningInterpretation interpretation,
        CancellationToken cancellationToken);
}
