namespace MaintenancePlanner.Application.Planning.Tools;

/// <summary>The planning job a model may save candidates for: set by the workflow from the request, never by the model.</summary>
public sealed record ModelCandidateContext(
    IReadOnlyList<string> AssetIds,
    string WindowId,
    IReadOnlyList<string> CompletedTaskIds,
    IReadOnlyDictionary<string, decimal> ReleasableReservedQuantities);

/// <summary>A candidate the model saved with <c>save_candidate</c>, already validated and costed by the application.</summary>
public sealed record ModelCandidateProposal(int Number, PlanningCandidate Candidate);

/// <summary>
/// AI03 draft saving: the candidates the model saved during the current planning job. Scoped to the job's processing
/// scope, so each job (and each attempt) starts empty. Nothing here is persisted directly: the workflow adds the saved
/// candidates to the job's unapproved candidate result, which the worker stores with the existing lease-guarded
/// completion. Approval stays the separate reviewer action that revalidates the selected candidate.
/// </summary>
public sealed class ModelCandidateProposals
{
    public const int MaximumProposals = 3;

    private readonly object _gate = new();
    private readonly List<ModelCandidateProposal> _proposals = [];
    private ModelCandidateContext? _context;

    public ModelCandidateContext? Context
    {
        get { lock (_gate) return _context; }
    }

    public IReadOnlyList<ModelCandidateProposal> Proposals
    {
        get { lock (_gate) return _proposals.ToArray(); }
    }

    /// <summary>Starts a planning job's interpretation: sets its context and discards any earlier proposals.</summary>
    public void Begin(ModelCandidateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_gate)
        {
            _context = context;
            _proposals.Clear();
        }
    }

    /// <summary>
    /// Keeps a validated candidate. Saving the same placements again returns the earlier number; once
    /// <see cref="MaximumProposals"/> distinct candidates are kept, further ones are refused (number null).
    /// </summary>
    public (int? Number, bool AlreadySaved) Add(PlanningCandidate candidate)
    {
        lock (_gate)
        {
            var existing = _proposals.FirstOrDefault(proposal => SamePlacements(proposal.Candidate, candidate));
            if (existing is not null)
                return (existing.Number, true);

            if (_proposals.Count >= MaximumProposals)
                return (null, false);

            var proposal = new ModelCandidateProposal(_proposals.Count + 1, candidate);
            _proposals.Add(proposal);
            return (proposal.Number, false);
        }
    }

    /// <summary>Same tasks at the same times with the same crews, regardless of order.</summary>
    public static bool SamePlacements(PlanningCandidate left, PlanningCandidate right) =>
        left.TaskPlacements.Count == right.TaskPlacements.Count &&
        left.TaskPlacements.OrderBy(placement => placement.TaskId, StringComparer.OrdinalIgnoreCase)
            .Zip(right.TaskPlacements.OrderBy(placement => placement.TaskId, StringComparer.OrdinalIgnoreCase))
            .All(pair =>
                string.Equals(pair.First.TaskId, pair.Second.TaskId, StringComparison.OrdinalIgnoreCase) &&
                pair.First.Start == pair.Second.Start &&
                pair.First.End == pair.Second.End &&
                pair.First.TechnicianIds.ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals(pair.Second.TechnicianIds));
}
