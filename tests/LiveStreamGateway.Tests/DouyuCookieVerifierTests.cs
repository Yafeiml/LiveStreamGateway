using LiveStreamGateway;

namespace LiveStreamGateway.Tests;

public class DouyuCookieVerifierTests
{
    [Fact]
    public void SuccessResponse_MarksCookieValidAndDecodesNickname()
    {
        var status = CreateStatus();

        CookieVerifier.ApplyDouyuVerificationResponse(
            status,
            "acf_uid=123; acf_nickname=%E6%B5%8B%E8%AF%95%E7%94%A8%E6%88%B7",
            "{\"error\":0,\"data\":{\"total\":0}}");

        Assert.True(status.IsValid);
        Assert.False(status.IsNetworkError);
        Assert.Equal("测试用户", status.Username);
        Assert.Equal("已授权有效", status.Message);
    }

    [Theory]
    [InlineData("{\"error\":-1,\"msg\":\"用户未登陆或token已过期\"}")]
    [InlineData("{\"error\":100,\"msg\":\"用户未登录\"}")]
    public void ExplicitLoginRejection_MarksCookieExpired(string responseBody)
    {
        var status = CreateStatus();

        CookieVerifier.ApplyDouyuVerificationResponse(status, "acf_uid=123", responseBody);

        Assert.False(status.IsValid);
        Assert.False(status.IsNetworkError);
        Assert.Equal("Cookie已失效或未登录", status.Message);
    }

    [Fact]
    public void UnknownUpstreamError_DoesNotMarkCookieExpired()
    {
        var status = CreateStatus();

        CookieVerifier.ApplyDouyuVerificationResponse(
            status,
            "acf_uid=123",
            "{\"error\":5001,\"msg\":\"service unavailable\"}");

        Assert.False(status.IsValid);
        Assert.True(status.IsNetworkError);
        Assert.Equal("斗鱼认证响应异常 (error=5001)", status.Message);
    }

    [Theory]
    [InlineData("<html>not found</html>")]
    [InlineData("{\"msg\":\"missing error code\"}")]
    public void InvalidResponseShape_DoesNotMarkCookieExpired(string responseBody)
    {
        var status = CreateStatus();

        CookieVerifier.ApplyDouyuVerificationResponse(status, "acf_uid=123", responseBody);

        Assert.False(status.IsValid);
        Assert.True(status.IsNetworkError);
        Assert.Equal("斗鱼认证响应格式异常", status.Message);
    }

    private static PlatformCookieStatus CreateStatus() => new()
    {
        Platform = "douyu",
        Configured = true
    };
}
