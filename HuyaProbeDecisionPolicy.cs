using LiveStreamGateway;

internal static class HuyaProbeDecisionPolicy
{
    // Unsupported routing/container modes mean "cannot verify", not proof that a working
    // production route is unhealthy. Do not switch away just because a probe is stricter.
    internal static bool IsQualityFailure(HuyaLineProbeResult result) => !result.Success &&
        result.Category is "HttpStatus" or "LengthMismatch" or "InvalidTransportStream" or
            "Timeout" or "TransportError" or "SlowDownload" or "PlaylistNotAdvancing" or "InvalidPlaylist";

    internal static HuyaLineProbeResult? SelectAlternative(string currentLineKey,
        IReadOnlyList<HuyaLineProbeResult> results, bool faultStillPresent,
        IReadOnlySet<string> isolatedLines)
    {
        var current = results.FirstOrDefault(item => item.LineKey == currentLineKey);
        if (!faultStillPresent || current == null || !IsQualityFailure(current)) return null;
        return results.Where(item => item.LineKey != currentLineKey && item.Success && item.SampleCount >= 2 &&
                double.IsFinite(item.DownloadMediaRatio) && item.DownloadMediaRatio <= 0.65 &&
                !isolatedLines.Contains(item.LineKey))
            .OrderBy(item => item.DownloadMediaRatio).FirstOrDefault();
    }
}
