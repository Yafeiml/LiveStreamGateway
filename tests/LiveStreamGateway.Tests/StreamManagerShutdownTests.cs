using System.Collections.Concurrent;
using System.Reflection;

namespace LiveStreamGateway.Tests;

[CollectionDefinition("StreamManager shutdown globals", DisableParallelization = true)]
public sealed class StreamManagerShutdownCollection
{
}

[Collection("StreamManager shutdown globals")]
public sealed class StreamManagerShutdownTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task StopDuringSourceResolution_PreventsLateSessionCreationWithoutReportingFfmpegFailure()
    {
        CancellationToken testCancellation = TestContext.Current.CancellationToken;
        await using var scope = new ShutdownTestScope();
        Task start = scope.StartChannelAsync(testCancellation);
        await scope.Extractor.Entered.WaitAsync(TestTimeout, testCancellation);
        Assert.False(start.IsCompleted);

        // StopAsync must seal session creation without waiting for the extractor.
        await scope.Service.StopAsync(testCancellation).WaitAsync(TestTimeout, testCancellation);
        Assert.False(start.IsCompleted);

        scope.Extractor.Complete();
        await start.WaitAsync(TestTimeout, testCancellation);

        AssertNoSessionWasStarted(scope);
    }

    [Fact]
    public async Task CancellationDuringSourceResolution_PreventsSessionCreationWithoutReportingFfmpegFailure()
    {
        CancellationToken testCancellation = TestContext.Current.CancellationToken;
        await using var scope = new ShutdownTestScope();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(testCancellation);
        Task start = scope.StartChannelAsync(cancellation.Token);
        await scope.Extractor.Entered.WaitAsync(TestTimeout, testCancellation);

        cancellation.Cancel();
        scope.Extractor.Complete();
        await start.WaitAsync(TestTimeout, testCancellation);

        AssertNoSessionWasStarted(scope);
    }

    [Fact]
    public async Task RepeatedStopAsync_IsSafeAndReusesTheSameSessionCleanupTask()
    {
        CancellationToken testCancellation = TestContext.Current.CancellationToken;
        await using var scope = new ShutdownTestScope();

        await scope.Service.StopAsync(testCancellation).WaitAsync(TestTimeout, testCancellation);
        Task firstCleanup = InvokeStopAllSessions(scope.Service);
        Task secondCleanup = InvokeStopAllSessions(scope.Service);
        Assert.Same(firstCleanup, secondCleanup);
        Assert.Same(firstCleanup, PrivateField("_shutdownTask").GetValue(scope.Service));
        Assert.True((bool)PrivateField("_stopping").GetValue(scope.Service)!);

        await Task.WhenAll(
            scope.Service.StopAsync(testCancellation),
            scope.Service.StopAsync(testCancellation)).WaitAsync(TestTimeout, testCancellation);

        Assert.Same(firstCleanup, InvokeStopAllSessions(scope.Service));
        await firstCleanup.WaitAsync(TestTimeout, testCancellation);
        Assert.Empty(GetSessions(scope.Service));
    }

    [Fact]
    public async Task DisposeAsync_WaitsForAlreadyPendingSessionDisposalAndClosesStartupGate()
    {
        CancellationToken testCancellation = TestContext.Current.CancellationToken;
        await using var scope = new ShutdownTestScope();
        var releaseDisposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = (HashSet<Task>)PrivateField("_pendingSessionDisposals").GetValue(scope.Service)!;
        object lifecycleLock = PrivateField("_sessionLifecycleLock").GetValue(scope.Service)!;
        lock (lifecycleLock)
            pending.Add(releaseDisposal.Task);

        Task? disposal = null;
        try
        {
            // No StartAsync call: host startup failure must still await asynchronous resource cleanup.
            disposal = scope.Service.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            Assert.True((bool)PrivateField("_stopping").GetValue(scope.Service)!);
            Assert.False(InvokeStopAllSessions(scope.Service).IsCompleted);

            releaseDisposal.SetResult();
            await disposal.WaitAsync(TestTimeout, testCancellation);
            Assert.Empty(GetSessions(scope.Service));
        }
        finally
        {
            // Never leave a blocked shutdown task behind when an assertion fails.
            releaseDisposal.TrySetResult();
            try
            {
                if (disposal != null)
                {
                    // Teardown must finish even if the test runner has already cancelled this test.
#pragma warning disable xUnit1051
                    await disposal.WaitAsync(TestTimeout, CancellationToken.None);
#pragma warning restore xUnit1051
                }
            }
            finally
            {
                lock (lifecycleLock)
                    pending.Remove(releaseDisposal.Task);
            }
        }
    }

    [Fact]
    public async Task DisposeBeforeHostStart_ClosesGateAndSupportsRepeatedDisposeAndStop()
    {
        CancellationToken testCancellation = TestContext.Current.CancellationToken;
        await using var scope = new ShutdownTestScope();

        await ((IAsyncDisposable)scope.Service).DisposeAsync().AsTask().WaitAsync(TestTimeout, testCancellation);
        Assert.True((bool)PrivateField("_stopping").GetValue(scope.Service)!);
        Task cleanup = InvokeStopAllSessions(scope.Service);
        Assert.True(cleanup.IsCompletedSuccessfully);

        scope.Service.Dispose();
        await scope.Service.StopAsync(testCancellation).WaitAsync(TestTimeout, testCancellation);
        await scope.Service.DisposeAsync().AsTask().WaitAsync(TestTimeout, testCancellation);
        scope.Service.Dispose();

        Assert.Same(cleanup, InvokeStopAllSessions(scope.Service));
        Assert.Empty(GetSessions(scope.Service));
        Assert.Equal(ChannelState.Ready, scope.Status.State);
    }

    private static void AssertNoSessionWasStarted(ShutdownTestScope scope)
    {
        Assert.Empty(GetSessions(scope.Service));
        Assert.Equal(ChannelState.Ready, scope.Status.State);
        Assert.DoesNotContain("已启动推流", scope.Status.Message);
        Assert.DoesNotContain("FFmpeg 进程启动失败", scope.Status.Message);

        Assert.True(Globals.Metrics.TryGetValue(scope.Channel.Id, out var metrics));
        Assert.Equal(1L, metrics!.ProbeCount);
        Assert.Equal(0, metrics.StartCount);
        Assert.Equal(0, metrics.ErrorCount);
        Assert.Null(metrics.LastErrorAt);
        Assert.Empty(Directory.EnumerateFiles(scope.ChannelDirectory, "ffmpeg*.log"));
    }

    private static Task InvokeStopAllSessions(StreamManagerService service) =>
        (Task)PrivateMethod("StopAllSessionsAsync").Invoke(service, null)!;

    private static MethodInfo PrivateMethod(string name) =>
        typeof(StreamManagerService).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"未找到测试目标方法：{name}");

    private static FieldInfo PrivateField(string name) =>
        typeof(StreamManagerService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"未找到测试目标字段：{name}");

    private static ConcurrentDictionary<string, StreamingSession> GetSessions(StreamManagerService service)
        => (ConcurrentDictionary<string, StreamingSession>)PrivateField("_sessions").GetValue(service)!;

    private sealed class DelayedExtractor : BaseExtractor, IDisposable
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _sourceUrl;

        internal DelayedExtractor(string channelId) : base(null)
        {
            // Even a regression can only point FFmpeg at a nonexistent local file, never an external host.
            _sourceUrl = $"file:///D:/Devtools/LSG.ShutdownTests/{channelId}.ts";
        }

        internal Task Entered => _entered.Task;

        public override Task<string?> GetStreamUrlAsync(string url, string quality)
        {
            _entered.TrySetResult();
            return _result.Task;
        }

        internal void Complete() => _result.TrySetResult(_sourceUrl);

        public void Dispose() => _httpClient.Dispose();
    }

    private sealed class ShutdownTestScope : IAsyncDisposable
    {
        private readonly StreamManagerService? _previousStreamManager = Globals.StreamManager;
        private Task? _startTask;

        internal ShutdownTestScope()
        {
            string id = "shutdown-test-" + Guid.NewGuid().ToString("N");
            Channel = new ChannelConfig
            {
                Id = id,
                Name = id,
                Platform = "shutdown-test",
                Url = "shutdown-test://local-only/" + id,
                Quality = "OD"
            };
            Status = new ChannelStatus { Id = id, Name = id, State = ChannelState.Ready };
            Extractor = new DelayedExtractor(id);
            Service = new StreamManagerService();
            Globals.Extractors[id] = Extractor;
            lock (Globals.StatusLock)
                Globals.ChannelStatuses.Add(Status);
        }

        internal StreamManagerService Service { get; }
        internal ChannelConfig Channel { get; }
        internal ChannelStatus Status { get; }
        internal DelayedExtractor Extractor { get; }
        internal string ChannelDirectory => Path.Combine(Globals.HLS_FULL_PATH, Channel.Id);

        internal Task StartChannelAsync(CancellationToken cancellationToken) =>
            _startTask = (Task)PrivateMethod("StartSingleChannelCoreAsync")
                .Invoke(Service, [Channel, Status, cancellationToken])!;

        public async ValueTask DisposeAsync()
        {
            try
            {
                // Seal startup before releasing a pending resolver, including on assertion failures.
                await Service.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);
            }
            finally
            {
                Extractor.Complete();
                try
                {
                    if (_startTask != null)
                        await _startTask.WaitAsync(TestTimeout);
                }
                finally
                {
                    try
                    {
                        // Explicit cleanup also removes a late session if a future regression creates one.
                        await Service.StopAndCleanChannelAsync(Channel.Id).WaitAsync(TestTimeout);
                    }
                    finally
                    {
                        Service.Dispose();
                        Extractor.Dispose();
                        Globals.Extractors.TryRemove(Channel.Id, out _);
                        Globals.Metrics.TryRemove(Channel.Id, out _);
                        Globals.M3u8Cache.TryRemove(Channel.Id, out _);
                        Globals.LastClientAccessTime.TryRemove(Channel.Id, out _);
                        if (Globals.HuyaRefreshLocks.TryRemove(Channel.Id, out var refreshLock))
                            refreshLock.Dispose();
                        lock (Globals.StatusLock)
                            Globals.ChannelStatuses.Remove(Status);
                        Globals.StreamManager = _previousStreamManager;

                        // Only this test's GUID-named child is removed, never the shared HLS root.
                        if (Directory.Exists(ChannelDirectory))
                            Directory.Delete(ChannelDirectory, recursive: true);
                    }
                }
            }
        }
    }
}
