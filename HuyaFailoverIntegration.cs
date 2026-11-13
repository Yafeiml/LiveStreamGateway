using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LiveStreamGateway;

public partial class StreamManagerService
{
    private readonly ConcurrentDictionary<string, HuyaChannelLineState> _huyaLineStates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HuyaProbeJob> _huyaProbeJobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, HuyaProbeJob> _allHuyaProbeJobs = new();
    private readonly CancellationTokenSource _huyaProbeStop = new();
    private readonly SemaphoreSlim _huyaProbeSlots = new(2, 2);
    private readonly HttpClient _huyaProbeClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(4),
        MaxConnectionsPerServer = 2,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private sealed record HuyaProbeBatch(IReadOnlyList<HuyaLineProbeResult> Results, bool Cancelled, DateTime CompletedAtUtc);
    private sealed record HuyaProbeJob(Guid Id, DateTime StartedAtUtc, string Fingerprint, string SessionId, long Generation,
        string CurrentLineKey, HuyaExtractor Extractor, HuyaLineCandidate[] Candidates,
        CancellationTokenSource Cancellation, Task<HuyaProbeBatch> Task);

    internal static string SourceFingerprint(ChannelConfig channel)
    {
        // Length-prefix fields: neither concatenation collisions nor credentials in logs/state keys.
        string[] fields = [(channel.Platform ?? "").ToLowerInvariant(), channel.Url ?? "",
            channel.Quality ?? "", channel.Cookies ?? ""];
        string canonical = string.Concat(fields.Select(value => $"{value.Length}:{value}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static ChannelConfig SnapshotChannel(ChannelConfig source) => new()
    {
        Id = source.Id, Name = source.Name, Platform = source.Platform, Url = source.Url,
        Quality = source.Quality, Cookies = source.Cookies, CookieProfileKey = source.CookieProfileKey,
        Enable = source.Enable
    };

    private HuyaChannelLineState GetHuyaLineState(ChannelConfig channel)
    {
        string fingerprint = SourceFingerprint(channel);
        var state = _huyaLineStates.GetOrAdd(channel.Id, _ => new HuyaChannelLineState(fingerprint));
        bool changed;
        lock (state) changed = state.ResetSource(fingerprint);
        if (changed)
        {
            CancelHuyaProbe(channel.Id);
            Globals.Extractors.TryRemove(channel.Id, out _);
            Globals.M3u8Cache.TryRemove(channel.Id, out _);
        }
        return state;
    }

    private void RecordHuyaTransportFailure(MediaFaultSignal signal)
    {
        if (!_huyaLineStates.TryGetValue(signal.ChannelId, out var state)) return;
        bool added;
        int failures;
        lock (state)
        {
            added = state.RecordTransportFailure(signal.SessionId, signal.RouteGeneration,
                signal.SegmentIdentity, signal.OccurredAtUtc, DateTime.UtcNow);
            failures = state.RecentDistinctFailureCount(DateTime.UtcNow);
        }
        if (!added) return;
        Globals.RecordContinuitySignal(signal.ChannelId, signal.Category);
        LogHuyaEvent(signal.ChannelId, "transport-failure",
            $"category={signal.Category} session={ShortSessionId(signal.SessionId)} generation={signal.RouteGeneration} distinctFailures={failures}");
    }

    private static bool IsOutputStale(string channelId, StreamingSession session, DateTime now)
    {
        var output = ReadOutputProgress(channelId);
        int startupTimeout = Globals.Config.StartupTimeoutSeconds > 0 ? Globals.Config.StartupTimeoutSeconds : 30;
        return output == null
            ? now - session.CreatedAtUtc >= TimeSpan.FromSeconds(startupTimeout)
            : now - output.WrittenAtUtc >= TimeSpan.FromSeconds(STALE_THRESHOLD_SECONDS);
    }

    private static bool SessionHasExited(StreamingSession session)
    {
        try { return session.Process.HasExited; }
        catch (InvalidOperationException) { return true; } // Includes a concurrently disposed Process.
    }

    /// <summary>Only schedules network work; the manager loop and startup locks never wait on CDN probes.</summary>
    private bool TryScheduleHuyaProbe(ChannelConfig channel, StreamingSession session, bool outputStale)
    {
        if (!string.Equals(channel.Platform, "huya", StringComparison.OrdinalIgnoreCase)) return false;
        if (_huyaProbeJobs.TryGetValue(channel.Id, out var existing))
        {
            if (existing.SessionId == session.SessionId && existing.Fingerprint == SourceFingerprint(channel)) return true;
            CancelHuyaProbe(channel.Id);
        }
        if (!Globals.Extractors.TryGetValue(channel.Id, out var raw) || raw is not HuyaExtractor extractor ||
            extractor.SelectedLine == null || !_huyaLineStates.TryGetValue(channel.Id, out var state)) return false;

        // Ordering matches session creation/shutdown: lifecycle seal, then per-channel state.
        lock (_sessionLifecycleLock)
        {
            if (_stopping || _huyaProbeStop.IsCancellationRequested) return false;
            lock (state)
            {
                DateTime now = DateTime.UtcNow;
                if (!state.IsCurrent(session.SessionId, session.RouteGeneration) ||
                    state.SourceFingerprint != SourceFingerprint(channel)) return false;
                if (!state.CanProbe(session.SessionId, session.RouteGeneration, now, outputStale))
                    return outputStale && (state.AllCandidatesRetryAtUtc > now ||
                        state.LastProbeAtUtc > now.AddSeconds(-HuyaChannelLineState.ProbeCooldownSeconds));

                var current = extractor.SelectedLine;
                var candidates = new[] { current }.Concat(extractor.Candidates.Where(line =>
                    line.Key != current.Key && !state.IsLineIsolated(line.Key, now))).Take(3).ToArray();
                // All alternatives quarantined: retain the current route and wait, do not
                // fall through into a blind same-route restart on each manager tick.
                if (candidates.Length < 2)
                    return outputStale && extractor.Candidates.Count > 1;
                state.MarkProbeStarted(now);
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_huyaProbeStop.Token);
                cancellation.CancelAfter(TimeSpan.FromSeconds(25));
                var task = Task.Run(() => ProbeHuyaBatchAsync(extractor, candidates, cancellation.Token));
                var job = new HuyaProbeJob(Guid.NewGuid(), now, state.SourceFingerprint, session.SessionId,
                    session.RouteGeneration, current.Key, extractor, candidates, cancellation, task);
                _huyaProbeJobs[channel.Id] = job;
                _allHuyaProbeJobs[job.Id] = job;
                LogHuyaEvent(channel.Id, "probe-started",
                    $"session={ShortSessionId(session.SessionId)} generation={session.RouteGeneration} current={current} candidates={candidates.Length}");
                return true;
            }
        }
    }

    private async Task<HuyaProbeBatch> ProbeHuyaBatchAsync(HuyaExtractor extractor,
        HuyaLineCandidate[] candidates, CancellationToken token)
    {
        var results = new List<HuyaLineProbeResult>();
        bool acquired = false;
        try
        {
            await _huyaProbeSlots.WaitAsync(token).ConfigureAwait(false);
            acquired = true;
            var probe = new HuyaLineProbe(_huyaProbeClient);
            foreach (var candidate in candidates)
            {
                results.Add(await probe.ProbeAsync(extractor, candidate, token).ConfigureAwait(false));
                if (results[0].Success || results.Skip(1).Any(result => result.Success && result.DownloadMediaRatio <= 0.65))
                    break;
            }
            return new(results, false, DateTime.UtcNow);
        }
        catch (OperationCanceledException) { return new(results, true, DateTime.UtcNow); }
        catch { return new(results, true, DateTime.UtcNow); }
        finally
        {
            if (acquired) _huyaProbeSlots.Release();
            NotifyConfigChanged();
        }
    }

    private void CancelHuyaProbe(string channelId)
    {
        if (_huyaProbeJobs.TryRemove(channelId, out var job))
            try { job.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
    }

    private async Task ProcessHuyaProbesAsync(IReadOnlyList<ChannelConfig> channels, CancellationToken token)
    {
        foreach (var pair in _huyaProbeJobs.ToArray())
        {
            var job = pair.Value;
            if (!job.Task.IsCompleted) continue;
            if (!_huyaProbeJobs.TryRemove(new KeyValuePair<string, HuyaProbeJob>(pair.Key, job))) continue;
            try
            {
                var batch = await job.Task.ConfigureAwait(false);
                if (batch.Cancelled || batch.Results.Count == 0 ||
                    !IsFreshProbe(job.StartedAtUtc, batch.CompletedAtUtc, DateTime.UtcNow)) continue;
                ChannelConfig? channel = channels.FirstOrDefault(item => item.Enable && item.Id == pair.Key);
                lock (Globals.ConfigLock)
                {
                    var configured = Globals.Config.Channels.FirstOrDefault(item => item.Enable && item.Id == pair.Key);
                    channel = channel != null && configured != null ? SnapshotChannel(configured) : null;
                }
                if (channel == null || SourceFingerprint(channel) != job.Fingerprint ||
                    !_sessions.TryGetValue(pair.Key, out var session) || session.SessionId != job.SessionId ||
                    session.RouteGeneration != job.Generation ||
                    !_huyaLineStates.TryGetValue(pair.Key, out var state)) continue;

                bool stale = IsOutputStale(channel.Id, session, DateTime.UtcNow);
                HuyaLineProbeResult? selected;
                lock (state)
                {
                    if (!state.IsCurrent(job.SessionId, job.Generation)) continue;
                    foreach (var result in batch.Results)
                    {
                        var line = job.Candidates.First(item => item.Key == result.LineKey);
                        LogHuyaEvent(channel.Id, "probe-result",
                            $"session={ShortSessionId(job.SessionId)} line={line} result={result.Category} samples={result.SampleCount} bytes={result.DownloadedBytes} ratio={result.DownloadMediaRatio.ToString("F3", CultureInfo.InvariantCulture)}");
                        if (HuyaProbeDecisionPolicy.IsQualityFailure(result))
                            state.RecordProbeFailure(result.LineKey, DateTime.UtcNow);
                    }
                    // Not a generic speed contest: a healthy current route is kept even if another is faster.
                    bool stillFailing = stale || state.LastTransportFailureAt >=
                        DateTime.UtcNow.AddSeconds(-HuyaChannelLineState.FailureWindowSeconds);
                    var isolated = batch.Results.Where(item => state.IsLineIsolated(item.LineKey, DateTime.UtcNow))
                        .Select(item => item.LineKey).ToHashSet(StringComparer.Ordinal);
                    selected = HuyaProbeDecisionPolicy.SelectAlternative(job.CurrentLineKey, batch.Results, stillFailing, isolated);
                    if (selected == null && batch.Results.Count == job.Candidates.Length &&
                        !batch.Results.Any(item => item.Success))
                        state.MarkAllCandidatesFailed(DateTime.UtcNow);
                }
                if (selected != null)
                    await TryRecoverChannelAsync(channel, session, "huya-line-switch", token,
                        selectedLineKey: selected.LineKey, expectedJob: job);
                else if (stale && batch.Results.Any(item => item.Success))
                    await TryRecoverChannelAsync(channel, session, "manifest-stale", token);
                else
                    LogHuyaEvent(channel.Id, "probe-retained",
                        $"session={ShortSessionId(job.SessionId)} action=keep-current cancelled={batch.Cancelled}");
            }
            finally
            {
                _allHuyaProbeJobs.TryRemove(job.Id, out _);
                job.Cancellation.Dispose();
            }
        }
        // Config/deletion/idle cancellation can orphan a batch; it is still joined at shutdown.
        foreach (var pair in _allHuyaProbeJobs.ToArray())
        {
            if (!pair.Value.Task.IsCompleted || _huyaProbeJobs.Values.Contains(pair.Value)) continue;
            if (_allHuyaProbeJobs.TryRemove(pair.Key, out var job)) job.Cancellation.Dispose();
        }
    }

    private async Task<bool> TryRecoverChannelAsync(ChannelConfig channel, StreamingSession expectedSession,
        string reason, CancellationToken token, MediaFaultSignal? signal = null,
        string? selectedLineKey = null, HuyaProbeJob? expectedJob = null)
    {
        var startLock = _startupLocks.GetOrAdd(channel.Id, _ => new SemaphoreSlim(1, 1));
        await startLock.WaitAsync(token);
        try
        {
            if (_stopping || token.IsCancellationRequested ||
                !_sessions.TryGetValue(channel.Id, out var session) || !ReferenceEquals(session, expectedSession)) return false;
            ChannelConfig? live;
            lock (Globals.ConfigLock)
            {
                var configured = Globals.Config.Channels.FirstOrDefault(item => item.Enable && item.Id == channel.Id);
                live = configured == null ? null : SnapshotChannel(configured);
            }
            if (live == null || SourceFingerprint(live) != SourceFingerprint(channel)) return false;

            if (signal != null)
            {
                if (session.SessionId != signal.SessionId) return false;
                _recentMediaFaults.TryGetValue(channel.Id, out var recent);
                _pendingMediaRecoveries.TryGetValue(channel.Id, out var pending);
                var evaluation = MediaRecoveryPolicy.EvaluatePendingRecovery(pending?.Signal ?? signal,
                    pending?.LastSeenAtUtc ?? signal.OccurredAtUtc, recent ?? [],
                    pending?.OutputBaseline, ReadOutputProgress(channel.Id), session.CreatedAtUtc, DateTime.UtcNow);
                if (evaluation.Disposition != MediaRecoveryDisposition.Recover) return false;
                if (evaluation.Reason == "output-stalled" &&
                    TryScheduleHuyaProbe(channel, session, outputStale: true)) return false;
            }
            else if (reason != "ffmpeg-exited" && !IsOutputStale(channel.Id, session, DateTime.UtcNow) &&
                selectedLineKey == null) return false;

            var budget = _automaticRecoveryBudgets.GetOrAdd(channel.Id, _ => new AutomaticRecoveryBudget());
            DateTime now = DateTime.UtcNow;
            HuyaChannelLineState? state = null;
            if (selectedLineKey != null)
            {
                if (expectedJob == null || expectedJob.Fingerprint != SourceFingerprint(live) ||
                    !_huyaLineStates.TryGetValue(channel.Id, out state)) return false;
            }
            DateTime retryAt;
            bool acquired;
            if (state != null)
            {
                lock (state)
                {
                    // Configuration/session, evidence age, dwell, budget and selection are
                    // one commit. A failed precondition must not consume a recovery action.
                    if (!state.IsCurrent(expectedJob!.SessionId, expectedJob.Generation) ||
                        session.RouteGeneration != expectedJob.Generation ||
                        state.SourceFingerprint != expectedJob.Fingerprint ||
                        now - expectedJob.StartedAtUtc > TimeSpan.FromSeconds(30) ||
                        state.IsLineIsolated(selectedLineKey!, now) || state.SelectedLineKey == selectedLineKey ||
                        (state.SelectedAtUtc.HasValue && now < state.SelectedAtUtc.Value.AddSeconds(HuyaChannelLineState.MinimumDwellSeconds)) ||
                        (!IsOutputStale(channel.Id, session, now) &&
                         !(state.LastTransportFailureAt is DateTime last &&
                           last >= now.AddSeconds(-HuyaChannelLineState.FailureWindowSeconds)))) return false;
                    acquired = budget.TryAcquire(session.SessionId, now, out retryAt);
                    if (acquired) state.TrySelectLine(expectedJob.SessionId, expectedJob.Generation, selectedLineKey!, now);
                }
            }
            else acquired = budget.TryAcquire(session.SessionId, now, out retryAt);
            if (!acquired)
            {
                // Remember the cooldown but never retain an unqualified stale alarm as a restart order.
                if (signal != null)
                {
                    _pendingMediaRecoveries.TryGetValue(channel.Id, out var previous);
                    _pendingMediaRecoveries[channel.Id] = new(previous?.Signal ?? signal, retryAt,
                        previous?.OutputBaseline ?? ReadOutputProgress(channel.Id),
                        previous?.LastSeenAtUtc ?? signal.OccurredAtUtc);
                    Globals.MarkContinuityRecoveryPending(channel.Id, signal.SessionId, retryAt);
                }
                Globals.UpdateStatus(channel.Id, "已检测到异常，自动恢复冷却中", ConsoleColor.Yellow);
                if (!_automaticRetryNotBefore.TryGetValue(channel.Id, out var previousRetry) || previousRetry != retryAt)
                    LogHuyaEvent(channel.Id, "recovery-deferred", $"reason={reason} session={ShortSessionId(session.SessionId)} retryAt={retryAt:O}");
                _automaticRetryNotBefore[channel.Id] = retryAt;
                return false;
            }
            _automaticRetryNotBefore[channel.Id] = budget.GetRetryAt("next-session", now);
            _pendingMediaRecoveries.TryRemove(channel.Id, out _);
            _recentMediaFaults.TryRemove(channel.Id, out _);
            Globals.ClearContinuityRecoveryPending(channel.Id, session.SessionId);
            CancelHuyaProbe(channel.Id);
            if (signal != null)
                await MediaDiagnostics.CaptureAsync(channel.Id, session, signal, token);
            Globals.MarkContinuityRecoveryStarted(channel.Id);
            Globals.RecordRestart(channel.Id, reason);
            Globals.RecordError(channel.Id);
            LogHuyaEvent(channel.Id, "automatic-recovery",
                $"reason={reason} session={ShortSessionId(session.SessionId)} action={(selectedLineKey == null ? "rebuild" : "switch-and-rebuild")}");
            Globals.UpdateState(channel.Id, ChannelState.Restarting);
            Globals.UpdateStatus(channel.Id, selectedLineKey == null ? "检测到推流异常，正在自动恢复..." : "线路质量异常，正在切换已验证的备用线路...", ConsoleColor.Yellow);
            await StopSessionAsync(channel.Id);
            await ResetSourceStateAsync(live, token);
            var status = Globals.ChannelStatuses.FirstOrDefault(item => item.Id == channel.Id);
            if (status != null && !token.IsCancellationRequested)
                await StartSingleChannelGuardedAsync(live, status, token, SourceFingerprint(channel));
            return true;
        }
        finally { startLock.Release(); }
    }

    internal static bool IsFreshProbe(DateTime startedAtUtc, DateTime completedAtUtc, DateTime now) =>
        startedAtUtc <= completedAtUtc && completedAtUtc <= now &&
        now - startedAtUtc <= TimeSpan.FromSeconds(30);

    private static void LogHuyaEvent(string channelId, string eventName, string details)
    {
        LogLifecycle(channelId, eventName, details);
        // Small, signature-free event trail survives FFmpeg log rotation and Windows tray mode.
        // Existing per-channel HLS diagnostics retention rules still apply to this directory.
        try
        {
            string directory = Path.GetFullPath(Path.Combine(Globals.HLS_FULL_PATH, channelId));
            string root = Path.GetFullPath(Globals.HLS_FULL_PATH) + Path.DirectorySeparatorChar;
            if (!directory.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                !Directory.Exists(directory)) return;
            string path = Path.Combine(directory, "lifecycle.log");
            if (File.Exists(path) && new FileInfo(path).Length > 128 * 1024)
                File.Move(path, Path.Combine(directory, "lifecycle.previous.log"), overwrite: true);
            string safeDetails = details.Replace('\r', ' ').Replace('\n', ' ');
            if (safeDetails.Length > 1024) safeDetails = safeDetails[..1024];
            File.AppendAllText(path, $"{DateTime.UtcNow:O} event={SafeLogValue(eventName)} {safeDetails}{Environment.NewLine}");
        }
        catch { /* Diagnostic I/O must not block or abort recovery. */ }
    }
}
