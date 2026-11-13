using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;

namespace LiveStreamGateway;

public sealed record HuyaLineProbeResult(
    string LineKey,
    bool Success,
    string Category,
    double DownloadMediaRatio,
    int SampleCount,
    long DownloadedBytes,
    TimeSpan Elapsed,
    string? ExceptionType = null);

/// <summary>
/// Samples a candidate independently of the production HLS proxy and its playlist cache.
/// The supplied client must have automatic redirects disabled; no cookies are required.
/// A pass is a bounded transport/TS framing check, not a full audiovisual decode guarantee.
/// </summary>
public sealed class HuyaLineProbe
{
    internal const int MaxSegmentBytes = 8 * 1024 * 1024;
    internal const int MaxTotalBytes = 16 * 1024 * 1024;
    private const int MaxPlaylistBytes = 256 * 1024;
    private readonly HttpClient _client;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _pollDelay;

    public HuyaLineProbe(HttpClient client) : this(client, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(750)) { }

    internal HuyaLineProbe(HttpClient client, TimeSpan timeout, TimeSpan pollDelay)
    {
        _client = client;
        _timeout = timeout;
        _pollDelay = pollDelay;
    }

    public async Task<HuyaLineProbeResult> ProbeAsync(HuyaExtractor extractor, HuyaLineCandidate candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(candidate);
        var stopwatch = Stopwatch.StartNew();
        var budget = new ByteBudget();
        int samples = 0;
        double downloadSeconds = 0, mediaSeconds = 0;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        var token = deadline.Token;

        HuyaLineProbeResult Result(bool success, string category, string? exceptionType = null) =>
            new(candidate.Key, success, category, mediaSeconds > 0 ? downloadSeconds / mediaSeconds : double.PositiveInfinity,
                samples, budget.Downloaded, stopwatch.Elapsed, exceptionType);

        try
        {
            if (!Uri.TryCreate(extractor.GetFreshUrl(candidate), UriKind.Absolute, out var playlistUri) ||
                !IsTrustedOrigin(playlistUri)) throw new ProbeFailure("UnsafeUri");
            var first = await ReadPlaylistAsync(playlistUri, playlistUri, budget, token);
            var initialSegment = first.Segments[^1];
            var segmentWatch = Stopwatch.StartNew();
            await ReadSegmentAsync(initialSegment.Uri, playlistUri, budget, token);
            downloadSeconds += segmentWatch.Elapsed.TotalSeconds;
            mediaSeconds += initialSegment.Duration;
            samples++;

            Playlist? progressed = null;
            // Two independent media-list snapshots must show a new complete segment. A single
            // successful request to a stale CDN must never qualify that line for failover.
            // A 2-3 second Huya segment can be observed just after its publication.
            // Allow at least one full segment cycle plus margin, within the outer
            // ten-second deadline, rather than declaring a healthy edge stale at ~3s.
            int attempts = Math.Max(8, (int)Math.Ceiling(initialSegment.Duration /
                Math.Max(0.75, _pollDelay.TotalSeconds)) + 3);
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                if (attempt > 0) await Task.Delay(_pollDelay, token);
                if (!Uri.TryCreate(extractor.GetFreshUrl(candidate), UriKind.Absolute, out var refreshUri) ||
                    !IsSameOrigin(refreshUri, playlistUri)) throw new ProbeFailure("UnsafeUri");
                var next = await ReadPlaylistAsync(refreshUri, playlistUri, budget, token);
                var latest = next.Segments[^1];
                bool advances = first.HasSequence && next.HasSequence
                    ? latest.Sequence > initialSegment.Sequence
                    : latest.Uri.AbsolutePath != initialSegment.Uri.AbsolutePath;
                if (advances && latest.Uri.AbsolutePath != initialSegment.Uri.AbsolutePath)
                {
                    progressed = next;
                    break;
                }
            }
            if (progressed == null) throw new ProbeFailure("PlaylistNotAdvancing");
            var secondSegment = progressed.Segments[^1];
            segmentWatch.Restart();
            await ReadSegmentAsync(secondSegment.Uri, playlistUri, budget, token);
            downloadSeconds += segmentWatch.Elapsed.TotalSeconds;
            mediaSeconds += secondSegment.Duration;
            samples++;
            return downloadSeconds / mediaSeconds <= 0.8
                ? Result(true, "Healthy") : Result(false, "SlowDownload");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result(false, "Timeout", nameof(OperationCanceledException));
        }
        catch (OperationCanceledException) { throw; }
        catch (ProbeFailure ex) { return Result(false, ex.Category); }
        catch (Exception ex)
        {
            // Exception messages may contain signed URLs. Only expose the CLR type.
            return Result(false, "TransportError", ex.GetType().Name);
        }
    }

    private async Task<Playlist> ReadPlaylistAsync(Uri uri, Uri origin, ByteBudget budget, CancellationToken token,
        int depth = 0, HashSet<string>? visitedPaths = null)
    {
        visitedPaths ??= new HashSet<string>(StringComparer.Ordinal);
        if (!visitedPaths.Add(uri.GetLeftPart(UriPartial.Path))) throw new ProbeFailure("PlaylistLoop");
        // Match the production proxy's first-variant behavior, never rank variants or
        // silently lower quality. Only one master -> media hop is supported.
        if (depth > 1) throw new ProbeFailure("MasterDepthExceeded");
        var bytes = await ReadBytesAsync(uri, origin, MaxPlaylistBytes, budget, token, isPlaylist: true);
        var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF', '\r', '\n', ' ', '\t');
        if (!text.StartsWith("#EXTM3U", StringComparison.Ordinal)) throw new ProbeFailure("InvalidPlaylist");
        var lines = text.Split('\n').Select(line => line.Trim()).ToArray();
        int firstVariantIndex = Array.FindIndex(lines,
            line => line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal));
        if (firstVariantIndex >= 0)
        {
            string? firstVariant = lines.Skip(firstVariantIndex + 1)
                .FirstOrDefault(line => line.Length > 0 && !line.StartsWith('#'));
            if (firstVariant == null) throw new ProbeFailure("InvalidPlaylist");
            if (!Uri.TryCreate(uri, firstVariant, out var childUri) || !IsSameOrigin(childUri, origin))
                throw new ProbeFailure("UnsafePlaylistUri");
            return await ReadPlaylistAsync(childUri, origin, budget, token, depth + 1, visitedPaths);
        }
        var segments = new List<Segment>();
        long sequence = 0;
        bool hasSequence = false;
        double? duration = null;
        foreach (var line in lines)
        {
            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
            {
                if (!long.TryParse(line[22..], NumberStyles.None, CultureInfo.InvariantCulture, out sequence) || sequence < 0)
                    throw new ProbeFailure("InvalidPlaylist");
                hasSequence = true;
            }
            else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                if (!double.TryParse(line[8..].Split(',')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
                    !double.IsFinite(seconds) || seconds <= 0 || seconds > 30)
                    throw new ProbeFailure("InvalidPlaylist");
                duration = seconds;
            }
            else if (line.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal) ||
                line.StartsWith("#EXT-X-I-FRAME-STREAM-INF", StringComparison.Ordinal) ||
                line.StartsWith("#EXT-X-MAP", StringComparison.Ordinal) ||
                line.StartsWith("#EXT-X-BYTERANGE", StringComparison.Ordinal) ||
                line.StartsWith("#EXT-X-ENDLIST", StringComparison.Ordinal) ||
                (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal) && line != "#EXT-X-KEY:METHOD=NONE"))
            {
                // No decryption, byte-range, fMP4, or VOD approximation.
                throw new ProbeFailure("UnsupportedPlaylist");
            }
            else if (line.Length > 0 && !line.StartsWith('#'))
            {
                if (duration == null || !Uri.TryCreate(uri, line, out var segmentUri) ||
                    !IsSameOrigin(segmentUri, origin) ||
                    !segmentUri.AbsolutePath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
                    throw new ProbeFailure("UnsafeSegmentUri");
                if (sequence == long.MaxValue) throw new ProbeFailure("InvalidPlaylist");
                segments.Add(new Segment(segmentUri, duration.Value, sequence++));
                duration = null;
            }
        }
        if (segments.Count == 0) throw new ProbeFailure("InvalidPlaylist");
        return new Playlist(segments, hasSequence);
    }

    private async Task ReadSegmentAsync(Uri uri, Uri origin, ByteBudget budget, CancellationToken token)
    {
        var bytes = await ReadBytesAsync(uri, origin, MaxSegmentBytes, budget, token, isPlaylist: false);
        if (bytes.Length < 188 * 3 || bytes.Length % 188 != 0) throw new ProbeFailure("InvalidTransportStream");
        for (int offset = 0; offset < bytes.Length; offset += 188)
        {
            if (bytes[offset] != 0x47 || (bytes[offset + 3] & 0x30) == 0)
                throw new ProbeFailure("InvalidTransportStream");
        }
    }

    private async Task<byte[]> ReadBytesAsync(Uri uri, Uri origin, int limit, ByteBudget budget,
        CancellationToken token, bool isPlaylist)
    {
        if (!IsSameOrigin(uri, origin)) throw new ProbeFailure("UnsafeUri");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation("Referer", "https://www.huya.com");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        if (isPlaylist)
        {
            request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache, no-store");
            request.Headers.TryAddWithoutValidation("Pragma", "no-cache");
        }
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode != HttpStatusCode.OK) throw new ProbeFailure("HttpStatus");
        if (response.RequestMessage?.RequestUri is Uri effectiveUri && !IsSameOrigin(effectiveUri, origin))
            throw new ProbeFailure("UnsafeRedirect");
        if (response.Content.Headers.ContentEncoding.Any(encoding => !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase)))
            throw new ProbeFailure("UnsupportedContentEncoding");
        long? expected = response.Content.Headers.ContentLength;
        if (expected is > 0 && expected > limit) throw new ProbeFailure("ResponseTooLarge");
        if (expected is > 0 && expected > MaxTotalBytes - budget.Downloaded) throw new ProbeFailure("BudgetExceeded");
        using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16 * 1024];
        while (true)
        {
            int remaining = (int)Math.Min(limit - buffer.Length, MaxTotalBytes - budget.Downloaded);
            int count = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, remaining + 1)), token);
            if (count == 0) break;
            budget.Downloaded += count;
            if (budget.Downloaded > MaxTotalBytes) throw new ProbeFailure("BudgetExceeded");
            if (buffer.Length + count > limit) throw new ProbeFailure("ResponseTooLarge");
            buffer.Write(chunk, 0, count);
        }
        if (expected.HasValue && buffer.Length != expected.Value) throw new ProbeFailure("LengthMismatch");
        return buffer.ToArray();
    }

    private static bool IsTrustedOrigin(Uri uri)
    {
        if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
            IPAddress.TryParse(uri.IdnHost, out _)) return false;
        var host = uri.IdnHost;
        return host.EndsWith(".huya.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".huyacdn.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".huya.net", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameOrigin(Uri uri, Uri origin) => IsTrustedOrigin(uri) &&
        uri.Scheme == origin.Scheme && uri.IdnHost.Equals(origin.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        uri.Port == origin.Port;

    private sealed class ByteBudget { public long Downloaded; }
    private sealed record Segment(Uri Uri, double Duration, long Sequence);
    private sealed record Playlist(List<Segment> Segments, bool HasSequence);
    private sealed class ProbeFailure(string category) : Exception
    {
        internal string Category { get; } = category;
    }
}
