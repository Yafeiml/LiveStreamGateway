using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using LiveStreamGateway;

namespace LiveStreamGateway.Tests;

[Collection("StreamManager shutdown globals")]
public sealed class HuyaRecoveryIntegrationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    private static readonly DateTime Clock = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(30, 30, true)]
    [InlineData(31, 30, false)]
    [InlineData(20, 21, false)]
    [InlineData(20, -1, false)]
    [InlineData(-1, 0, false)]
    public void ProbeFreshness_RejectsExpiredFutureOrReversedResults(int nowSeconds, int completedSeconds, bool expected)
    {
        Assert.Equal(expected, StreamManagerService.IsFreshProbe(
            Clock, Clock.AddSeconds(completedSeconds), Clock.AddSeconds(nowSeconds)));
    }

    [Fact]
    public void SourceFingerprint_IsStableForPresentationChangesAndPlatformCase()
    {
        var source = Source();
        var presentation = Source();
        presentation.Id = "another-id";
        presentation.Name = "renamed";
        presentation.Enable = false;
        presentation.Platform = "HUYA";
        Assert.Equal(StreamManagerService.SourceFingerprint(source), StreamManagerService.SourceFingerprint(presentation));
    }

    [Fact]
    public void SourceFingerprint_UsesFieldBoundariesAndIncludesQualityAndCookies()
    {
        var first = Source();
        first.Url = "abc";
        first.Quality = "d";
        var second = Source();
        second.Url = "ab";
        second.Quality = "cd";
        Assert.NotEqual(StreamManagerService.SourceFingerprint(first), StreamManagerService.SourceFingerprint(second));

        second = Source();
        second.Cookies = "changed=credential";
        Assert.NotEqual(StreamManagerService.SourceFingerprint(Source()), StreamManagerService.SourceFingerprint(second));
        var changedQuality = Source();
        changedQuality.Quality = "HD";
        Assert.NotEqual(StreamManagerService.SourceFingerprint(Source()), StreamManagerService.SourceFingerprint(changedQuality));
        string fingerprint = StreamManagerService.SourceFingerprint(second);
        Assert.Equal(64, fingerprint.Length);
        Assert.DoesNotContain("credential", fingerprint);
    }

    [Fact]
    public void SourceFingerprint_ExplicitNullFieldsAreEquivalentToEmptyFields()
    {
        var nullFields = new ChannelConfig { Platform = null!, Url = null!, Quality = null!, Cookies = null! };
        var emptyFields = new ChannelConfig { Platform = "", Url = "", Quality = "", Cookies = "" };
        Assert.Equal(StreamManagerService.SourceFingerprint(emptyFields), StreamManagerService.SourceFingerprint(nullFields));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredOrCancelledProbe_DoesNotQuarantineRoutesOrConsumeBudget(bool cancelled)
    {
        CancellationToken testCancellation = TestContext.Current.CancellationToken;
        await using var scope = new RecoveryTestScope();
        DateTime now = DateTime.UtcNow;
        var session = scope.InstallSentinelSession("probe-session", now.AddMinutes(-5));
        var state = new HuyaChannelLineState(StreamManagerService.SourceFingerprint(scope.Channel));
        var current = new HuyaLineCandidate("TX", new Uri("https://tx.huya.com/live"), "local-test", "m3u8", "", "");
        var alternate = new HuyaLineCandidate("AL", new Uri("https://al.huya.com/live"), "local-test", "m3u8", "", "");
        long generation = state.BindSession(session.SessionId, current.Key, now.AddMinutes(-5));
        SetSessionGeneration(session, generation);
        PrivateDictionary<HuyaChannelLineState>(scope.Service, "_huyaLineStates")[scope.Channel.Id] = state;

        // A failed current-route result would quarantine TX if an invalid batch leaked
        // into processing. No live HTTP, FFmpeg process, or decoder is involved.
        var results = new[] { new HuyaLineProbeResult(current.Key, false, "LengthMismatch", 1, 0, 0, TimeSpan.Zero) };
        DateTime completed = cancelled ? now : now.AddSeconds(-90);
        DateTime started = completed.AddSeconds(-5);
        using var extractor = new DisposableHuyaExtractor();
        using var cancellation = new CancellationTokenSource();
        object batch = NestedConstructor("HuyaProbeBatch").Invoke([results, cancelled, completed]);
        Type batchType = batch.GetType();
        object task = typeof(Task).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == nameof(Task.FromResult) && method.IsGenericMethodDefinition)
            .MakeGenericMethod(batchType).Invoke(null, [batch])!;
        object job = NestedConstructor("HuyaProbeJob").Invoke([
            Guid.NewGuid(), started, StreamManagerService.SourceFingerprint(scope.Channel), session.SessionId,
            generation, current.Key, extractor, new[] { current, alternate }, cancellation, task
        ]);
        object jobs = PrivateField("_huyaProbeJobs").GetValue(scope.Service)!;
        jobs.GetType().GetProperty("Item")!.SetValue(jobs, job, [scope.Channel.Id]);

        await ((Task)PrivateMethod("ProcessHuyaProbesAsync").Invoke(scope.Service,
            [new[] { scope.Channel }, testCancellation])!).WaitAsync(TestTimeout, testCancellation);

        Assert.False(state.IsLineIsolated(current.Key, DateTime.UtcNow));
        Assert.Null(state.AllCandidatesRetryAtUtc);
        Assert.Null(scope.Budget.LastActionAtUtc);
        Assert.Equal(0, (int)jobs.GetType().GetProperty("Count")!.GetValue(jobs)!);
        Assert.Same(session, GetSessions(scope.Service)[scope.Channel.Id]);
        Assert.Equal(0, Globals.Metrics.GetValueOrDefault(scope.Channel.Id)?.StartCount ?? 0);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("source-changed")]
    [InlineData("old-session")]
    public async Task InvalidRecoveryContext_IsRejectedBeforeBudgetOrProcessAccess(string scenario)
    {
        CancellationToken testCancellation = TestContext.Current.CancellationToken;
        await using var scope = new RecoveryTestScope();
        var current = scope.InstallSentinelSession("current-session", DateTime.UtcNow);
        var expected = scenario == "old-session" ? SentinelSession("old-session", DateTime.UtcNow) : current;
        if (scenario != "old-session") scope.ReplaceConfiguredChannel(disabled: scenario == "disabled");

        bool recovered = await ((Task<bool>)PrivateMethod("TryRecoverChannelAsync").Invoke(scope.Service,
            [scope.Channel, expected, "ffmpeg-exited", testCancellation, null, null, null])!)
            .WaitAsync(TestTimeout, testCancellation);

        Assert.False(recovered);
        Assert.Null(scope.Budget.LastActionAtUtc);
        DateTime now = DateTime.UtcNow;
        Assert.Equal(now, scope.Budget.GetRetryAt(current.SessionId, now));
        Assert.Same(current, GetSessions(scope.Service)[scope.Channel.Id]);
        Assert.Equal(0, Globals.Metrics.GetValueOrDefault(scope.Channel.Id)?.StartCount ?? 0);
        Assert.False(scope.Extractor.Entered.IsCompleted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisableOrSourceChangeDuringRecoveryResolution_PreventsLateFfmpegCreation(bool disabled)
    {
        CancellationToken testCancellation = TestContext.Current.CancellationToken;
        await using var scope = new RecoveryTestScope();
        Task start = scope.StartGuardedAsync(testCancellation);
        await scope.Extractor.Entered.WaitAsync(TestTimeout, testCancellation);
        Assert.False(start.IsCompleted);

        scope.ReplaceConfiguredChannel(disabled);
        scope.Extractor.Complete();
        await start.WaitAsync(TestTimeout, testCancellation);

        Assert.Empty(GetSessions(scope.Service));
        Assert.Null(scope.Budget.LastActionAtUtc);
        Assert.DoesNotContain("已启动推流", scope.Status.Message);
        Assert.DoesNotContain("FFmpeg 进程启动失败", scope.Status.Message);
        Assert.True(Globals.Metrics.TryGetValue(scope.Channel.Id, out var metrics));
        Assert.Equal(0, metrics!.StartCount);
        Assert.Equal(0, metrics.ErrorCount);
        Assert.Empty(Directory.EnumerateFiles(scope.ChannelDirectory, "ffmpeg*.log"));
    }

    [Fact]
    public async Task VerifiedAlternative_CommitsOneSwitchAndStopsOldSessionWithoutLaunchingFfmpeg()
    {
        CancellationToken testCancellation = TestContext.Current.CancellationToken;
        await using var scope = new RecoveryTestScope();
        var fixture = InstallFreshSwitchFixture(scope);
        using var extractor = new DisposableHuyaExtractor();
        using var cancellation = new CancellationTokenSource();
        InstallCompletedProbe(scope, fixture.Session, fixture.Generation, fixture.Candidates,
            SuccessfulAlternativeResults(fixture.Candidates), extractor, cancellation);

        await ProcessProbesAsync(scope, testCancellation).WaitAsync(TestTimeout, testCancellation);

        // This exercises route selection, budget commit and controlled stop. Re-parsing
        // intentionally fails for "integration-test" after ResetSource removes the fake
        // extractor: no real HTTP request or positive FFmpeg launch is being claimed.
        Assert.Equal(fixture.Candidates[1].Key, fixture.State.SelectedLineKey);
        Assert.Equal(fixture.Generation + 1, fixture.State.Generation);
        Assert.Empty(GetSessions(scope.Service));
        Assert.NotNull(scope.Budget.LastActionAtUtc);
        Assert.Equal(1, BudgetActionCount(scope.Budget));
        Assert.Equal(DateTime.MaxValue, scope.Budget.GetRetryAt(fixture.Session.SessionId, DateTime.UtcNow));
        Assert.True(Globals.Metrics.TryGetValue(scope.Channel.Id, out var metrics));
        Assert.Equal(1, metrics!.RestartCount);
        Assert.Equal("huya-line-switch", metrics.LastRestartReason);
        Assert.Equal(0, metrics.StartCount);
        Assert.False(scope.Extractor.Entered.IsCompleted);
        Assert.Empty(Directory.EnumerateFiles(scope.ChannelDirectory, "ffmpeg*.log"));

        DateTime? committedAt = scope.Budget.LastActionAtUtc;
        bool repeated = await ((Task<bool>)PrivateMethod("TryRecoverChannelAsync").Invoke(scope.Service,
            [scope.Channel, fixture.Session, "ffmpeg-exited", testCancellation, null, null, null])!)
            .WaitAsync(TestTimeout, testCancellation);
        Assert.False(repeated);
        Assert.Equal(committedAt, scope.Budget.LastActionAtUtc);
        Assert.Equal(1, BudgetActionCount(scope.Budget));
        Assert.Equal(1, metrics.RestartCount);
    }

    [Fact]
    public async Task ProbeFromDifferentSessionRouteGeneration_DoesNotCommitOrConsumeBudget()
    {
        CancellationToken testCancellation = TestContext.Current.CancellationToken;
        await using var scope = new RecoveryTestScope();
        var fixture = InstallFreshSwitchFixture(scope);
        SetSessionGeneration(fixture.Session, fixture.Generation + 1);
        using var extractor = new DisposableHuyaExtractor();
        using var cancellation = new CancellationTokenSource();
        InstallCompletedProbe(scope, fixture.Session, fixture.Generation, fixture.Candidates,
            SuccessfulAlternativeResults(fixture.Candidates), extractor, cancellation);

        await ProcessProbesAsync(scope, testCancellation).WaitAsync(TestTimeout, testCancellation);

        Assert.False(fixture.State.IsLineIsolated(fixture.Candidates[0].Key, DateTime.UtcNow));
        Assert.Equal(fixture.Candidates[0].Key, fixture.State.SelectedLineKey);
        Assert.Null(scope.Budget.LastActionAtUtc);
        Assert.Equal(0, BudgetActionCount(scope.Budget));
        Assert.Same(fixture.Session, GetSessions(scope.Service)[scope.Channel.Id]);
    }

    [Fact]
    public async Task FaultClearedWhileWaitingForCommit_DoesNotSwitchOrConsumeBudget()
    {
        CancellationToken testCancellation = TestContext.Current.CancellationToken;
        await using var scope = new RecoveryTestScope();
        var fixture = InstallFreshSwitchFixture(scope);
        using var extractor = new DisposableHuyaExtractor();
        using var cancellation = new CancellationTokenSource();
        InstallCompletedProbe(scope, fixture.Session, fixture.Generation, fixture.Candidates,
            SuccessfulAlternativeResults(fixture.Candidates), extractor, cancellation);
        using var startupLock = new SemaphoreSlim(0, 1);
        PrivateDictionary<SemaphoreSlim>(scope.Service, "_startupLocks")[scope.Channel.Id] = startupLock;
        Task? processing = null;
        bool released = false;
        try
        {
            processing = ProcessProbesAsync(scope, testCancellation);
            Assert.False(processing.IsCompleted);
            Assert.Null(scope.Budget.LastActionAtUtc);

            // The stale output justified probing, but it recovers while the per-channel
            // operation gate is held. Final commit must re-read it, not trust selection.
            Directory.CreateDirectory(scope.ChannelDirectory);
            await File.WriteAllTextAsync(Path.Combine(scope.ChannelDirectory, "stream.m3u8"),
                "#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:100\n#EXTINF:3,\na.ts\n", testCancellation);
            startupLock.Release();
            released = true;
            await processing.WaitAsync(TestTimeout, testCancellation);

            Assert.Equal(fixture.Candidates[0].Key, fixture.State.SelectedLineKey);
            Assert.Null(scope.Budget.LastActionAtUtc);
            Assert.Equal(0, BudgetActionCount(scope.Budget));
            Assert.Same(fixture.Session, GetSessions(scope.Service)[scope.Channel.Id]);
            Assert.Equal(0, Globals.Metrics.GetValueOrDefault(scope.Channel.Id)?.RestartCount ?? 0);
        }
        finally
        {
            if (!released) startupLock.Release();
            // Always drain the blocked operation before scope shutdown, even when an
            // assertion or fixture write fails and the runner cancels the test.
            if (processing != null)
            {
#pragma warning disable xUnit1051
                await processing.WaitAsync(TestTimeout, CancellationToken.None);
#pragma warning restore xUnit1051
            }
        }
    }

    private sealed record SwitchFixture(StreamingSession Session, HuyaChannelLineState State,
        long Generation, HuyaLineCandidate[] Candidates);

    private static SwitchFixture InstallFreshSwitchFixture(RecoveryTestScope scope)
    {
        DateTime now = DateTime.UtcNow;
        var session = scope.InstallSentinelSession("switch-session", now.AddMinutes(-5));
        HuyaLineCandidate[] candidates =
        [
            new("TX", new Uri("https://tx.huya.com/live"), "local-test", "m3u8", "", ""),
            new("AL", new Uri("https://al.huya.com/live"), "local-test", "m3u8", "", "")
        ];
        var state = new HuyaChannelLineState(StreamManagerService.SourceFingerprint(scope.Channel));
        long generation = state.BindSession(session.SessionId, candidates[0].Key, now.AddMinutes(-5));
        SetSessionGeneration(session, generation);
        PrivateDictionary<HuyaChannelLineState>(scope.Service, "_huyaLineStates")[scope.Channel.Id] = state;
        return new(session, state, generation, candidates);
    }

    private static HuyaLineProbeResult[] SuccessfulAlternativeResults(HuyaLineCandidate[] candidates) =>
    [
        new(candidates[0].Key, false, "LengthMismatch", 1, 1, 188 * 3, TimeSpan.FromMilliseconds(10)),
        new(candidates[1].Key, true, "Healthy", 0.2, 2, 188 * 6, TimeSpan.FromMilliseconds(20))
    ];

    private static void InstallCompletedProbe(RecoveryTestScope scope, StreamingSession session,
        long generation, HuyaLineCandidate[] candidates, HuyaLineProbeResult[] results,
        HuyaExtractor extractor, CancellationTokenSource cancellation)
    {
        DateTime completed = DateTime.UtcNow;
        object batch = NestedConstructor("HuyaProbeBatch").Invoke([results, false, completed]);
        object task = typeof(Task).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == nameof(Task.FromResult) && method.IsGenericMethodDefinition)
            .MakeGenericMethod(batch.GetType()).Invoke(null, [batch])!;
        object job = NestedConstructor("HuyaProbeJob").Invoke([
            Guid.NewGuid(), completed.AddSeconds(-1), StreamManagerService.SourceFingerprint(scope.Channel),
            session.SessionId, generation, candidates[0].Key, extractor, candidates, cancellation, task
        ]);
        object jobs = PrivateField("_huyaProbeJobs").GetValue(scope.Service)!;
        jobs.GetType().GetProperty("Item")!.SetValue(jobs, job, [scope.Channel.Id]);
    }

    private static Task ProcessProbesAsync(RecoveryTestScope scope, CancellationToken cancellationToken) =>
        (Task)PrivateMethod("ProcessHuyaProbesAsync").Invoke(scope.Service, [new[] { scope.Channel }, cancellationToken])!;

    private static int BudgetActionCount(AutomaticRecoveryBudget budget) =>
        ((Queue<DateTime>)typeof(AutomaticRecoveryBudget).GetField("_actions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(budget)!).Count;

    private static void SetSessionGeneration(StreamingSession session, long generation) =>
        typeof(StreamingSession).GetField("<RouteGeneration>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(session, generation);

    private static ChannelConfig Source() => new()
    {
        Id = "source", Name = "source", Platform = "huya", Url = "https://www.huya.com/local-test",
        Quality = "OD", Cookies = "test=credential"
    };

    private static MethodInfo PrivateMethod(string name) =>
        typeof(StreamManagerService).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"未找到测试目标方法：{name}");

    private static FieldInfo PrivateField(string name) =>
        typeof(StreamManagerService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"未找到测试目标字段：{name}");

    private static ConstructorInfo NestedConstructor(string name) =>
        typeof(StreamManagerService).GetNestedType(name, BindingFlags.NonPublic)!
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .OrderByDescending(constructor => constructor.GetParameters().Length).First();

    private static ConcurrentDictionary<string, TValue> PrivateDictionary<TValue>(StreamManagerService service, string name) =>
        (ConcurrentDictionary<string, TValue>)PrivateField(name).GetValue(service)!;

    private static ConcurrentDictionary<string, StreamingSession> GetSessions(StreamManagerService service) =>
        PrivateDictionary<StreamingSession>(service, "_sessions");

    private static StreamingSession SentinelSession(string sessionId, DateTime createdAtUtc)
    {
        // Intentionally has no Process, readers or log handles. Most tests exit before
        // touching resources; the positive switch test explicitly exercises only the
        // null-safe controlled disposal. No process is ever constructed here.
        var session = (StreamingSession)RuntimeHelpers.GetUninitializedObject(typeof(StreamingSession));
        typeof(StreamingSession).GetField("<SessionId>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(session, sessionId);
        typeof(StreamingSession).GetField("<CreatedAtUtc>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(session, createdAtUtc);
        return session;
    }

    private sealed class DisposableHuyaExtractor : HuyaExtractor, IDisposable
    {
        internal DisposableHuyaExtractor() : base(null) { }
        public void Dispose() => _httpClient.Dispose();
    }

    private sealed class DelayedExtractor : BaseExtractor, IDisposable
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _source;
        internal DelayedExtractor(string id) : base(null) =>
            _source = $"file:///D:/Devtools/Codex/LiveStreamGateway/huya-failover-fix-20261113/nonexistent-{id}.ts";
        internal Task Entered => _entered.Task;
        public override Task<string?> GetStreamUrlAsync(string url, string quality)
        {
            _entered.TrySetResult();
            return _result.Task;
        }
        internal void Complete() => _result.TrySetResult(_source);
        public void Dispose() => _httpClient.Dispose();
    }

    private sealed class RecoveryTestScope : IAsyncDisposable
    {
        private readonly StreamManagerService? _previousStreamManager = Globals.StreamManager;
        private readonly List<StreamingSession> _sentinels = [];
        private Task? _start;
        internal RecoveryTestScope()
        {
            string id = "huya-recovery-test-" + Guid.NewGuid().ToString("N");
            Channel = new ChannelConfig
            {
                Id = id, Name = id, Platform = "integration-test", Url = "integration-test://local-only/" + id,
                Quality = "OD", Cookies = ""
            };
            Status = new ChannelStatus { Id = id, Name = id, State = ChannelState.Ready };
            Extractor = new DelayedExtractor(id);
            Service = new StreamManagerService();
            Budget = new AutomaticRecoveryBudget();
            PrivateDictionary<AutomaticRecoveryBudget>(Service, "_automaticRecoveryBudgets")[id] = Budget;
            Globals.Extractors[id] = Extractor;
            lock (Globals.ConfigLock) Globals.Config.Channels.Add(Channel);
            lock (Globals.StatusLock) Globals.ChannelStatuses.Add(Status);
        }
        internal StreamManagerService Service { get; }
        internal ChannelConfig Channel { get; }
        internal ChannelStatus Status { get; }
        internal DelayedExtractor Extractor { get; }
        internal AutomaticRecoveryBudget Budget { get; }
        internal string ChannelDirectory => Path.Combine(Globals.HLS_FULL_PATH, Channel.Id);
        internal StreamingSession InstallSentinelSession(string id, DateTime createdAtUtc)
        {
            var session = SentinelSession(id, createdAtUtc);
            _sentinels.Add(session);
            GetSessions(Service)[Channel.Id] = session;
            return session;
        }
        internal Task StartGuardedAsync(CancellationToken cancellationToken) =>
            _start = (Task)PrivateMethod("StartSingleChannelGuardedAsync").Invoke(Service,
                [Channel, Status, cancellationToken, StreamManagerService.SourceFingerprint(Channel)])!;
        internal void ReplaceConfiguredChannel(bool disabled)
        {
            lock (Globals.ConfigLock)
            {
                int index = Globals.Config.Channels.FindIndex(item => item.Id == Channel.Id);
                Globals.Config.Channels[index] = new ChannelConfig
                {
                    Id = Channel.Id, Name = Channel.Name, Platform = Channel.Platform, Url = Channel.Url,
                    Quality = disabled ? Channel.Quality : "HD", Cookies = Channel.Cookies, Enable = !disabled
                };
            }
        }
        public async ValueTask DisposeAsync()
        {
            // Sentinels are deliberately not disposable live sessions. Do not hide or
            // remove an actual late-created session if a future implementation regresses.
            foreach (var sentinel in _sentinels)
                GetSessions(Service).TryRemove(new KeyValuePair<string, StreamingSession>(Channel.Id, sentinel));
            try
            {
                await Service.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);
            }
            finally
            {
                Extractor.Complete();
                try
                {
                    if (_start != null) await _start.WaitAsync(TestTimeout);
                }
                finally
                {
                    try { await Service.StopAndCleanChannelAsync(Channel.Id).WaitAsync(TestTimeout); }
                    finally
                    {
                        Service.Dispose();
                        Extractor.Dispose();
                        Globals.Extractors.TryRemove(Channel.Id, out _);
                        Globals.Metrics.TryRemove(Channel.Id, out _);
                        Globals.M3u8Cache.TryRemove(Channel.Id, out _);
                        Globals.LastClientAccessTime.TryRemove(Channel.Id, out _);
                        if (Globals.HuyaRefreshLocks.TryRemove(Channel.Id, out var refreshLock)) refreshLock.Dispose();
                        lock (Globals.ConfigLock) Globals.Config.Channels.RemoveAll(item => item.Id == Channel.Id);
                        lock (Globals.StatusLock) Globals.ChannelStatuses.RemoveAll(item => item.Id == Channel.Id);
                        Globals.StreamManager = _previousStreamManager;
                        if (Directory.Exists(ChannelDirectory)) Directory.Delete(ChannelDirectory, recursive: true);
                    }
                }
            }
        }
    }
}
