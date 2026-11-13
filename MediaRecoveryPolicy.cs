using System.Globalization;

/// <summary>区分传输抖动与需要重建解码时间线的故障，避免把同一次跳段的多路报告当作损坏。</summary>
internal static class MediaRecoveryPolicy
{
    internal const int ObservationSeconds = 30;
    internal const int StableOutputSeconds = 15;
    internal const int FreshOutputSeconds = 10;
    internal const int EvidenceSeconds = 30;
    internal const int SustainedFaultSeconds = 5;

    internal static MediaFaultSignal? SelectStrongEvidence(
        IReadOnlyList<MediaFaultSignal> recent, DateTime sessionCreatedAtUtc) =>
        SelectStrongEvidence(recent, sessionCreatedAtUtc,
            recent.Count == 0 ? sessionCreatedAtUtc : recent.Max(item => item.OccurredAtUtc));

    internal static MediaFaultSignal? SelectStrongEvidence(
        IReadOnlyList<MediaFaultSignal> recent, DateTime sessionCreatedAtUtc, DateTime now)
    {
        // 调用方一般已按会话保存信号，这里仍禁止把不同进程/会话的报告拼成持续损坏。
        var groups = FreshSignals(recent, sessionCreatedAtUtc, now)
            .GroupBy(item => (item.ChannelId, item.SessionId, item.ProcessId));
        return groups.Select(group => SelectSessionStrongEvidence(group.ToArray()))
            .Where(item => item != null).OrderBy(item => item!.OccurredAtUtc).LastOrDefault();
    }

    private static MediaFaultSignal? SelectSessionStrongEvidence(IReadOnlyList<MediaFaultSignal> recent)
    {
        var regression = recent.LastOrDefault(item =>
            item.Kind == MediaFaultKind.HuyaContinuity && item.RequiresImmediateRecovery);
        if (regression != null) return regression;

        var timestamps = recent.Where(item => item.Kind == MediaFaultKind.TimestampDiscontinuity).ToArray();
        var dts = recent.Where(item => item.Kind == MediaFaultKind.NonMonotonicDts).ToArray();
        bool boundary = recent.Any(item =>
            item.Kind is MediaFaultKind.SegmentSkip or MediaFaultKind.HuyaContinuity);
        bool sustainedDts = SpansSustainedPeriod(dts) &&
            (dts.Length >= 10 || (dts.Length >= 3 && (boundary || timestamps.Length > 0)));
        if (sustainedDts)
            return dts[^1] with { RequiresImmediateRecovery = true };

        // FFmpeg 的一次 timestamp discontinuity 表示已校正时间戳，而不是校正后必然损坏。
        // 同一跳段往往同时报告音/视频两次；按两秒 burst 合并后才判断是否持续发生。
        var timestampBursts = new List<MediaFaultSignal>();
        foreach (var signal in timestamps)
            if (timestampBursts.Count == 0 ||
                signal.OccurredAtUtc - timestampBursts[^1].OccurredAtUtc >= TimeSpan.FromSeconds(2))
                timestampBursts.Add(signal);
        if (timestampBursts.Count >= 3 && SpansSustainedPeriod(timestampBursts))
            return timestamps[^1] with { RequiresImmediateRecovery = true };

        // 单次大校正、一次校正伴随跳段、重复 gap 都只能进入观察，不能永久锁定重启。
        return null;
    }

    internal static MediaRecoveryEvaluation EvaluatePendingRecovery(
        MediaFaultSignal signal, DateTime lastSeenAtUtc, IReadOnlyList<MediaFaultSignal> recent,
        HlsOutputProgress? baseline, HlsOutputProgress? current, DateTime sessionCreatedAtUtc, DateTime now)
    {
        if (now < sessionCreatedAtUtc || signal.OccurredAtUtc > now || lastSeenAtUtc > now)
            return new(MediaRecoveryDisposition.Observe, null, "invalid-clock");

        var sameSession = FreshSignals(recent.Append(signal), sessionCreatedAtUtc, now)
            .Where(item => SameSession(item, signal)).ToArray();
        var strong = SelectSessionStrongEvidence(sameSession);
        if (strong != null)
            return new(MediaRecoveryDisposition.Recover, strong, "fresh-strong-evidence");

        // 冷却期间也重新检查当前输出；清单仍增长不能盖过新鲜 DTS/时间戳故障。
        DateTime lastFaultAtUtc = sameSession.Length == 0 ? signal.OccurredAtUtc : sameSession[^1].OccurredAtUtc;
        if (lastSeenAtUtc > lastFaultAtUtc) lastFaultAtUtc = lastSeenAtUtc;
        bool recentTimelineFault = sameSession.Any(item =>
            item.Kind is MediaFaultKind.NonMonotonicDts or MediaFaultKind.TimestampDiscontinuity);
        int quietSeconds = signal.RequiresImmediateRecovery || recentTimelineFault
            ? EvidenceSeconds : StableOutputSeconds;
        if (OutputAdvanced(baseline, current, now) &&
            current!.WrittenAtUtc >= lastFaultAtUtc.AddSeconds(quietSeconds))
            return new(MediaRecoveryDisposition.CancelObservation, null, "quiet-output-progress");

        // 旧告警本身不再是重启依据；缺失/过期/不推进的当前输出才是独立的新佐证。
        // 给启动和一次新观察完整的三十秒宽限，不因启动时暂时没有清单而立即恢复。
        DateTime observationStart = signal.OccurredAtUtc > sessionCreatedAtUtc
            ? signal.OccurredAtUtc : sessionCreatedAtUtc;
        bool graceElapsed = now - observationStart >= TimeSpan.FromSeconds(ObservationSeconds);
        bool staleOutput = current == null ||
            (current.WrittenAtUtc <= now && now - current.WrittenAtUtc >= TimeSpan.FromSeconds(ObservationSeconds));
        bool unchangedOutput = baseline != null && current != null && current.WrittenAtUtc <= now &&
            current.LastSequence <= baseline.LastSequence &&
            now - baseline.WrittenAtUtc >= TimeSpan.FromSeconds(ObservationSeconds);
        if (graceElapsed && (staleOutput || unchangedOutput))
            return new(MediaRecoveryDisposition.Recover,
                signal with { OccurredAtUtc = now, RequiresImmediateRecovery = true }, "output-stalled");

        return new(MediaRecoveryDisposition.Observe, null,
            recentTimelineFault ? "await-timeline-quiet" : "await-output-progress");
    }

    internal static bool CanCancelObservation(
        MediaFaultSignal signal, HlsOutputProgress? baseline, HlsOutputProgress? current, DateTime now)
    {
        // 兼容没有提供近期信号的旧调用方；正式恢复路径应使用上面的完整评估。
        return EvaluatePendingRecovery(signal, signal.OccurredAtUtc, [], baseline, current,
            signal.OccurredAtUtc, now).Disposition == MediaRecoveryDisposition.CancelObservation;
    }

    private static bool SameSession(MediaFaultSignal left, MediaFaultSignal right) =>
        left.ChannelId == right.ChannelId && left.SessionId == right.SessionId && left.ProcessId == right.ProcessId;

    private static bool SpansSustainedPeriod(IReadOnlyList<MediaFaultSignal> signals) =>
        signals.Count > 1 &&
        signals[^1].OccurredAtUtc - signals[0].OccurredAtUtc >= TimeSpan.FromSeconds(SustainedFaultSeconds);

    private static bool OutputAdvanced(HlsOutputProgress? baseline, HlsOutputProgress? current, DateTime now) =>
        current != null && current.WrittenAtUtc <= now &&
        now - current.WrittenAtUtc <= TimeSpan.FromSeconds(FreshOutputSeconds) &&
        (baseline == null || (current.LastSequence > baseline.LastSequence &&
            current.WrittenAtUtc > baseline.WrittenAtUtc));

    private static IEnumerable<MediaFaultSignal> FreshSignals(
        IEnumerable<MediaFaultSignal> recent, DateTime sessionCreatedAtUtc, DateTime now) =>
        recent.Where(item => item.OccurredAtUtc >= sessionCreatedAtUtc && item.OccurredAtUtc <= now &&
                now - item.OccurredAtUtc <= TimeSpan.FromSeconds(EvidenceSeconds))
            .OrderBy(item => item.OccurredAtUtc)
            // 同一日志重复入队/代理转报不增加证据数量，Weight 也不代表独立媒体故障。
            .DistinctBy(item => (item.Kind, item.Category, item.Detail, item.ChannelId, item.SessionId,
                item.ProcessId, Second: item.OccurredAtUtc.Ticks / TimeSpan.TicksPerSecond));
}

internal enum MediaRecoveryDisposition { Observe, CancelObservation, Recover }

internal sealed record MediaRecoveryEvaluation(
    MediaRecoveryDisposition Disposition, MediaFaultSignal? Evidence, string Reason);

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
