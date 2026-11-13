using System.Text.RegularExpressions;

/// <summary>只分类真正的取片失败，不将一个报错的多条伴随日志当成多片故障。</summary>
internal sealed class MediaTransportFaultParser
{
    private static readonly Regex FailedSegment = new(
        @"Failed to open segment\s+(?<sequence>\d+)\s+of playlist\s+(?<playlist>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal MediaTransportFailure? Parse(string line)
    {
        Match segment = FailedSegment.Match(line);
        if (segment.Success)
            return new("segment-open-failed",
                $"playlist={segment.Groups["playlist"].Value};sequence={segment.Groups["sequence"].Value}");

        string? category = line.Contains("Stream ends prematurely", StringComparison.OrdinalIgnoreCase)
            ? "premature-eof"
            : line.Contains("Error when loading first segment", StringComparison.OrdinalIgnoreCase)
                ? "segment-open-failed"
                : line.Contains("Error during demuxing", StringComparison.OrdinalIgnoreCase) &&
                    (line.Contains("Input/output error", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("Connection", StringComparison.OrdinalIgnoreCase))
                    ? "transport-read-failed" : null;
        if (category == null) return null;

        // 签名/query、主机和直播间流名不进入信号或生命周期日志。
        // 没有明确分片身份的 EOF 交给状态机按连续突发去重，不猜测为另一片。
        // 不将 URL path hash 和 FFmpeg sequence 当作两个身份命名空间，避免同一片
        // 先 EOF、随后 failed-open 时被重复计数。匿名报告在状态层与邻近明确片合并。
        return new(category, null);
    }
}

internal sealed record MediaTransportFailure(string Category, string? SegmentIdentity);
