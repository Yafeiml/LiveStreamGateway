namespace LiveStreamGateway.Tests;

public class MediaRecoveryPolicyTests
{
    private static readonly DateTime Start = new(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FaultTime = Start.AddMinutes(2);

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public void ActualPlaylistGap_IsObservedWithoutImmediateRestart(int missing)
    {
        var previous = Playlist(100, "a", "b", "c");
        var current = Playlist(103 + missing, "d", "e", "f");
        var assessment = HlsPlaylistContinuity.Compare(previous, current);
        Assert.Equal("sequence-gap", assessment.Category);
        Assert.Equal(missing, assessment.SkippedSegments);
        Assert.True(assessment.RequiresDiscontinuity);
        Assert.False(assessment.RequiresImmediateRecovery);
    }

    [Fact]
    public void AdjacentWindows_AreContinuous()
    {
        var assessment = HlsPlaylistContinuity.Compare(Playlist(100, "a", "b", "c"), Playlist(103, "d", "e", "f"));
        Assert.False(assessment.RequiresDiscontinuity);
    }

    [Fact]
    public void SequenceRegression_RemainsStrongEvidence()
    {
        var assessment = HlsPlaylistContinuity.Compare(Playlist(100, "a", "b", "c"), Playlist(90, "d", "e", "f"));
        Assert.True(assessment.RequiresImmediateRecovery);
        var signal = Signal(MediaFaultKind.HuyaContinuity, true, "sequence-regression");
        Assert.Same(signal, MediaRecoveryPolicy.SelectStrongEvidence([signal], Start));
    }

    [Fact]
    public void ProxyAndFfmpegReportsOfRepeatedSkips_DoNotProveTimelineDamage()
    {
        MediaFaultSignal[] signals = Enumerable.Range(0, 12).Select(i =>
            Signal(i % 2 == 0 ? MediaFaultKind.HuyaContinuity : MediaFaultKind.SegmentSkip,
                false, i % 2 == 0 ? "sequence-gap" : "segment-skip") with { Weight = 7 }).ToArray();
        Assert.Null(MediaRecoveryPolicy.SelectStrongEvidence(signals, Start));
    }

    [Fact]
    public void IsolatedLargeTimestampCorrectionAndSkip_DoNotProvePermanentDamage()
    {
        var timestamp = Signal(MediaFaultKind.TimestampDiscontinuity, true);
        var skip = Signal(MediaFaultKind.SegmentSkip);
        Assert.Null(MediaRecoveryPolicy.SelectStrongEvidence([timestamp, skip], Start));
    }

    [Fact]
    public void StartupTimestampWithoutCorroboration_IsObserved()
    {
        var timestamp = Signal(MediaFaultKind.TimestampDiscontinuity, true) with { OccurredAtUtc = Start.AddSeconds(5) };
        Assert.Null(MediaRecoveryPolicy.SelectStrongEvidence([timestamp], Start));
        Assert.Null(MediaRecoveryPolicy.SelectStrongEvidence(
            [timestamp, Signal(MediaFaultKind.SegmentSkip) with { OccurredAtUtc = Start.AddSeconds(6) }], Start));
    }

    [Theory]
    [InlineData(2, false, false)]
    [InlineData(3, false, false)]
    [InlineData(3, true, true)]
    [InlineData(9, false, false)]
    [InlineData(10, false, true)]
    public void NonMonotonicDts_RequiresThresholdOrIndependentBoundary(int count, bool boundary, bool expected)
    {
        var signals = Enumerable.Range(0, count).Select(i => Signal(MediaFaultKind.NonMonotonicDts) with
        {
            OccurredAtUtc = FaultTime.AddSeconds(i * 3),
            Detail = "dts " + i
        }).ToList();
        if (boundary) signals.Add(Signal(MediaFaultKind.SegmentSkip));
        var evidence = MediaRecoveryPolicy.SelectStrongEvidence(signals, Start);
        Assert.Equal(expected, evidence != null);
        if (evidence != null)
        {
            Assert.Equal(MediaFaultKind.NonMonotonicDts, evidence.Kind);
            Assert.True(evidence.RequiresImmediateRecovery);
        }
    }

    [Fact]
    public void SmallTimestampAndIndependentGap_AreObserved()
    {
        var evidence = MediaRecoveryPolicy.SelectStrongEvidence(
            [Signal(MediaFaultKind.TimestampDiscontinuity), Signal(MediaFaultKind.HuyaContinuity)], Start);
        Assert.Null(evidence);
    }

    [Fact]
    public void RepeatedVideoAndAudioCorrectionsOfOneBoundary_AreOneBurst()
    {
        var signals = Enumerable.Range(0, 12).Select(i => Signal(MediaFaultKind.TimestampDiscontinuity, true) with
        {
            OccurredAtUtc = FaultTime.AddMilliseconds(i * 100),
            Detail = "track " + i
        }).ToArray();
        Assert.Null(MediaRecoveryPolicy.SelectStrongEvidence(signals, Start));
    }

    [Fact]
    public void SustainedTimestampCorrections_RemainStrongEvenWhenOutputAdvances()
    {
        var signals = new[] { 0, 3, 6 }.Select(i => Signal(MediaFaultKind.TimestampDiscontinuity) with
        {
            OccurredAtUtc = FaultTime.AddSeconds(i)
        }).ToArray();
        var evaluation = MediaRecoveryPolicy.EvaluatePendingRecovery(signals[0], signals[^1].OccurredAtUtc,
            signals, new HlsOutputProgress(100, FaultTime),
            new HlsOutputProgress(110, FaultTime.AddSeconds(20)), Start, FaultTime.AddSeconds(20));
        Assert.Equal(MediaRecoveryDisposition.Recover, evaluation.Disposition);
        Assert.Equal("fresh-strong-evidence", evaluation.Reason);
        Assert.True(evaluation.Evidence!.RequiresImmediateRecovery);
    }

    [Fact]
    public void DuplicateDtsReports_DoNotMeetThreshold()
    {
        var duplicates = Enumerable.Range(0, 20).Select(i => Signal(MediaFaultKind.NonMonotonicDts) with
        {
            OccurredAtUtc = FaultTime.AddMilliseconds(i * 10)
        }).Append(Signal(MediaFaultKind.SegmentSkip)).ToArray();
        Assert.Null(MediaRecoveryPolicy.SelectStrongEvidence(duplicates, Start));
    }

    [Fact]
    public void FaultsFromOtherSessionAndProcess_CannotCorroborateCurrentFault()
    {
        var signal = Signal(MediaFaultKind.TimestampDiscontinuity);
        var otherSession = signal with { SessionId = "old-session", OccurredAtUtc = FaultTime.AddSeconds(3) };
        var otherProcess = signal with { ProcessId = 456, OccurredAtUtc = FaultTime.AddSeconds(6) };
        Assert.Null(MediaRecoveryPolicy.SelectStrongEvidence([signal, otherSession, otherProcess], Start));
        var evaluation = MediaRecoveryPolicy.EvaluatePendingRecovery(signal, FaultTime,
            [otherSession, otherProcess], new HlsOutputProgress(100, FaultTime),
            new HlsOutputProgress(110, FaultTime.AddSeconds(31)), Start, FaultTime.AddSeconds(31));
        Assert.Equal(MediaRecoveryDisposition.CancelObservation, evaluation.Disposition);
    }

    [Fact]
    public void StrongEvidence_ExpiresBeforeDelayedRecovery()
    {
        var regression = Signal(MediaFaultKind.HuyaContinuity, true, "sequence-regression");
        var now = FaultTime.AddSeconds(280);
        Assert.Null(MediaRecoveryPolicy.SelectStrongEvidence([regression], Start, now));
        var evaluation = MediaRecoveryPolicy.EvaluatePendingRecovery(regression, FaultTime, [regression],
            new HlsOutputProgress(100, FaultTime), new HlsOutputProgress(180, now), Start, now);
        Assert.Equal(MediaRecoveryDisposition.CancelObservation, evaluation.Disposition);
        Assert.Null(evaluation.Evidence);
    }

    [Fact]
    public void FreshRegression_CannotBeCancelledOnlyBecausePlaylistGrows()
    {
        var regression = Signal(MediaFaultKind.HuyaContinuity, true, "sequence-regression");
        var now = FaultTime.AddSeconds(20);
        var evaluation = MediaRecoveryPolicy.EvaluatePendingRecovery(regression, FaultTime, [regression],
            new HlsOutputProgress(100, FaultTime), new HlsOutputProgress(110, now), Start, now);
        Assert.Equal(MediaRecoveryDisposition.Recover, evaluation.Disposition);
        Assert.Same(regression, evaluation.Evidence);
    }

    [Fact]
    public void FreshDtsAfterOldStrongSignal_PreventsOutputOnlyCancellation()
    {
        var regression = Signal(MediaFaultKind.HuyaContinuity, true, "sequence-regression");
        var now = FaultTime.AddSeconds(280);
        var freshDts = Signal(MediaFaultKind.NonMonotonicDts) with { OccurredAtUtc = now.AddSeconds(-3) };
        var evaluation = MediaRecoveryPolicy.EvaluatePendingRecovery(regression, freshDts.OccurredAtUtc,
            [freshDts], new HlsOutputProgress(100, FaultTime), new HlsOutputProgress(180, now), Start, now);
        Assert.Equal(MediaRecoveryDisposition.Observe, evaluation.Disposition);
        Assert.Equal("await-timeline-quiet", evaluation.Reason);
    }

    [Fact]
    public void SustainedDtsCannotBeCancelledByFreshOutput()
    {
        var signals = Enumerable.Range(0, 10).Select(i => Signal(MediaFaultKind.NonMonotonicDts) with
        {
            OccurredAtUtc = FaultTime.AddSeconds(i), Detail = "dts " + i
        }).ToArray();
        var now = FaultTime.AddSeconds(15);
        var evaluation = MediaRecoveryPolicy.EvaluatePendingRecovery(signals[0], signals[^1].OccurredAtUtc,
            signals, new HlsOutputProgress(100, FaultTime), new HlsOutputProgress(110, now), Start, now);
        Assert.Equal(MediaRecoveryDisposition.Recover, evaluation.Disposition);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrStaleOutput_CorroboratesOldPendingFault(bool stale)
    {
        var signal = Signal(MediaFaultKind.TimestampDiscontinuity, true);
        var now = FaultTime.AddMinutes(4);
        var current = stale ? new HlsOutputProgress(110, now.AddSeconds(-35)) : null;
        var evaluation = MediaRecoveryPolicy.EvaluatePendingRecovery(signal, FaultTime, [],
            new HlsOutputProgress(100, FaultTime), current, Start, now);
        Assert.Equal(MediaRecoveryDisposition.Recover, evaluation.Disposition);
        Assert.Equal("output-stalled", evaluation.Reason);
        Assert.Equal(now, evaluation.Evidence!.OccurredAtUtc);
    }

    [Fact]
    public void RewrittenPlaylistWithoutNewMedia_IsNotHealthyOutput()
    {
        var signal = Signal(MediaFaultKind.SegmentSkip);
        var now = FaultTime.AddSeconds(31);
        var evaluation = MediaRecoveryPolicy.EvaluatePendingRecovery(signal, FaultTime, [],
            new HlsOutputProgress(100, FaultTime), new HlsOutputProgress(100, now), Start, now);
        Assert.Equal(MediaRecoveryDisposition.Recover, evaluation.Disposition);
        Assert.Equal("output-stalled", evaluation.Reason);
    }

    [Fact]
    public void StartupWithoutOutput_HasFullObservationGrace()
    {
        var signal = Signal(MediaFaultKind.TimestampDiscontinuity, true) with { OccurredAtUtc = Start.AddSeconds(5) };
        var evaluation = MediaRecoveryPolicy.EvaluatePendingRecovery(signal, signal.OccurredAtUtc, [],
            null, null, Start, Start.AddSeconds(20));
        Assert.Equal(MediaRecoveryDisposition.Observe, evaluation.Disposition);
    }

    [Fact]
    public void NewWeakFaultDuringCooldown_DoesNotRenewExpiredStrongEvidence()
    {
        var signal = Signal(MediaFaultKind.HuyaContinuity, true, "sequence-regression");
        var now = FaultTime.AddMinutes(4);
        var weak = Signal(MediaFaultKind.SegmentSkip) with { OccurredAtUtc = now.AddSeconds(-1) };
        var evaluation = MediaRecoveryPolicy.EvaluatePendingRecovery(signal, weak.OccurredAtUtc, [weak],
            new HlsOutputProgress(100, FaultTime), new HlsOutputProgress(180, now), Start, now);
        Assert.Equal(MediaRecoveryDisposition.Observe, evaluation.Disposition);
        Assert.Null(evaluation.Evidence);
    }

    [Fact]
    public void FutureSignalsAndOutput_CannotEstablishRecoveryOrHealth()
    {
        var future = Signal(MediaFaultKind.HuyaContinuity, true, "sequence-regression") with
        {
            OccurredAtUtc = FaultTime.AddSeconds(60)
        };
        Assert.Null(MediaRecoveryPolicy.SelectStrongEvidence([future], Start, FaultTime));
        var evaluation = MediaRecoveryPolicy.EvaluatePendingRecovery(Signal(MediaFaultKind.SegmentSkip), FaultTime,
            [], new HlsOutputProgress(100, FaultTime), new HlsOutputProgress(110, FaultTime.AddSeconds(60)),
            Start, FaultTime.AddSeconds(20));
        Assert.Equal(MediaRecoveryDisposition.Observe, evaluation.Disposition);
    }

    [Theory]
    [InlineData(15, 0, 101, false, true)]
    [InlineData(25, 5, 102, false, true)]
    [InlineData(90, 2, 110, false, true)]
    [InlineData(14, 0, 101, false, false)]
    [InlineData(30, 11, 101, false, false)]
    [InlineData(30, 0, 100, false, false)]
    [InlineData(30, 0, 99, false, false)]
    [InlineData(90, 0, 110, true, true)]
    public void PendingRecovery_RechecksRealOutputProgress(
        int elapsedSeconds, int outputAgeSeconds, long lastSequence, bool strong, bool expected)
    {
        var signal = Signal(MediaFaultKind.HuyaContinuity, strong);
        var now = FaultTime.AddSeconds(elapsedSeconds);
        var baseline = new HlsOutputProgress(100, FaultTime);
        var current = new HlsOutputProgress(lastSequence, now.AddSeconds(-outputAgeSeconds));
        Assert.Equal(expected, MediaRecoveryPolicy.CanCancelObservation(signal, baseline, current, now));
    }

    [Fact]
    public void MissingOutput_CannotCancelPendingRecovery()
    {
        Assert.False(MediaRecoveryPolicy.CanCancelObservation(Signal(MediaFaultKind.SegmentSkip),
            new HlsOutputProgress(100, FaultTime), null, FaultTime.AddSeconds(90)));
    }

    [Fact]
    public void InitialOutputAfterStartup_CanClearObservation()
    {
        Assert.True(MediaRecoveryPolicy.CanCancelObservation(Signal(MediaFaultKind.SegmentSkip), null,
            new HlsOutputProgress(100, FaultTime.AddSeconds(20)), FaultTime.AddSeconds(22)));
    }

    [Fact]
    public void OutputProgress_TracksGrowingStartupWindowAndRejectsEmptyOrInvalidOutput()
    {
        const string header = "#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:100\n";
        Assert.Equal(100, HlsOutputProgress.Parse(header + "#EXTINF:3,\na.ts\n", FaultTime)!.LastSequence);
        Assert.Equal(101, HlsOutputProgress.Parse(header + "#EXTINF:3,\na.ts\n#EXTINF:3,\nb.ts\n", FaultTime)!.LastSequence);
        Assert.Null(HlsOutputProgress.Parse(header, FaultTime));
        Assert.Null(HlsOutputProgress.Parse("#EXTM3U\na.ts\n", FaultTime));
        Assert.Null(HlsOutputProgress.Parse("#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:9223372036854775807\na.ts\nb.ts\n", FaultTime));
    }

    [Theory]
    [InlineData("huya", true)]
    [InlineData("HUYA", true)]
    [InlineData("douyu", false)]
    [InlineData("bilibili", false)]
    public void PersistentConnectionOverride_IsLimitedToHuya(string platform, bool disabled)
    {
        var options = FfmpegInputOptions.ForPlatform(platform);
        if (disabled) Assert.Equal(new[] { "-http_persistent", "0" }, options);
        else Assert.Empty(options);
    }

    private static MediaFaultSignal Signal(MediaFaultKind kind, bool strong = false, string category = "test") =>
        new("channel", "session", 123, FaultTime, kind, category, "test evidence", strong);

    private static HlsMediaPlaylistSnapshot Playlist(long sequence, params string[] segments) =>
        new() { MediaSequence = sequence, SegmentIdentities = segments.ToList() };

    [Fact]
    public void NewSessionRecovery_DoesNotClearAnotherConfirmedPendingFault()
    {
        string id = "recovery-test-" + Guid.NewGuid().ToString("N");
        try
        {
            Globals.Metrics[id] = new ChannelMetrics
            {
                ContinuityRecoveryInProgress = true,
                ContinuityRecoverySessionId = "new-session",
                ContinuityRecoveryPending = true,
                ContinuityPendingSessionId = "new-session",
                ContinuityPendingUntil = FaultTime.AddSeconds(90),
                ContinuityDegraded = true
            };
            Assert.True(Globals.MarkContinuityRecovered(id, "new-session"));
            Assert.True(Globals.Metrics[id].ContinuityRecoveryPending);
            Assert.True(Globals.Metrics[id].ContinuityDegraded);
            Assert.Equal("new-session", Globals.Metrics[id].ContinuityPendingSessionId);
        }
        finally { Globals.Metrics.TryRemove(id, out _); }
    }
}
