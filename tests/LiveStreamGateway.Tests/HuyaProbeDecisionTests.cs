using LiveStreamGateway;

namespace LiveStreamGateway.Tests;

public sealed class HuyaProbeDecisionTests
{
    private static HuyaLineProbeResult Result(string key, bool success, string category = "Healthy", double ratio = 0.2,
        int samples = 2) => new(key, success, category, ratio, samples, 18800, TimeSpan.FromSeconds(1));

    [Fact]
    public void HealthyCurrentRouteIsNotReplacedByFasterAlternative() =>
        Assert.Null(HuyaProbeDecisionPolicy.SelectAlternative("TX",
            [Result("TX", true, ratio: 0.5), Result("AL", true, ratio: 0.1)], true, new HashSet<string>()));

    [Theory]
    [InlineData("UnsupportedPlaylist")]
    [InlineData("UnsafeUri")]
    [InlineData("UnsafeRedirect")]
    [InlineData("UnsafeSegmentUri")]
    [InlineData("UnsupportedContentEncoding")]
    public void UnverifiableCurrentRouteIsNotProofOfBadQuality(string category) =>
        Assert.Null(HuyaProbeDecisionPolicy.SelectAlternative("TX",
            [Result("TX", false, category), Result("AL", true)], true, new HashSet<string>()));

    [Theory]
    [InlineData("LengthMismatch")]
    [InlineData("InvalidTransportStream")]
    [InlineData("HttpStatus")]
    [InlineData("Timeout")]
    [InlineData("PlaylistNotAdvancing")]
    [InlineData("SlowDownload")]
    public void PersistentBadCurrentRouteCanUseCompleteHealthyAlternative(string category) =>
        Assert.Equal("AL", HuyaProbeDecisionPolicy.SelectAlternative("TX",
            [Result("TX", false, category), Result("AL", true)], true, new HashSet<string>())?.LineKey);

    [Fact]
    public void RecoveredFaultDoesNotTriggerDelayedSwitch() =>
        Assert.Null(HuyaProbeDecisionPolicy.SelectAlternative("TX",
            [Result("TX", false, "Timeout"), Result("AL", true)], false, new HashSet<string>()));

    [Fact]
    public void IsolatedSlowIncompleteOrFailedAlternativesAreNotSelected() =>
        Assert.Null(HuyaProbeDecisionPolicy.SelectAlternative("TX",
            [Result("TX", false, "Timeout"), Result("AL", true), Result("HW", true, ratio: 0.7),
             Result("HS", true, samples: 1), Result("OTHER", false, "LengthMismatch")],
            true, new HashSet<string> { "AL" }));
}
