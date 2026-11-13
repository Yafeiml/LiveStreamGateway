namespace LiveStreamGateway.Tests;

public class MediaTransportFaultParserTests
{
    private static readonly DateTime Start = new(2026, 11, 13, 15, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OneFragmentPrematureFailedAndDemuxReports_DoNotManufactureTwoFailures(bool identifiedFirst)
    {
        var parser = new MediaTransportFaultParser();
        var state = State();
        string[] reports = identifiedFirst
            ? [Failed(9823), Premature, DemuxFailure]
            : [Premature, Failed(9823), DemuxFailure];

        for (int index = 0; index < reports.Length; index++)
            Record(parser, state, reports[index], 100 + index);

        Assert.Equal(1, state.RecentDistinctFailureCount(At(102)));
        Assert.False(state.CanProbe("session", state.Generation, At(102), false));
        Assert.True(state.CanProbe("session", state.Generation, At(102), true));
    }

    [Fact]
    public void TwoExplicitFailedSequences_StillCountTwoDespiteAccompanyingAnonymousReports()
    {
        var parser = new MediaTransportFaultParser();
        var state = State();
        Record(parser, state, Premature, 100);
        Record(parser, state, Failed(9823), 101);
        Record(parser, state, DemuxFailure, 102);
        Record(parser, state, Failed(9824), 103);
        Record(parser, state, Premature, 104);
        Assert.Equal(2, state.RecentDistinctFailureCount(At(104)));
        Assert.True(state.CanProbe("session", state.Generation, At(104), false));
    }

    [Fact]
    public void RepeatedExplicitSequence_AndCaseVariantsRemainOneIdentity()
    {
        var parser = new MediaTransportFaultParser();
        var state = State();
        Record(parser, state, Failed(9823), 100);
        Record(parser, state, Failed(9823).ToUpperInvariant(), 101);
        Record(parser, state, Failed(9823), 102);
        Assert.Equal(1, state.RecentDistinctFailureCount(At(102)));
        Assert.False(state.CanProbe("session", state.Generation, At(102), false));
    }

    [Fact]
    public void UnidentifiedRepeatedTransportErrors_AreOneContinuousBurst()
    {
        var parser = new MediaTransportFaultParser();
        var state = State();
        Record(parser, state, Premature, 100);
        Record(parser, state, DemuxFailure, 109);
        Record(parser, state, "Error when loading first segment", 118);
        Record(parser, state, Premature, 127);
        Assert.Equal(1, state.RecentDistinctFailureCount(At(127)));
        Assert.False(state.CanProbe("session", state.Generation, At(127), false));
    }

    [Fact]
    public void ParsedTransportDetails_NeverContainUrlHostPathOrQueryCredentials()
    {
        var parser = new MediaTransportFaultParser();
        const string sensitiveUrl = "https://sensitive-cdn.invalid/private-stream/segment.ts?token=private-test-token&wsSecret=private-test-secret";
        var eof = Assert.IsType<MediaTransportFailure>(parser.Parse(Premature + " URL: " + sensitiveUrl));
        var failed = Assert.IsType<MediaTransportFailure>(parser.Parse(Failed(9823) + " " + sensitiveUrl));
        Assert.Null(eof.SegmentIdentity);
        Assert.Equal("playlist=0;sequence=9823", failed.SegmentIdentity);
        foreach (var failure in new[] { eof, failed })
        {
            Assert.DoesNotContain("sensitive-cdn", failure.ToString());
            Assert.DoesNotContain("private-stream", failure.ToString());
            Assert.DoesNotContain("private-test", failure.ToString());
            Assert.DoesNotContain("?", failure.ToString());
        }
    }

    [Theory]
    [InlineData("PES packet size mismatch")]
    [InlineData("Packet corrupt (stream = 0, dts = 1)")]
    [InlineData("Non-monotonic DTS in output stream 0:1")]
    [InlineData("Error during demuxing: Invalid data found when processing input")]
    [InlineData("Opening 'https://example.invalid/a.ts?token=fixture' for reading")]
    [InlineData("Failed to open segment unknown of playlist 0")]
    public void NonTransportOrUnprovenMessages_DoNotCreateTransportFailure(string message)
    {
        Assert.Null(new MediaTransportFaultParser().Parse(message));
    }

    [Theory]
    [InlineData("Error during demuxing: Input/output error", "transport-read-failed")]
    [InlineData("Error during demuxing: Connection reset by peer", "transport-read-failed")]
    [InlineData("Error when loading first segment", "segment-open-failed")]
    [InlineData("Stream ends prematurely at 2048, should be 8192", "premature-eof")]
    public void GenuineAnonymousTransportFailures_AreClassifiedWithoutInventingFragmentIdentity(string message, string category)
    {
        var failure = Assert.IsType<MediaTransportFailure>(new MediaTransportFaultParser().Parse(message));
        Assert.Equal(category, failure.Category);
        Assert.Null(failure.SegmentIdentity);
    }

    private const string Premature = "[https @ 000001] Stream ends prematurely at 2048, should be 8192";
    private const string DemuxFailure = "[in#0/hls @ 000002] Error during demuxing: Input/output error";
    private static string Failed(int sequence) => $"[hls @ 000003] Failed to open segment {sequence} of playlist 0";

    private static HuyaChannelLineState State()
    {
        var state = new HuyaChannelLineState("fingerprint");
        state.BindSession("session", "tx", Start);
        return state;
    }

    private static void Record(MediaTransportFaultParser parser, HuyaChannelLineState state, string message, int seconds)
    {
        var failure = Assert.IsType<MediaTransportFailure>(parser.Parse(message));
        state.RecordTransportFailure("session", state.Generation, failure.SegmentIdentity, At(seconds), At(seconds));
    }

    private static DateTime At(int seconds) => Start.AddSeconds(seconds);
}
