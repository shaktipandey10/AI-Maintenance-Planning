using System.Collections.ObjectModel;
using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Application.Planning.Tools;

namespace MaintenancePlanner.UnitTests;

/// <summary>AI03 stage 4: the per-job store of model-saved candidates.</summary>
public sealed class AI03ModelCandidateProposalsTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 8, 0, 0, TimeSpan.FromHours(5.5));

    private static ModelCandidateContext Context(string windowId = "WINDOW-1") =>
        new(["A-1"], windowId, [], ReadOnlyDictionary<string, decimal>.Empty);

    private static PlanningCandidate Candidate(int startHourOffset, params string[] technicians) =>
        new([new PlanningTaskPlacement("A1-PREP", Start.AddHours(startHourOffset), Start.AddHours(startHourOffset + 1), technicians)], "r", []);

    [Fact]
    public void SameCandidateSavedAgain_ReturnsTheSameNumber_RegardlessOfTechnicianOrder()
    {
        var store = new ModelCandidateProposals();
        store.Begin(Context());

        Assert.Equal((1, false), store.Add(Candidate(0, "T-1", "T-2")));
        Assert.Equal((1, true), store.Add(Candidate(0, "T-2", "T-1")));
        Assert.Equal((2, false), store.Add(Candidate(1, "T-1")));
        Assert.Equal(2, store.Proposals.Count);
    }

    [Fact]
    public void BeyondTheLimit_FurtherDistinctCandidatesAreRefused()
    {
        var store = new ModelCandidateProposals();
        store.Begin(Context());
        for (var i = 0; i < ModelCandidateProposals.MaximumProposals; i++)
            Assert.NotNull(store.Add(Candidate(i, "T-1")).Number);

        Assert.Null(store.Add(Candidate(9, "T-1")).Number);
        Assert.Equal((1, true), store.Add(Candidate(0, "T-1")));
        Assert.Equal(ModelCandidateProposals.MaximumProposals, store.Proposals.Count);
    }

    [Fact]
    public void Begin_StartsANewJob_WithItsOwnContextAndNoEarlierProposals()
    {
        var store = new ModelCandidateProposals();
        Assert.Null(store.Context);
        store.Begin(Context("WINDOW-1"));
        store.Add(Candidate(0, "T-1"));

        store.Begin(Context("WINDOW-2"));

        Assert.Equal("WINDOW-2", store.Context!.WindowId);
        Assert.Empty(store.Proposals);
    }

    [Fact]
    public async Task ConcurrentSavesOfTheSameCandidate_KeepExactlyOne()
    {
        var store = new ModelCandidateProposals();
        store.Begin(Context());

        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => store.Add(Candidate(0, "T-1")))));

        Assert.Single(store.Proposals);
        Assert.All(results, result => Assert.Equal(1, result.Number));
        Assert.Single(results, result => !result.AlreadySaved);
    }
}
