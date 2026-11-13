using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using LiveStreamGateway;

namespace LiveStreamGateway.Tests;

public class HuyaLineProbeTests
{
    [Fact]
    public void CandidateSnapshotKeepsAllLinesAndSelectionAcrossSignatureRefresh()
    {
        var extractor = CreateExtractor();
        Assert.Equal(4, extractor.Candidates.Count);
        Assert.Equal("TX", extractor.SelectedLine?.CdnType);
        var previousSnapshot = extractor.Candidates;
        var alternate = extractor.Candidates.Single(line => line.CdnType == "AL");
        Assert.True(extractor.TrySelectLine(alternate.Key));
        Assert.True(extractor.InstallCandidates(CandidateList("changed_signature", "new-stream"), "HD"));
        Assert.Equal(alternate.Key, extractor.SelectedLine?.Key);
        Assert.Equal("stream", previousSnapshot[0].StreamName);
        Assert.Equal("new-stream", extractor.SelectedLine?.StreamName);
        Assert.Contains("ratio=2000", extractor.GetFreshUrl());
        Assert.DoesNotContain("changed_signature", extractor.SelectedLine!.ToString());
        Assert.DoesNotContain("new-stream", extractor.SelectedLine.ToString());
        Assert.False(extractor.TrySelectLine("missing"));
        Assert.Equal(alternate.Key, extractor.SelectedLine.Key);
        Assert.True(((IList<HuyaLineCandidate>)extractor.Candidates).IsReadOnly);
    }

    [Fact]
    public void CandidateSignatureAndStreamFieldsRemainIndependent()
    {
        var extractor = new HuyaExtractor(null);
        var list = new JsonArray
        {
            Candidate("AL", "https://al.hls.huya.com/live", "al-stream", "YWxfMA=="),
            Candidate("TX", "https://tx.hls.huya.com/live", "tx-stream", "dHhfMA==")
        };
        Assert.True(extractor.InstallCandidates(list, "SD"));
        var al = extractor.Candidates[0];
        var tx = extractor.Candidates[1];
        Assert.Contains("/al-stream.m3u8?", extractor.GetFreshUrl(al));
        Assert.Contains("/tx-stream.m3u8?", extractor.GetFreshUrl(tx));
        Assert.Equal("fm=YWxfMA==&ctype=test&fs=1", al.AntiCode);
        Assert.Equal("fm=dHhfMA==&ctype=test&fs=1", tx.AntiCode);
        Assert.Contains("ratio=1000", extractor.GetFreshUrl(al));
    }

    [Theory]
    [InlineData("HW", "HS", "AL", "HW")]
    [InlineData("HS", "OTHER", "AL", "AL")]
    [InlineData("OTHER", "HS", "ANOTHER", "HS")]
    [InlineData("OTHER", "FIRST", "ANOTHER", "OTHER")]
    public void DefaultPriorityIsTxHwAlHsThenFirst(string first, string second, string third, string expected)
    {
        var extractor = new HuyaExtractor(null);
        Assert.True(extractor.InstallCandidates(new JsonArray
        {
            Candidate(first, $"https://{first.ToLowerInvariant()}.hls.huya.com/live"),
            Candidate(second, $"https://{second.ToLowerInvariant()}.hls.huya.com/live"),
            Candidate(third, $"https://{third.ToLowerInvariant()}.hls.huya.com/live")
        }, "OD"));
        Assert.Equal(expected, extractor.SelectedLine?.CdnType);
    }

    [Fact]
    public void MalformedOrDuplicateCandidateDoesNotDiscardOtherLines()
    {
        var extractor = new HuyaExtractor(null);
        Assert.True(extractor.InstallCandidates(new JsonArray
        {
            null, new JsonObject { ["sHlsUrl"] = 123 },
            Candidate("TX", "file:///bad"),
            Candidate("TX", "https://tx.hls.huya.com/live", "../bad"),
            Candidate("TX", "https://tx.hls.huya.com/live"),
            Candidate("TX", "https://tx.hls.huya.com/live", "duplicate"),
            Candidate("AL", "https://al.hls.huya.com/live")
        }, "OD"));
        Assert.Equal(2, extractor.Candidates.Count);
    }

    [Fact]
    public async Task AdvancingPlaylistWithTwoCompleteTsSamplesIsHealthyAndDoesNotChangeSelection()
    {
        var extractor = CreateExtractor();
        var selectedKey = extractor.SelectedLine!.Key;
        var alternate = extractor.Candidates.Single(line => line.CdnType == "AL");
        int manifests = 0, segments = 0;
        using var client = Client(request =>
        {
            Assert.Equal("al.hls.huya.com", request.RequestUri!.Host);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.Contains("Chrome/120.0.0.0", request.Headers.UserAgent.ToString());
            Assert.Equal("www.huya.com", request.Headers.Referrer?.Host);
            Assert.Equal("https", request.Headers.Referrer?.Scheme);
            if (request.RequestUri.AbsolutePath.EndsWith(".m3u8"))
            {
                Assert.True(request.Headers.CacheControl?.NoCache == true);
                Assert.True(request.Headers.CacheControl?.NoStore == true);
                Assert.Equal("no-cache", request.Headers.GetValues("Pragma").Single());
                return Text(Playlist(++manifests));
            }
            Assert.False(request.Headers.Contains("Cache-Control"));
            Assert.False(request.Headers.Contains("Pragma"));
            segments++;
            return Bytes(TransportStream());
        });
        var result = await Probe(client).ProbeAsync(extractor, alternate, TestContext.Current.CancellationToken);
        Assert.True(result.Success, $"{result.Category} ({result.ExceptionType ?? "none"})");
        Assert.Equal("Healthy", result.Category);
        Assert.Equal(2, result.SampleCount);
        Assert.Equal(2, manifests);
        Assert.Equal(2, segments);
        Assert.True(result.DownloadedBytes > TransportStream().Length * 2);
        Assert.InRange(result.DownloadMediaRatio, 0, 0.8);
        Assert.Equal(selectedKey, extractor.SelectedLine!.Key);
    }

    [Theory]
    [InlineData("html", "InvalidPlaylist")]
    [InlineData("key", "UnsupportedPlaylist")]
    [InlineData("map", "UnsupportedPlaylist")]
    [InlineData("range", "UnsupportedPlaylist")]
    [InlineData("vod", "UnsupportedPlaylist")]
    [InlineData("duration", "InvalidPlaylist")]
    [InlineData("empty", "InvalidPlaylist")]
    public async Task Http200InvalidOrUnsupportedPlaylistCannotQualify(string kind, string expected)
    {
        string text = kind switch
        {
            "html" => "<html>success</html>",
            "key" => "#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"key\"\n" + Playlist(1)[8..],
            "map" => "#EXTM3U\n#EXT-X-MAP:URI=\"init.mp4\"\n" + Playlist(1)[8..],
            "range" => "#EXTM3U\n#EXT-X-BYTERANGE:188@0\n" + Playlist(1)[8..],
            "vod" => Playlist(1) + "\n#EXT-X-ENDLIST",
            "duration" => Playlist(1).Replace("#EXTINF:3", "#EXTINF:NaN"),
            _ => "#EXTM3U\n"
        };
        using var client = Client(_ => Text(text));
        var result = await RunAsync(client);
        Assert.False(result.Success);
        Assert.Equal(expected, result.Category);
        Assert.Equal(0, result.SampleCount);
    }

    [Fact]
    public async Task MasterUsesOnlyFirstVariantLikeProductionAndSharesFreshnessHeaders()
    {
        int mediaManifests = 0, masterManifests = 0, segments = 0;
        using var client = Client(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            Assert.False(path.EndsWith("second.m3u8"));
            if (path.EndsWith(".m3u8"))
            {
                Assert.True(request.Headers.CacheControl?.NoCache == true);
                Assert.True(request.Headers.CacheControl?.NoStore == true);
                Assert.Equal("no-cache", request.Headers.GetValues("Pragma").Single());
                if (path.EndsWith("stream.m3u8"))
                {
                    masterManifests++;
                    return Text(Master("first.m3u8") + "\n#EXT-X-STREAM-INF:BANDWIDTH=1\nsecond.m3u8\n");
                }
                Assert.EndsWith("first.m3u8", path);
                return Text(Playlist(++mediaManifests));
            }
            Assert.False(request.Headers.Contains("Cache-Control"));
            segments++;
            return Bytes(TransportStream());
        });
        var result = await RunAsync(client);
        Assert.True(result.Success, $"{result.Category} ({result.ExceptionType ?? "none"})");
        Assert.Equal(2, masterManifests);
        Assert.Equal(2, mediaManifests);
        Assert.Equal(2, segments);
    }

    [Theory]
    [InlineData("http://127.0.0.1/private.m3u8")]
    [InlineData("https://al.hls.huya.com/first.m3u8")]
    [InlineData("https://tx.hls.huya.com:9898/private.m3u8")]
    [InlineData("file:///private.m3u8")]
    public async Task MasterCannotSendRequestsToCrossOriginOrUnsafeVariants(string variant)
    {
        int requests = 0;
        using var client = Client(_ => { requests++; return Text(Master(variant)); });
        var result = await RunAsync(client);
        Assert.Equal("UnsafePlaylistUri", result.Category);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task MasterLoopIsRejectedWithoutRepeatingNetworkRequest()
    {
        int requests = 0;
        using var client = Client(_ => { requests++; return Text(Master("stream.m3u8?different-signature")); });
        var result = await RunAsync(client);
        Assert.Equal("PlaylistLoop", result.Category);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task MasterDepthCannotExceedOneHop()
    {
        int requests = 0;
        using var client = Client(request =>
        {
            requests++;
            return Text(Master(request.RequestUri!.AbsolutePath.EndsWith("stream.m3u8") ? "first.m3u8" : "second.m3u8"));
        });
        var result = await RunAsync(client);
        Assert.Equal("MasterDepthExceeded", result.Category);
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task MasterChildRedirectIsRejectedRatherThanFollowed()
    {
        using var client = Client(request => request.RequestUri!.AbsolutePath.EndsWith("stream.m3u8")
            ? Text(Master("first.m3u8"))
            : new HttpResponseMessage(HttpStatusCode.Redirect)
            { Headers = { Location = new Uri("https://tx.hls.huya.com/other.m3u8") } });
        var result = await RunAsync(client);
        Assert.Equal("HttpStatus", result.Category);
    }

    [Theory]
    [InlineData("http://127.0.0.1/private.ts")]
    [InlineData("http://192.168.31.251/private.ts")]
    [InlineData("http://tx.hls.huya.com:9898/private.ts")]
    [InlineData("https://evil.example/private.ts")]
    [InlineData("file:///private.ts")]
    [InlineData("https://user:password@tx.hls.huya.com/private.ts")]
    [InlineData("https://tx.hls.huya.com/private.ts#fragment")]
    public async Task UntrustedSegmentUriIsRejectedBeforeNetworkRequest(string segmentUri)
    {
        int requests = 0;
        using var client = Client(_ => { requests++; return Text(Playlist(1).Replace("segment1.ts", segmentUri)); });
        var result = await RunAsync(client);
        Assert.Equal("UnsafeSegmentUri", result.Category);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task RedirectIsNotFollowedOrConsideredHealthy()
    {
        using var client = Client(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("http://127.0.0.1/private") }
        });
        var result = await RunAsync(client);
        Assert.Equal("HttpStatus", result.Category);
    }

    [Fact]
    public async Task AutomaticCrossOriginRedirectDetectedDefensively()
    {
        using var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://evil.example/private"),
            Content = new StringContent(Playlist(1))
        });
        var result = await RunAsync(client);
        Assert.Equal("UnsafeRedirect", result.Category);
    }

    [Fact]
    public async Task UntrustedCandidateOriginIsRejectedWithoutAnyHttpRequest()
    {
        var extractor = new HuyaExtractor(null);
        Assert.True(extractor.InstallCandidates(new JsonArray { Candidate("TX", "http://127.0.0.1/live") }, "OD"));
        int requests = 0;
        using var client = Client(_ => { requests++; return Text(Playlist(1)); });
        var result = await Probe(client).ProbeAsync(extractor, extractor.SelectedLine!, TestContext.Current.CancellationToken);
        Assert.Equal("UnsafeUri", result.Category);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task StalePlaylistCannotQualifyEvenIfTsRequestsWork()
    {
        using var client = Client(request => request.RequestUri!.AbsolutePath.EndsWith(".m3u8")
            ? Text(Playlist(1)) : Bytes(TransportStream()));
        var result = await RunAsync(client);
        Assert.False(result.Success);
        Assert.Equal("PlaylistNotAdvancing", result.Category);
        Assert.Equal(1, result.SampleCount);
    }

    [Theory]
    [InlineData("html", "InvalidTransportStream")]
    [InlineData("partial", "InvalidTransportStream")]
    [InlineData("length", "LengthMismatch")]
    [InlineData("large", "ResponseTooLarge")]
    [InlineData("encoding", "UnsupportedContentEncoding")]
    public async Task Http200BadSegmentCannotQualify(string kind, string expected)
    {
        using var client = Client(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".m3u8")) return Text(Playlist(1));
            var response = Bytes(kind switch
            {
                "html" => Encoding.UTF8.GetBytes("<html>http 200 is not a media stream</html>"),
                "partial" => TransportStream()[..^1],
                _ => TransportStream()
            });
            if (kind == "length") response.Content.Headers.ContentLength = TransportStream().Length + 188;
            if (kind == "large") response.Content.Headers.ContentLength = HuyaLineProbe.MaxSegmentBytes + 1;
            if (kind == "encoding") response.Content.Headers.ContentEncoding.Add("gzip");
            return response;
        });
        var result = await RunAsync(client);
        Assert.Equal(expected, result.Category);
        Assert.False(result.Success);
        Assert.Equal(0, result.SampleCount);
    }

    [Fact]
    public async Task UnknownLengthStreamIsStoppedAtPerSegmentLimit()
    {
        using var client = Client(request => request.RequestUri!.AbsolutePath.EndsWith(".m3u8")
            ? Text(Playlist(1)) : new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new RepeatingStream(HuyaLineProbe.MaxSegmentBytes + 10)) });
        var result = await RunAsync(client);
        Assert.Equal("ResponseTooLarge", result.Category);
        Assert.InRange(result.DownloadedBytes, HuyaLineProbe.MaxSegmentBytes, HuyaLineProbe.MaxSegmentBytes + 1024);
    }

    [Fact]
    public async Task TotalCandidateBudgetIncludesPlaylistsAndBothSegments()
    {
        int manifests = 0;
        int nearLimit = HuyaLineProbe.MaxSegmentBytes / 188 * 188;
        using var client = Client(request => request.RequestUri!.AbsolutePath.EndsWith(".m3u8")
            ? Text(Playlist(++manifests) + "\n#" + new string('x', 1024))
            : new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new RepeatingStream(nearLimit)) });
        var result = await RunAsync(client);
        Assert.Equal("BudgetExceeded", result.Category);
        Assert.Equal(1, result.SampleCount);
        Assert.InRange(result.DownloadedBytes, HuyaLineProbe.MaxSegmentBytes, HuyaLineProbe.MaxTotalBytes + 1L);
    }

    [Fact]
    public async Task TimeoutIsBoundedAndExternalCancellationIsPreserved()
    {
        using var client = new HttpClient(new AsyncHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Text(Playlist(1));
        }));
        var extractor = CreateExtractor();
        var probe = new HuyaLineProbe(client, TimeSpan.FromMilliseconds(30), TimeSpan.Zero);
        var result = await probe.ProbeAsync(extractor, extractor.SelectedLine!, TestContext.Current.CancellationToken);
        Assert.Equal("Timeout", result.Category);
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(2));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.ProbeAsync(extractor, extractor.SelectedLine!, cancelled.Token));
    }

    [Fact]
    public async Task CompleteButSlowSegmentsCannotQualify()
    {
        int manifests = 0;
        using var client = new HttpClient(new AsyncHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".m3u8"))
                return Text(Playlist(++manifests).Replace("#EXTINF:3", "#EXTINF:0.01"));
            await Task.Delay(30, token);
            return Bytes(TransportStream());
        }));
        var result = await RunAsync(client);
        Assert.Equal("SlowDownload", result.Category);
        Assert.False(result.Success);
        Assert.Equal(2, result.SampleCount);
        Assert.True(result.DownloadMediaRatio > 0.8);
    }

    [Fact]
    public async Task ExceptionMessagesAndSignedUrlsAreNotReturned()
    {
        using var client = new HttpClient(new AsyncHandler((_, _) => throw new HttpRequestException("https://secret.invalid/?wsSecret=do-not-log")));
        var result = await RunAsync(client);
        Assert.Equal("TransportError", result.Category);
        Assert.Equal(nameof(HttpRequestException), result.ExceptionType);
        Assert.DoesNotContain("do-not-log", result.ToString());
        Assert.DoesNotContain("wsSecret", result.ToString());
    }

    private static HuyaLineProbe Probe(HttpClient client) => new(client, TimeSpan.FromSeconds(2), TimeSpan.Zero);

    private static async Task<HuyaLineProbeResult> RunAsync(HttpClient client)
    {
        var extractor = CreateExtractor();
        return await Probe(client).ProbeAsync(extractor, extractor.SelectedLine!, TestContext.Current.CancellationToken);
    }

    private static HuyaExtractor CreateExtractor()
    {
        var extractor = new HuyaExtractor(null);
        Assert.True(extractor.InstallCandidates(CandidateList("signature", "stream"), "OD"));
        return extractor;
    }

    private static JsonArray CandidateList(string signature, string streamName) => new(
        Candidate("AL", "https://al.hls.huya.com/live", streamName, signature),
        Candidate("HS", "https://hs.hls.huya.com/live", streamName, signature),
        Candidate("TX", "https://tx.hls.huya.com/live", streamName, signature),
        Candidate("HW", "https://hw.hls.huya.com/live", streamName, signature));

    private static JsonObject Candidate(string cdn, string baseUrl, string stream = "stream", string antiCode = "dGVzdF8w") => new()
    {
        ["sCdnType"] = cdn, ["sHlsUrl"] = baseUrl, ["sStreamName"] = stream,
        ["sHlsUrlSuffix"] = "m3u8", ["sHlsAntiCode"] = $"fm={antiCode}&ctype=test&fs=1"
    };

    private static string Playlist(int sequence) => $"#EXTM3U\n#EXT-X-TARGETDURATION:3\n#EXT-X-MEDIA-SEQUENCE:{sequence}\n#EXTINF:3,\nsegment{sequence}.ts\n";

    private static string Master(string firstVariant) => $"#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=6000000\n{firstVariant}\n";

    private static byte[] TransportStream()
    {
        var bytes = new byte[188 * 10];
        for (int offset = 0; offset < bytes.Length; offset += 188)
        {
            bytes[offset] = 0x47;
            bytes[offset + 3] = 0x10;
        }
        return bytes;
    }

    private static HttpResponseMessage Text(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> response) =>
        new(new AsyncHandler((request, _) => Task.FromResult(response(request))));

    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request, cancellationToken);
    }

    private sealed class RepeatingStream(int length) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = Math.Min(count, length - _position);
            for (int i = 0; i < read; i++)
                buffer[offset + i] = ((_position + i) % 188) switch { 0 => (byte)0x47, 3 => (byte)0x10, _ => (byte)0 };
            _position += read;
            return read;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
