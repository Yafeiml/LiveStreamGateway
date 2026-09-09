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
    public void LargeTimestampDamage_IsPreservedEvenWhenLastReportIsAWeakSkip()
    {
        var timestamp = Signal(MediaFaultKind.TimestampDiscontinuity, true);
        var skip = Signal(MediaFaultKind.SegmentSkip);
        Assert.Same(timestamp, MediaRecoveryPolicy.SelectStrongEvidence([timestamp, skip], Start));
    }

    [Fact]
    public void StartupTimestampWithoutCorroboration_IsObserved()
    {
        var timestamp = Signal(MediaFaultKind.TimestampDiscontinuity, true) with { OccurredAtUtc = Start.AddSeconds(5) };
        Assert.Null(MediaRecoveryPolicy.SelectStrongEvidence([timestamp], Start));
        Assert.NotNull(MediaRecoveryPolicy.SelectStrongEvidence([timestamp, Signal(MediaFaultKind.SegmentSkip)], Start));
    }

    [Theory]
    [InlineData(2, false, false)]
    [InlineData(3, false, false)]
    [InlineData(3, true, true)]
    [InlineData(9, false, false)]
    [InlineData(10, false, true)]
    public void NonMonotonicDts_RequiresThresholdOrIndependentBoundary(int count, bool boundary, bool expected)
    {
        var signals = Enumerable.Range(0, count).Select(_ => Signal(MediaFaultKind.NonMonotonicDts)).ToList();
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
    public void SmallTimestampAndIndependentGap_RemainStrongEvidence()
    {
        var evidence = MediaRecoveryPolicy.SelectStrongEvidence(
            [Signal(MediaFaultKind.TimestampDiscontinuity), Signal(MediaFaultKind.HuyaContinuity)], Start);
        Assert.NotNull(evidence);
        Assert.Equal(MediaFaultKind.TimestampDiscontinuity, evidence.Kind);
        Assert.True(evidence.RequiresImmediateRecovery);
    }

    [Theory]
    [InlineData(15, 0, 101, false, true)]
    [InlineData(25, 5, 102, false, true)]
    [InlineData(90, 2, 110, false, true)]
    [InlineData(14, 0, 101, false, false)]
    [InlineData(30, 11, 101, false, false)]
    [InlineData(30, 0, 100, false, false)]
    [InlineData(30, 0, 99, false, false)]
    [InlineData(90, 0, 110, true, false)]
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
