namespace LiveStreamGateway.Tests;

public class HuyaFailoverPolicyTests
{
    private static readonly DateTime Start = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void OneFragmentWithSeveralReports_DoesNotQualifyForProbe()
    {
        var state = State();
        Assert.True(Failure(state, "fragment-10", 100));
        Assert.False(Failure(state, "fragment-10", 101));
        Assert.False(Failure(state, "fragment-10", 102));
        Assert.Equal(1, state.RecentDistinctFailureCount(At(102)));
        Assert.Equal(At(102), state.LastTransportFailureAt);
        Assert.False(state.CanProbe("session-1", state.Generation, At(102), false));
    }

    [Fact]
    public void TwoDistinctRecentFragments_QualifyForProbe()
    {
        var state = State();
        Failure(state, "fragment-10", 100);
        Failure(state, "fragment-11", 102);
        Assert.True(state.CanProbe("session-1", state.Generation, At(102), false));
    }

    [Fact]
    public void ContinuousUnknownFragmentWarnings_FormOneBurstAcrossTimeBoundaries()
    {
        var state = State();
        Assert.True(Failure(state, null, 100));
        Assert.False(Failure(state, null, 109));
        Assert.False(Failure(state, null, 118));
        Assert.False(Failure(state, null, 127));
        Assert.Equal(1, state.RecentDistinctFailureCount(At(127)));
        Assert.False(state.CanProbe("session-1", state.Generation, At(127), false));
        Assert.True(state.CanProbe("session-1", state.Generation, At(127), true));
    }

    [Fact]
    public void UnknownWarningsWithSilenceBetweenThem_FormIndependentBursts()
    {
        var state = State();
        Failure(state, null, 100);
        Failure(state, null, 111);
        Assert.Equal(2, state.RecentDistinctFailureCount(At(111)));
        Assert.True(state.CanProbe("session-1", state.Generation, At(111), false));
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 1)]
    [InlineData(true, 10)]
    [InlineData(false, 10)]
    public void NearbyAnonymousAndIdentifiedReports_CountAsOneFailureInEitherOrder(bool anonymousFirst, int gap)
    {
        var state = State();
        Assert.True(Failure(state, anonymousFirst ? null : "fragment-10", 100));
        Assert.False(Failure(state, anonymousFirst ? "fragment-10" : null, 100 + gap));
        Assert.Equal(1, state.RecentDistinctFailureCount(At(100 + gap)));
        Assert.False(state.CanProbe("session-1", state.Generation, At(100 + gap), false));
        Assert.Equal(At(100 + gap), state.LastTransportFailureAt);
    }

    [Fact]
    public void AnonymousBurstUpgradedToKnownIdentity_RemainsOneForLaterAnonymousWarnings()
    {
        var state = State();
        Assert.True(Failure(state, null, 100));
        Assert.False(Failure(state, "fragment-10", 105));
        Assert.False(Failure(state, null, 114));
        Assert.False(Failure(state, null, 123));
        Assert.False(Failure(state, "fragment-10", 124));
        Assert.Equal(1, state.RecentDistinctFailureCount(At(124)));
        Assert.False(state.CanProbe("session-1", state.Generation, At(124), false));
    }

    [Fact]
    public void AnonymousWarningsBetweenDifferentKnownFragments_DoNotHideSecondKnownFragment()
    {
        var state = State();
        Assert.True(Failure(state, "fragment-10", 100));
        Assert.False(Failure(state, null, 101));
        Assert.True(Failure(state, "fragment-11", 102));
        Assert.False(Failure(state, null, 103));
        Assert.Equal(2, state.RecentDistinctFailureCount(At(103)));
        Assert.True(state.CanProbe("session-1", state.Generation, At(103), false));
    }

    [Fact]
    public void AnonymousFirstThenTwoDifferentKnownFragments_StillCountsTwoKnownFragments()
    {
        var state = State();
        Failure(state, null, 100);
        Assert.False(Failure(state, "fragment-10", 101));
        Assert.True(Failure(state, "fragment-11", 102));
        Assert.Equal(2, state.RecentDistinctFailureCount(At(102)));
    }

    [Fact]
    public void UpgradingAnonymousBurst_DoesNotExtendOriginalEvidenceLifetime()
    {
        var state = State();
        Failure(state, null, 100);
        Failure(state, null, 109);
        Failure(state, null, 118);
        Assert.False(Failure(state, "fragment-10", 127));
        Assert.Equal(1, state.RecentDistinctFailureCount(At(127)));
        Assert.Equal(0, state.RecentDistinctFailureCount(At(131)));
    }

    [Fact]
    public void LateKnownDuplicate_CanCollapseAnAnonymousBurstWithoutAddingEvidence()
    {
        var state = State();
        Failure(state, "fragment-10", 100);
        Failure(state, null, 115);
        Assert.Equal(2, state.RecentDistinctFailureCount(At(115)));
        Assert.False(Failure(state, "fragment-10", 116));
        Assert.Equal(1, state.RecentDistinctFailureCount(At(116)));
    }

    [Fact]
    public void OutOfOrderNearbyTimestampedReports_StillMergeConservatively()
    {
        var state = State();
        state.RecordTransportFailure("session-1", state.Generation, "fragment-10", At(109), At(110));
        Assert.False(state.RecordTransportFailure("session-1", state.Generation, null, At(100), At(110)));
        Assert.Equal(1, state.RecentDistinctFailureCount(At(110)));
    }

    [Fact]
    public void ExpiredEvidence_CannotTriggerLateProbeButStaleOutputCan()
    {
        var state = State();
        Failure(state, "a", 100);
        Failure(state, "b", 101);
        Assert.False(state.CanProbe("session-1", state.Generation, At(132), false));
        Assert.Equal(0, state.RecentDistinctFailureCount(At(132)));
        Assert.True(state.CanProbe("session-1", state.Generation, At(132), true));
    }

    [Fact]
    public void OldSessionOldGenerationFutureAndStaleSignals_AreRejected()
    {
        var state = State();
        Assert.False(state.RecordTransportFailure("old-session", state.Generation, "a", At(100), At(100)));
        Assert.False(state.RecordTransportFailure("session-1", state.Generation - 1, "a", At(100), At(100)));
        Assert.False(state.RecordTransportFailure("session-1", state.Generation, "a", At(101), At(100)));
        Assert.False(state.RecordTransportFailure("session-1", state.Generation, "a", At(69), At(100)));
        Assert.Equal(0, state.RecentDistinctFailureCount(At(100)));
        Assert.Null(state.LastTransportFailureAt);
    }

    [Fact]
    public void SignalsBeforeSessionCreation_AreRejected()
    {
        var state = new HuyaChannelLineState("source");
        var generation = state.BindSession("session-1", "tx", At(100));
        Assert.False(state.RecordTransportFailure("session-1", generation, "a", At(99), At(101)));
    }

    [Fact]
    public void InitialDwellAndProbeCooldown_HaveExactBoundaries()
    {
        var state = State();
        Assert.False(state.CanProbe("session-1", state.Generation, At(89), true));
        Assert.True(state.CanProbe("session-1", state.Generation, At(90), true));
        state.MarkProbeStarted(At(90));
        Assert.False(state.CanProbe("session-1", state.Generation, At(119), true));
        Assert.True(state.CanProbe("session-1", state.Generation, At(120), true));
    }

    [Fact]
    public void ProbeFailure_IsolatesCandidateUntilExactExpiry()
    {
        var state = State();
        state.RecordProbeFailure("al", At(100));
        Assert.True(state.IsLineIsolated("al", At(279)));
        Assert.False(state.IsLineIsolated("al", At(280)));
        Assert.False(state.IsLineIsolated("tx", At(100)));
    }

    [Fact]
    public void RepeatedProbeFailure_DoesNotShortenIsolation()
    {
        var state = State();
        state.RecordProbeFailure("al", At(100));
        state.RecordProbeFailure("al", At(110));
        state.RecordProbeFailure("al", At(90));
        Assert.True(state.IsLineIsolated("al", At(289)));
        Assert.False(state.IsLineIsolated("al", At(290)));
    }

    [Fact]
    public void AllBadBatches_BackOffExponentiallyAndRemainCapped()
    {
        var state = State();
        state.MarkAllCandidatesFailed(At(100));
        Assert.Equal(At(130), state.AllCandidatesRetryAtUtc);
        Assert.False(state.CanProbe("session-1", state.Generation, At(129), true));
        Assert.True(state.CanProbe("session-1", state.Generation, At(130), true));
        state.MarkAllCandidatesFailed(At(130));
        Assert.Equal(At(190), state.AllCandidatesRetryAtUtc);
        state.MarkAllCandidatesFailed(At(190));
        Assert.Equal(At(310), state.AllCandidatesRetryAtUtc);
        state.MarkAllCandidatesFailed(At(310));
        Assert.Equal(At(550), state.AllCandidatesRetryAtUtc);
        state.MarkAllCandidatesFailed(At(550));
        Assert.Equal(At(850), state.AllCandidatesRetryAtUtc);
        state.MarkAllCandidatesFailed(At(850));
        Assert.Equal(At(1150), state.AllCandidatesRetryAtUtc);
    }

    [Fact]
    public void Switch_RequiresCurrentIdentityNonIsolatedDifferentRouteAndDwell()
    {
        var state = State();
        long generation = state.Generation;
        state.RecordProbeFailure("hs", At(100));
        Assert.False(state.TrySelectLine("old-session", generation, "al", At(100)));
        Assert.False(state.TrySelectLine("session-1", generation - 1, "al", At(100)));
        Assert.False(state.TrySelectLine("session-1", generation, "hs", At(100)));
        Assert.False(state.TrySelectLine("session-1", generation, "tx", At(100)));
        Assert.False(state.TrySelectLine("session-1", generation, "al", At(89)));
        Assert.Equal("tx", state.SelectedLineKey);
        Assert.True(state.TrySelectLine("session-1", generation, "al", At(100)));
        Assert.Equal("al", state.SelectedLineKey);
        Assert.Equal(generation + 1, state.Generation);
        Assert.False(state.TrySelectLine("session-1", generation, "hs", At(300)));
    }

    [Fact]
    public void SuccessfulSwitch_ClearsOldEvidenceAndAllBadBackoff()
    {
        var state = State();
        Failure(state, "a", 100);
        Failure(state, "b", 101);
        state.MarkAllCandidatesFailed(At(100));
        Assert.True(state.TrySelectLine("session-1", state.Generation, "al", At(130)));
        Assert.Equal(0, state.RecentDistinctFailureCount(At(130)));
        Assert.Null(state.LastTransportFailureAt);
        Assert.Null(state.AllCandidatesRetryAtUtc);
        Assert.False(state.CanProbe("session-1", state.Generation, At(219), true));
        Assert.True(state.CanProbe("session-1", state.Generation, At(220), true));
    }

    [Fact]
    public void NewSession_PreservesRouteIsolationAndCooldownButClearsFailures()
    {
        var state = State();
        Failure(state, "a", 100);
        state.RecordProbeFailure("al", At(100));
        state.MarkProbeStarted(At(100));
        long previous = state.Generation;
        long current = state.BindSession("session-2", "tx", At(110));
        Assert.Equal(previous + 1, current);
        Assert.Equal("tx", state.SelectedLineKey);
        Assert.Equal(Start, state.SelectedAtUtc);
        Assert.True(state.IsLineIsolated("al", At(110)));
        Assert.Equal(0, state.RecentDistinctFailureCount(At(110)));
        Assert.Null(state.LastTransportFailureAt);
        Assert.False(state.CanProbe("session-2", current, At(129), true));
        Assert.True(state.CanProbe("session-2", current, At(130), true));
        Assert.False(state.IsCurrent("session-1", previous));
        Assert.Equal(current, state.BindSession("session-2", "tx", At(120)));
    }

    [Fact]
    public void SourceChange_ForgetsPreferencesAndPenaltiesButUnchangedSourceDoesNot()
    {
        var state = State();
        state.RecordProbeFailure("al", At(100));
        state.MarkProbeStarted(At(100));
        state.MarkAllCandidatesFailed(At(100));
        long previous = state.Generation;
        Assert.False(state.ResetSource("source"));
        Assert.Equal(previous, state.Generation);
        Assert.True(state.IsLineIsolated("al", At(101)));
        Assert.True(state.ResetSource("new-source"));
        Assert.Equal("new-source", state.SourceFingerprint);
        Assert.Null(state.SelectedLineKey);
        Assert.Null(state.SelectedAtUtc);
        Assert.Null(state.LastProbeAtUtc);
        Assert.Null(state.AllCandidatesRetryAtUtc);
        Assert.False(state.IsLineIsolated("al", At(101)));
        Assert.False(state.IsCurrent("session-1", previous));
    }

    [Fact]
    public void SharedRecoveryBudget_EnforcesCooldownAcrossAllCauseTypes()
    {
        var budget = new AutomaticRecoveryBudget();
        Assert.True(budget.TryAcquire("media-session", Start, out var first));
        Assert.Equal(Start, first);
        Assert.False(budget.TryAcquire("manifest-session", At(89), out var retry));
        Assert.Equal(At(90), retry);
        Assert.True(budget.TryAcquire("exit-session", At(90), out _));
        Assert.Equal(At(180), budget.GetRetryAt("route-switch-session", At(91)));
    }

    [Fact]
    public void SharedRecoveryBudget_AllowsAtMostThreeActionsInRollingTenMinutes()
    {
        var budget = new AutomaticRecoveryBudget();
        Assert.True(budget.TryAcquire("s1", Start, out _));
        Assert.True(budget.TryAcquire("s2", At(90), out _));
        Assert.True(budget.TryAcquire("s3", At(180), out _));
        Assert.False(budget.TryAcquire("s4", At(270), out var retry));
        Assert.Equal(At(600), retry);
        Assert.False(budget.TryAcquire("s4", At(599), out _));
        Assert.True(budget.TryAcquire("s4", At(600), out _));
        Assert.Equal(At(690), budget.GetRetryAt("s5", At(600)));
    }

    [Fact]
    public void SameSession_CannotSpendBudgetTwiceEvenAfterWindowExpires()
    {
        var budget = new AutomaticRecoveryBudget();
        Assert.True(budget.TryAcquire("same-session", Start, out _));
        Assert.Equal(DateTime.MaxValue, budget.GetRetryAt("same-session", At(3600)));
        Assert.False(budget.TryAcquire("same-session", At(3600), out var retry));
        Assert.Equal(DateTime.MaxValue, retry);
        Assert.True(budget.TryAcquire("new-session", At(3600), out _));
    }

    [Fact]
    public void BudgetAvailabilityChecksAndRejectedAcquisition_DoNotConsumeActions()
    {
        var budget = new AutomaticRecoveryBudget();
        Assert.Equal(Start, budget.GetRetryAt("s1", Start));
        Assert.Equal(Start, budget.GetRetryAt("s1", Start));
        Assert.Null(budget.LastActionAtUtc);
        Assert.True(budget.TryAcquire("s1", Start, out _));
        Assert.False(budget.TryAcquire("s2", At(1), out _));
        Assert.Equal(Start, budget.LastActionAtUtc);
        Assert.True(budget.TryAcquire("s2", At(90), out _));
    }

    private static HuyaChannelLineState State()
    {
        var state = new HuyaChannelLineState("source");
        state.BindSession("session-1", "tx", Start);
        return state;
    }

    private static bool Failure(HuyaChannelLineState state, string? identity, int seconds) =>
        state.RecordTransportFailure(state.SessionId, state.Generation, identity, At(seconds), At(seconds));

    private static DateTime At(int seconds) => Start.AddSeconds(seconds);
}
