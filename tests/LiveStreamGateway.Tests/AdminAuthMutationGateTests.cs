using Microsoft.AspNetCore.Http;

namespace LiveStreamGateway.Tests;

public sealed class AdminAuthMutationGateTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData("/api/auth/login")]
    [InlineData("/api/auth/setup")]
    [InlineData("/api/auth/change-password")]
    [InlineData("/api/auth/logout")]
    [InlineData("/api/playback-token/rotate")]
    [InlineData("/api/playback-token/rotate/")]
    [InlineData("/API/PLAYBACK-TOKEN/ROTATE/")]
    public async Task SecurityMutations_WaitForPriorMutation(string path)
    {
        using var gate = new AdminAuthMutationGate();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task first = gate.InvokeAsync(Context("POST", "/api/auth/change-password"),
            _ => release.Task);
        bool secondEntered = false;
        Task second = gate.InvokeAsync(Context("POST", path), _ =>
        {
            secondEntered = true;
            return Task.CompletedTask;
        });
        try
        {
            Assert.False(secondEntered);
            Assert.False(second.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(Timeout, TestContext.Current.CancellationToken);
        }
        Assert.True(secondEntered);
    }

    [Theory]
    [InlineData("GET", "/api/auth/session")]
    [InlineData("GET", "/api/status")]
    [InlineData("GET", "/p/123456.m3u")]
    [InlineData("GET", "/favicon.ico")]
    [InlineData("POST", "/api/channels")]
    [InlineData("POST", "/api/playback-token/auth")]
    public async Task OtherRequests_AreNotBlockedBySecurityMutation(string method, string path)
    {
        using var gate = new AdminAuthMutationGate();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task first = gate.InvokeAsync(Context("POST", "/api/auth/change-password"), _ => release.Task);
        try
        {
            bool entered = false;
            await gate.InvokeAsync(Context(method, path), _ =>
            {
                entered = true;
                return Task.CompletedTask;
            }).WaitAsync(Timeout, TestContext.Current.CancellationToken);
            Assert.True(entered);
            Assert.False(first.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await first.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task QueuedRotation_IsAuthenticatedAfterPasswordChangeInvalidatesOldSession()
    {
        using var gate = new AdminAuthMutationGate();
        using var auth = new AdminAuthService();
        string cookie = auth.CreateSession();
        var rotation = Context("POST", "/api/playback-token/rotate");
        rotation.Request.Headers.Cookie = $"{AdminAuthService.SessionCookieName}={cookie}";
        Assert.True(auth.IsAuthenticated(rotation.Request));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task changePassword = gate.InvokeAsync(Context("POST", "/api/auth/change-password"), async _ =>
        {
            await release.Task;
            auth.InvalidateAllSessions();
        });
        bool rotationRan = false;
        Task queuedRotation = gate.InvokeAsync(rotation, context =>
        {
            // 与生产管线一致：门禁内部才执行管理会话鉴权。
            if (!auth.IsAuthenticated(context.Request))
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            else
                rotationRan = true;
            return Task.CompletedTask;
        });
        try
        {
            Assert.False(queuedRotation.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(changePassword, queuedRotation).WaitAsync(Timeout, TestContext.Current.CancellationToken);
        }
        Assert.False(rotationRan);
        Assert.Equal(StatusCodes.Status401Unauthorized, rotation.Response.StatusCode);
    }

    [Fact]
    public async Task CancelledWait_DoesNotRunHandlerOrReleaseAnotherRequestsPermit()
    {
        using var gate = new AdminAuthMutationGate();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task first = gate.InvokeAsync(Context("POST", "/api/auth/login"), _ => release.Task);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var cancelledRequest = Context("POST", "/api/playback-token/rotate");
        cancelledRequest.RequestAborted = cancellation.Token;
        bool cancelledHandlerRan = false;
        Task cancelled = gate.InvokeAsync(cancelledRequest, _ =>
        {
            cancelledHandlerRan = true;
            return Task.CompletedTask;
        });
        Task third = Task.CompletedTask;
        bool thirdEntered = false;
        try
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            Assert.False(cancelledHandlerRan);
            third = gate.InvokeAsync(Context("POST", "/api/auth/logout"), _ =>
            {
                thirdEntered = true;
                return Task.CompletedTask;
            });
            Assert.False(thirdEntered);
            Assert.False(third.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(first, third).WaitAsync(Timeout, TestContext.Current.CancellationToken);
        }
        Assert.True(thirdEntered);
    }

    [Fact]
    public async Task HandlerFailure_ReleasesGateForNextMutation()
    {
        using var gate = new AdminAuthMutationGate();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.InvokeAsync(Context("POST", "/api/auth/login"), _ => throw new InvalidOperationException("test failure")));

        bool entered = false;
        await gate.InvokeAsync(Context("POST", "/api/playback-token/rotate"), _ =>
        {
            entered = true;
            return Task.CompletedTask;
        }).WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.True(entered);
    }

    private static DefaultHttpContext Context(string method, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        return context;
    }
}
