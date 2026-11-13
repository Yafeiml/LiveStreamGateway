/// <summary>
/// Per-channel Huya route state. The channel manager owns this object; it is deliberately
/// independent of an extractor or FFmpeg process so a restart does not forget a bad CDN.
/// All times are supplied by the caller in UTC. No network or configuration writes occur here.
/// </summary>
internal sealed class HuyaChannelLineState
{
    internal const int FailureWindowSeconds = 30;
    internal const int AnonymousBurstGapSeconds = 10;
    internal const int ProbeCooldownSeconds = 30;
    internal const int MinimumDwellSeconds = 90;
    internal const int BadLineIsolationSeconds = 180;
    internal const int InitialAllBadBackoffSeconds = 30;
    internal const int MaximumAllBadBackoffSeconds = 300;

    private const int MaximumRememberedSegments = 256;
    private const int MaximumRememberedLines = 64;
    private readonly List<TransportFailure> _failures = [];
    private readonly HashSet<string> _seenSegments = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenSegmentOrder = new();
    private readonly Dictionary<string, DateTime> _isolatedLines = new(StringComparer.Ordinal);
    private DateTime? _sessionStartedAtUtc;
    private DateTime? _lastAnonymousFailureAtUtc;
    private string? _anonymousBurstKey;
    private DateTime? _lastIdentifiedFailureAtUtc;
    private string? _lastIdentifiedFailureKey;
    private long _anonymousBurstSequence;
    private int _allBadRounds;

    internal HuyaChannelLineState(string sourceFingerprint)
    {
        SourceFingerprint = sourceFingerprint;
    }

    internal string SourceFingerprint { get; private set; }
    internal string? SelectedLineKey { get; private set; }
    internal string SessionId { get; private set; } = "";
    internal long Generation { get; private set; }
    internal DateTime? SelectedAtUtc { get; private set; }
    internal DateTime? LastTransportFailureAt { get; private set; }
    internal DateTime? LastProbeAtUtc { get; private set; }
    internal DateTime? AllCandidatesRetryAtUtc { get; private set; }

    /// <summary>Forget route preferences only when the configured source really changes.</summary>
    internal bool ResetSource(string sourceFingerprint)
    {
        if (string.Equals(SourceFingerprint, sourceFingerprint, StringComparison.Ordinal)) return false;
        SourceFingerprint = sourceFingerprint;
        SelectedLineKey = null;
        SelectedAtUtc = null;
        SessionId = "";
        _sessionStartedAtUtc = null;
        Generation++;
        LastProbeAtUtc = null;
        AllCandidatesRetryAtUtc = null;
        _allBadRounds = 0;
        _isolatedLines.Clear();
        ClearSessionEvidence();
        return true;
    }

    /// <summary>
    /// Bind the actual selected route of a new process. Same-source restarts preserve the
    /// selection preference, quarantine and cooldown; stale process signals cannot carry over.
    /// </summary>
    internal long BindSession(string sessionId, string selectedLineKey, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedLineKey);
        if (string.Equals(SessionId, sessionId, StringComparison.Ordinal) &&
            string.Equals(SelectedLineKey, selectedLineKey, StringComparison.Ordinal)) return Generation;

        bool changedLine = !string.Equals(SelectedLineKey, selectedLineKey, StringComparison.Ordinal);
        SessionId = sessionId;
        SelectedLineKey = selectedLineKey;
        _sessionStartedAtUtc = now;
        if (changedLine) SelectedAtUtc = now;
        Generation++;
        ClearSessionEvidence();
        return Generation;
    }

    internal bool IsCurrent(string sessionId, long generation) =>
        SessionId.Length > 0 && generation == Generation &&
        string.Equals(SessionId, sessionId, StringComparison.Ordinal);

    /// <summary>
    /// Record one actual failed input fragment, not one stderr message. The caller should
    /// pass a signature-free fragment identity shared by premature/EOF/failed reports.
    /// Unknown fragments are grouped into a burst until ten seconds of silence, so a
    /// continuous burst cannot manufacture several independent fragment failures. A
    /// nearby identified report upgrades an anonymous burst instead of adding a failure,
    /// regardless of which stderr message arrives first.
    /// </summary>
    internal bool RecordTransportFailure(string sessionId, long generation, string? segmentIdentity,
        DateTime occurredAtUtc, DateTime now)
    {
        if (!IsCurrent(sessionId, generation) || occurredAtUtc > now ||
            occurredAtUtc < now.AddSeconds(-FailureWindowSeconds) ||
            (_sessionStartedAtUtc.HasValue && occurredAtUtc < _sessionStartedAtUtc.Value)) return false;

        PruneFailures(now);
        string identity;
        DateTime evidenceAtUtc = occurredAtUtc;
        bool upgradedAnonymous = false;
        if (!string.IsNullOrWhiteSpace(segmentIdentity))
        {
            identity = "segment:" + segmentIdentity;
            if (_anonymousBurstKey?.StartsWith("anonymous:", StringComparison.Ordinal) == true &&
                _lastAnonymousFailureAtUtc.HasValue &&
                Math.Abs((occurredAtUtc - _lastAnonymousFailureAtUtc.Value).TotalSeconds) <= AnonymousBurstGapSeconds)
            {
                int anonymousIndex = _failures.FindIndex(item => item.Identity == _anonymousBurstKey);
                if (anonymousIndex >= 0)
                {
                    // Retain the earliest evidence time: a later, more precise stderr
                    // message must not prolong the life of the original failure.
                    evidenceAtUtc = _failures[anonymousIndex].OccurredAtUtc < evidenceAtUtc
                        ? _failures[anonymousIndex].OccurredAtUtc : evidenceAtUtc;
                    _failures.RemoveAt(anonymousIndex);
                    upgradedAnonymous = true;
                }
                _anonymousBurstKey = identity;
            }
            if (!_lastIdentifiedFailureAtUtc.HasValue || occurredAtUtc >= _lastIdentifiedFailureAtUtc.Value)
            {
                _lastIdentifiedFailureAtUtc = occurredAtUtc;
                _lastIdentifiedFailureKey = identity;
            }
        }
        else
        {
            if (!_lastAnonymousFailureAtUtc.HasValue ||
                occurredAtUtc > _lastAnonymousFailureAtUtc.Value.AddSeconds(AnonymousBurstGapSeconds))
            {
                // An identity-bearing message just before this anonymous warning may
                // describe the very same fragment. Conservatively use that identity.
                bool nearIdentified = _lastIdentifiedFailureAtUtc.HasValue &&
                    Math.Abs((occurredAtUtc - _lastIdentifiedFailureAtUtc.Value).TotalSeconds) <= AnonymousBurstGapSeconds;
                _anonymousBurstKey = nearIdentified ? _lastIdentifiedFailureKey : "anonymous:" + ++_anonymousBurstSequence;
            }
            if (!_lastAnonymousFailureAtUtc.HasValue || occurredAtUtc > _lastAnonymousFailureAtUtc.Value)
                _lastAnonymousFailureAtUtc = occurredAtUtc;
            identity = _anonymousBurstKey!;
        }

        if (!LastTransportFailureAt.HasValue || occurredAtUtc > LastTransportFailureAt.Value)
            LastTransportFailureAt = occurredAtUtc;
        if (!_seenSegments.Add(identity)) return false;
        _seenSegmentOrder.Enqueue(identity);
        while (_seenSegmentOrder.Count > MaximumRememberedSegments)
            _seenSegments.Remove(_seenSegmentOrder.Dequeue());
        _failures.Add(new TransportFailure(identity, evidenceAtUtc));
        if (_failures.Count > MaximumRememberedSegments) _failures.RemoveAt(0);
        return !upgradedAnonymous;
    }

    internal int RecentDistinctFailureCount(DateTime now)
    {
        PruneFailures(now);
        return _failures.Count;
    }

    internal bool CanProbe(string sessionId, long generation, DateTime now, bool outputStale)
    {
        if (!IsCurrent(sessionId, generation)) return false;
        if (SelectedAtUtc.HasValue && now < SelectedAtUtc.Value.AddSeconds(MinimumDwellSeconds)) return false;
        if (LastProbeAtUtc.HasValue && now < LastProbeAtUtc.Value.AddSeconds(ProbeCooldownSeconds)) return false;
        if (AllCandidatesRetryAtUtc.HasValue && now < AllCandidatesRetryAtUtc.Value) return false;
        return outputStale || RecentDistinctFailureCount(now) >= 2;
    }

    /// <summary>Call once when a probe batch is actually started, not for each candidate.</summary>
    internal void MarkProbeStarted(DateTime now)
    {
        if (!LastProbeAtUtc.HasValue || now > LastProbeAtUtc.Value) LastProbeAtUtc = now;
    }

    internal bool IsLineIsolated(string lineKey, DateTime now)
    {
        PruneIsolations(now);
        return _isolatedLines.TryGetValue(lineKey, out var until) && now < until;
    }

    internal void RecordProbeFailure(string lineKey, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lineKey);
        PruneIsolations(now);
        var until = now.AddSeconds(BadLineIsolationSeconds);
        if (!_isolatedLines.TryGetValue(lineKey, out var existing) || existing < until)
            _isolatedLines[lineKey] = until;
        if (_isolatedLines.Count > MaximumRememberedLines)
            _isolatedLines.Remove(_isolatedLines.MinBy(item => item.Value).Key);
    }

    /// <summary>Exponential backoff for an unsuccessful whole candidate batch.</summary>
    internal void MarkAllCandidatesFailed(DateTime now)
    {
        _allBadRounds = Math.Min(_allBadRounds + 1, 5);
        int seconds = Math.Min(InitialAllBadBackoffSeconds * (1 << (_allBadRounds - 1)),
            MaximumAllBadBackoffSeconds);
        var retryAt = now.AddSeconds(seconds);
        if (!AllCandidatesRetryAtUtc.HasValue || retryAt > AllCandidatesRetryAtUtc.Value)
            AllCandidatesRetryAtUtc = retryAt;
    }

    /// <summary>
    /// Commit only after successful probing, current-session revalidation and acquisition
    /// of the shared recovery budget. Returning false never changes the selected route.
    /// </summary>
    internal bool TrySelectLine(string sessionId, long generation, string lineKey, DateTime now)
    {
        if (!IsCurrent(sessionId, generation) || string.IsNullOrWhiteSpace(lineKey) ||
            string.Equals(SelectedLineKey, lineKey, StringComparison.Ordinal) || IsLineIsolated(lineKey, now) ||
            (SelectedAtUtc.HasValue && now < SelectedAtUtc.Value.AddSeconds(MinimumDwellSeconds))) return false;
        SelectedLineKey = lineKey;
        SelectedAtUtc = now;
        Generation++;
        _allBadRounds = 0;
        AllCandidatesRetryAtUtc = null;
        ClearSessionEvidence();
        return true;
    }

    private void ClearSessionEvidence()
    {
        _failures.Clear();
        _seenSegments.Clear();
        _seenSegmentOrder.Clear();
        LastTransportFailureAt = null;
        _lastAnonymousFailureAtUtc = null;
        _anonymousBurstKey = null;
        _lastIdentifiedFailureAtUtc = null;
        _lastIdentifiedFailureKey = null;
    }

    private void PruneFailures(DateTime now) =>
        _failures.RemoveAll(item => item.OccurredAtUtc < now.AddSeconds(-FailureWindowSeconds));

    private void PruneIsolations(DateTime now)
    {
        foreach (var key in _isolatedLines.Where(item => item.Value <= now).Select(item => item.Key).ToArray())
            _isolatedLines.Remove(key);
    }

    private sealed record TransportFailure(string Identity, DateTime OccurredAtUtc);
}

/// <summary>
/// Shared per-channel automatic recovery gate for every cause, including a route switch.
/// Probe/observation alone does not consume it. The manager acquires immediately before
/// committing a restart, under its existing session/operation gate.
/// </summary>
internal sealed class AutomaticRecoveryBudget
{
    internal const int CooldownSeconds = 90;
    internal const int WindowMinutes = 10;
    internal const int MaximumActions = 3;
    private readonly Queue<DateTime> _actions = new();
    private readonly HashSet<string> _consumedSessions = new(StringComparer.Ordinal);
    internal DateTime? LastActionAtUtc { get; private set; }

    /// <returns>now if available; MaxValue if this process session already consumed an action.</returns>
    internal DateTime GetRetryAt(string sessionId, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (_consumedSessions.Contains(sessionId)) return DateTime.MaxValue;
        Prune(now);
        var retryAt = now;
        if (LastActionAtUtc.HasValue && LastActionAtUtc.Value.AddSeconds(CooldownSeconds) > retryAt)
            retryAt = LastActionAtUtc.Value.AddSeconds(CooldownSeconds);
        if (_actions.Count >= MaximumActions && _actions.Peek().AddMinutes(WindowMinutes) > retryAt)
            retryAt = _actions.Peek().AddMinutes(WindowMinutes);
        return retryAt;
    }

    internal bool TryAcquire(string sessionId, DateTime now, out DateTime retryAt)
    {
        retryAt = GetRetryAt(sessionId, now);
        if (retryAt > now) return false;
        _actions.Enqueue(now);
        LastActionAtUtc = now;
        _consumedSessions.Add(sessionId);
        return true;
    }

    private void Prune(DateTime now)
    {
        while (_actions.Count > 0 && _actions.Peek() <= now.AddMinutes(-WindowMinutes))
            _actions.Dequeue();
    }
}
