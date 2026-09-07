using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using LiveStreamGateway;

namespace LiveStreamGateway.Tests;

public class DouyuExtractorTests
{
    [Theory]
    [InlineData("{\"roomId\":\"6657\",\"rid\":\"6979222\"}")]
    [InlineData("{\"roomId\":\"6657\",\"rid\":6979222}")]
    [InlineData("{\\\"roomId\\\":\\\"6657\\\",\\\"rid\\\":6979222}")]
    public void MobilePage_ExtractsCanonicalRoomId(string html)
    {
        Assert.Equal("6979222", DouyuRoomResolver.ExtractCanonicalRoomId(html));
    }

    [Fact]
    public async Task NumericVanityRoom_ResolvesToCanonicalRid()
    {
        Uri? requestedUri = null;
        using var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            requestedUri = request.RequestUri;
            return JsonResponse("{\"roomId\":\"6657\",\"rid\":\"6979222\"}");
        }));

        string roomId = await DouyuRoomResolver.ResolveCanonicalRoomIdAsync(
            client,
            "https://www.douyu.com/6657");

        Assert.Equal("6979222", roomId);
        Assert.Equal("https://m.douyu.com/6657", requestedUri?.AbsoluteUri);
    }

    [Fact]
    public async Task NumericRoom_FallsBackWhenMobilePageIsUnavailable()
    {
        using var client = new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        string roomId = await DouyuRoomResolver.ResolveCanonicalRoomIdAsync(
            client,
            "https://www.douyu.com/6979222");

        Assert.Equal("6979222", roomId);
    }

    [Fact]
    public void SuccessfulPlayResponse_ReturnsStreamUrl()
    {
        var response = JsonNode.Parse("""
            {
              "error": 0,
              "msg": "ok",
              "data": {
                "rtmp_url": "https://example.invalid/live/",
                "rtmp_live": "/stream.flv?token=redacted"
              }
            }
            """);

        string streamUrl = DouyuExtractor.ExtractStreamUrlOrThrow(response, "6979222");

        Assert.Equal("https://example.invalid/live/stream.flv?token=redacted", streamUrl);
    }

    [Theory]
    [InlineData(102)]
    [InlineData(104)]
    public void ExplicitOfflineResponse_IsClassifiedAsNotLive(int errorCode)
    {
        var response = JsonNode.Parse($"{{\"error\":{errorCode},\"msg\":\"房间未开播\"}}");

        var exception = Assert.Throws<Exception>(() =>
            DouyuExtractor.ExtractStreamUrlOrThrow(response, "6979222"));

        Assert.Contains("Not Live", exception.Message);
        Assert.Contains($"error={errorCode}", exception.Message);
        Assert.True(StreamManagerService.IsOfflineResult($"解析失败: {exception.Message}"));
    }

    [Theory]
    [InlineData("房间未开播")]
    [InlineData("")]
    public void MinusFiveOfflineResponse_IsNotMisclassifiedAsCookieInvalid(string message)
    {
        var response = JsonNode.Parse($"{{\"error\":-5,\"msg\":\"{message}\"}}");

        var exception = Assert.Throws<Exception>(() =>
            DouyuExtractor.ExtractStreamUrlOrThrow(response, "6979222"));
        string error = $"解析失败: {exception.Message}";

        Assert.Contains("Not Live", exception.Message);
        Assert.Contains("error=-5", exception.Message);
        Assert.True(StreamManagerService.IsOfflineResult(error));
        Assert.False(StreamManagerService.IsAuthenticationError(error));
    }

    [Theory]
    [InlineData(-5, "用户未登录")]
    [InlineData(51, "token已过期")]
    public void ExplicitAuthenticationMessage_IsClassifiedAsCookieInvalid(int errorCode, string message)
    {
        var response = JsonNode.Parse($"{{\"error\":{errorCode},\"msg\":\"{message}\"}}");

        var exception = Assert.Throws<Exception>(() =>
            DouyuExtractor.ExtractStreamUrlOrThrow(response, "6979222"));
        string error = $"解析失败: {exception.Message}";

        Assert.Contains("Cookie Invalid", exception.Message);
        Assert.Contains($"error={errorCode}", exception.Message);
        Assert.False(StreamManagerService.IsOfflineResult(error));
        Assert.True(StreamManagerService.IsAuthenticationError(error));
    }

    [Fact]
    public void AmbiguousCode51_IsPreservedAsExtractorError()
    {
        var response = JsonNode.Parse("{\"error\":51,\"msg\":\"请求暂时失败\"}");

        var exception = Assert.Throws<Exception>(() =>
            DouyuExtractor.ExtractStreamUrlOrThrow(response, "6979222"));
        string error = $"解析失败: Douyu Error: {exception.Message}";

        Assert.Contains("error=51", exception.Message);
        Assert.DoesNotContain("Cookie Invalid", exception.Message);
        Assert.False(StreamManagerService.IsOfflineResult(error));
        Assert.False(StreamManagerService.IsAuthenticationError(error));
    }

    [Fact]
    public void UnknownPlayError_PreservesCodeAndIsNotClassifiedAsOffline()
    {
        var response = JsonNode.Parse("{\"error\":-4,\"msg\":\"房间被封禁\"}");

        var exception = Assert.Throws<Exception>(() =>
            DouyuExtractor.ExtractStreamUrlOrThrow(response, "6657"));

        Assert.Contains("error=-4", exception.Message);
        Assert.Contains("房间被封禁", exception.Message);
        Assert.DoesNotContain("Not Live", exception.Message);
        Assert.DoesNotContain("未开播", exception.Message);
        Assert.False(StreamManagerService.IsOfflineResult($"解析失败: Douyu Error: {exception.Message}"));
    }

    private static HttpResponseMessage JsonResponse(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
