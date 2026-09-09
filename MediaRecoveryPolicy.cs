using System.Globalization;

/// <summary>区分传输抖动与需要重建解码时间线的故障，避免把同一次跳段的多路报告当作损坏。</summary>
internal static class MediaRecoveryPolicy
{
    internal const int ObservationSeconds = 30;
    internal const int StableOutputSeconds = 15;
    internal const int FreshOutputSeconds = 10;

    internal static MediaFaultSignal? SelectStrongEvidence(
        IReadOnlyList<MediaFaultSignal> recent, DateTime sessionCreatedAtUtc)
    {
        var boundary = recent.LastOrDefault(item =>
            item.Kind is MediaFaultKind.SegmentSkip or MediaFaultKind.HuyaContinuity);
        var regression = recent.LastOrDefault(item =>
            item.Kind == MediaFaultKind.HuyaContinuity && item.RequiresImmediateRecovery);
        if (regression != null) return regression;

        var largeTimestamp = recent.LastOrDefault(item =>
            item.Kind == MediaFaultKind.TimestampDiscontinuity && item.RequiresImmediateRecovery &&
            (item.OccurredAtUtc - sessionCreatedAtUtc >= TimeSpan.FromSeconds(20) || boundary != null));
        if (largeTimestamp != null) return largeTimestamp;

        var timestamps = recent.Where(item => item.Kind == MediaFaultKind.TimestampDiscontinuity).ToArray();
        var dts = recent.Where(item => item.Kind == MediaFaultKind.NonMonotonicDts).ToArray();
        if (dts.Length >= 10 || (dts.Length >= 3 && (boundary != null || timestamps.Length > 0)))
            return dts[^1] with { RequiresImmediateRecovery = true };
        if (timestamps.Length > 0 && boundary != null)
            return timestamps[^1] with { RequiresImmediateRecovery = true };

        // 多次 sequence-gap / segment-skip 仍只能证明取流落后，不能证明音视频解码状态损坏。
        return null;
    }

    internal static bool CanCancelObservation(
        MediaFaultSignal signal, HlsOutputProgress? baseline, HlsOutputProgress? current, DateTime now)
    {
        if (signal.RequiresImmediateRecovery || current == null) return false;
        return current.WrittenAtUtc >= signal.OccurredAtUtc.AddSeconds(StableOutputSeconds) &&
            current.WrittenAtUtc <= now &&
            now - current.WrittenAtUtc <= TimeSpan.FromSeconds(FreshOutputSeconds) &&
            (baseline == null || current.LastSequence > baseline.LastSequence);
    }
}

internal sealed record HlsOutputProgress(long LastSequence, DateTime WrittenAtUtc)
{
    internal static HlsOutputProgress? Parse(string playlist, DateTime writtenAtUtc)
    {
        const string prefix = "#EXT-X-MEDIA-SEQUENCE:";
        long? first = null;
        int segments = 0;
        foreach (string rawLine in playlist.Split('\n'))
        {
            string line = rawLine.Trim().TrimStart('\uFEFF');
            if (line.StartsWith(prefix, StringComparison.Ordinal) &&
                long.TryParse(line[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out long value))
                first = value;
            else if (line.Length > 0 && !line.StartsWith('#'))
                segments++;
        }
        if (!first.HasValue || segments == 0 || first.Value > long.MaxValue - segments + 1)
            return null;
        return new HlsOutputProgress(first.Value + segments - 1, writtenAtUtc);
    }

    internal static HlsOutputProgress? Read(string playlistPath)
    {
        try
        {
            // 先取时间再读原子替换的清单：遇到并发换片时宁可多观察一轮，也不把旧内容视为最新。
            DateTime writtenAtUtc = File.GetLastWriteTimeUtc(playlistPath);
            using var file = new FileStream(playlistPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file);
            return Parse(reader.ReadToEnd(), writtenAtUtc);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}

internal static class FfmpegInputOptions
{
    internal static IReadOnlyList<string> ForPlatform(string? platform) =>
        string.Equals(platform, "huya", StringComparison.OrdinalIgnoreCase)
            ? ["-http_persistent", "0"]
            : [];
}
