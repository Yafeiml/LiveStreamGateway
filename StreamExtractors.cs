using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;

namespace LiveStreamGateway
{
    public abstract class BaseExtractor
    {
        protected readonly HttpClient _httpClient;
        protected readonly string _cookies;

        protected BaseExtractor(string? cookies)
        {
            _cookies = SanitizeAsciiHeader(cookies);
            var handler = new HttpClientHandler
            {
                UseCookies = false
            };
            _httpClient = new HttpClient(handler);
            _httpClient.Timeout = TimeSpan.FromSeconds(10);
        }

        public static string SanitizeAsciiHeader(string? val)
        {
            if (string.IsNullOrEmpty(val)) return "";
            var sb = new StringBuilder(val.Length);
            foreach (char c in val)
            {
                // Only allow standard printable ASCII characters (0x20 to 0x7E) for HTTP headers
                if (c >= 0x20 && c <= 0x7E)
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        public abstract Task<string?> GetStreamUrlAsync(string url, string quality);

        protected long GetTimestampUnix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        protected long GetTimestampUnixMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        protected string GetMd5(string input)
        {
            using var md5 = MD5.Create();
            var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
            var sb = new StringBuilder();
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
        
        protected string Base64Decode(string base64)
        {
            try {
                int mod4 = base64.Length % 4;
                if (mod4 > 0) base64 += new string('=', 4 - mod4);
                var bytes = Convert.FromBase64String(base64);
                return Encoding.UTF8.GetString(bytes);
            } catch {
                return "";
            }
        }
    }

    public class BilibiliExtractor : BaseExtractor
    {
        public BilibiliExtractor(string? cookies) : base(cookies) { }

        public override async Task<string?> GetStreamUrlAsync(string url, string quality)
        {
            try
            {
                string rawId = url.Split('?')[0].TrimEnd('/').Split('/').Last();
                if (string.IsNullOrEmpty(rawId)) return null;

                // 1. 获取真实房间号与开播状态
                string realRoomId = rawId;
                try
                {
                    var initReq = new HttpRequestMessage(HttpMethod.Get, $"https://api.live.bilibili.com/room/v1/Room/room_init?id={rawId}");
                    initReq.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/131.0.0.0 Safari/537.36");
                    if (!string.IsNullOrEmpty(_cookies)) initReq.Headers.TryAddWithoutValidation("Cookie", _cookies);
                    
                    var initRes = await _httpClient.SendAsync(initReq);
                    if (initRes.IsSuccessStatusCode)
                    {
                        var initJson = JsonNode.Parse(await initRes.Content.ReadAsStringAsync());
                        if (initJson?["code"]?.GetValue<int>() == 0)
                        {
                            int liveStatus = initJson["data"]?["live_status"]?.GetValue<int>() ?? 0;
                            if (liveStatus == 0)
                            {
                                throw new Exception("Not Live (未开播)");
                            }
                            realRoomId = initJson["data"]?["room_id"]?.ToString() ?? rawId;
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (ex.Message.Contains("Not Live")) throw;
                }

                // 2. 映射画质 QN (OD/BD 均请求 10000 原画 1080P60)
                string qn = quality switch
                {
                    "OD" => "10000",
                    "BD" => "10000",
                    "UHD" => "400",
                    "HD" => "400",
                    "SD" => "250",
                    "LD" => "150",
                    _ => "10000"
                };

                // 3. 请求现代 V2 播放流接口
                var v2Req = new HttpRequestMessage(HttpMethod.Get, $"https://api.live.bilibili.com/xlive/web-room/v2/index/getRoomPlayInfo?room_id={realRoomId}&protocol=0,1&format=0,1,2&codec=0,1&qn={qn}&platform=web&ptype=8&dolby=5&panorama=1&hdr_type=0,1");
                v2Req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/131.0.0.0 Safari/537.36");
                v2Req.Headers.TryAddWithoutValidation("Referer", $"https://live.bilibili.com/{realRoomId}");
                if (!string.IsNullOrEmpty(_cookies)) v2Req.Headers.TryAddWithoutValidation("Cookie", _cookies);

                var v2Resp = await _httpClient.SendAsync(v2Req);
                var v2JsonStr = await v2Resp.Content.ReadAsStringAsync();
                var v2Json = JsonNode.Parse(v2JsonStr);

                if (v2Json?["data"]?["live_status"]?.GetValue<int>() == 0)
                    throw new Exception("Not Live (未开播)");

                int code = v2Json?["code"]?.GetValue<int>() ?? 0;
                if (code == -101 || code == -400)
                    throw new Exception("Cookie Invalid (Cookie失效或需登录)");

                var streamArray = v2Json?["data"]?["playurl_info"]?["playurl"]?["stream"]?.AsArray();
                if (streamArray != null && streamArray.Count > 0)
                {
                    // 优先选择 http_stream (FLV) 或 http_hls
                    var selectedStream = streamArray.FirstOrDefault(s => s?["protocol_name"]?.GetValue<string>() == "http_stream") 
                                         ?? streamArray[0];

                    var formatArray = selectedStream?["format"]?.AsArray();
                    if (formatArray != null && formatArray.Count > 0)
                    {
                        var selectedFormat = formatArray.FirstOrDefault(f => f?["format_name"]?.GetValue<string>() == "flv") 
                                             ?? formatArray[0];

                        var codecArray = selectedFormat?["codec"]?.AsArray();
                        if (codecArray != null && codecArray.Count > 0)
                        {
                            // 优先 AVC (h264) 格式，兼容性最好且码率满速
                            var selectedCodec = codecArray.FirstOrDefault(c => c?["codec_name"]?.GetValue<string>() == "avc") 
                                                ?? codecArray[0];

                            string baseUrl = selectedCodec?["base_url"]?.GetValue<string>() ?? "";
                            var urlInfoArray = selectedCodec?["url_info"]?.AsArray();
                            if (urlInfoArray != null && urlInfoArray.Count > 0)
                            {
                                // 优先选 gotcha 节点
                                var selectedUrlInfo = urlInfoArray.FirstOrDefault(u => u?["host"]?.GetValue<string>()?.Contains("gotcha") == true)
                                                      ?? urlInfoArray[0];

                                string host = selectedUrlInfo?["host"]?.GetValue<string>() ?? "";
                                string extra = selectedUrlInfo?["extra"]?.GetValue<string>() ?? "";
                                return host + baseUrl + extra;
                            }
                        }
                    }
                }

                throw new Exception("Not Live (未开播)");
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("Not Live") || ex.Message.Contains("Cookie Invalid")) throw;
                throw new Exception($"Bilibili Error: {ex.Message}");
            }
        }
    }

    internal static class DouyuRoomResolver
    {
        private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/131.0.0.0 Safari/537.36";

        internal static async Task<string> ResolveCanonicalRoomIdAsync(
            HttpClient httpClient,
            string url,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(httpClient);

            string input = (url ?? "").Trim();
            if (string.IsNullOrEmpty(input))
                throw new InvalidOperationException("斗鱼直播间地址不能为空");

            // 带 rid 查询参数的地址已经明确给出了斗鱼内部房间 ID。
            var explicitRid = Regex.Match(input, @"(?:[?&]|&amp;)rid=(\d+)", RegexOptions.IgnoreCase);
            if (explicitRid.Success)
                return explicitRid.Groups[1].Value;

            string? pagePath = ExtractRoomPath(input);
            string? fallbackRoomId = pagePath != null && Regex.IsMatch(pagePath, @"^\d+$")
                ? pagePath
                : null;

            if (string.IsNullOrEmpty(pagePath))
                throw new InvalidOperationException("无法从斗鱼地址中识别房间号");

            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"https://m.douyu.com/{Uri.EscapeDataString(pagePath)}");
                request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

                using var response = await httpClient.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    string html = await response.Content.ReadAsStringAsync(cancellationToken);
                    string? canonicalRoomId = ExtractCanonicalRoomId(html);
                    if (!string.IsNullOrEmpty(canonicalRoomId))
                        return canonicalRoomId;
                }
                else if (string.IsNullOrEmpty(fallbackRoomId))
                {
                    throw new HttpRequestException($"斗鱼房间页面请求失败 (HTTP {(int)response.StatusCode})");
                }
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested && !string.IsNullOrEmpty(fallbackRoomId))
            {
                // 页面解析超时时，真实数字房间号仍可直接交给播放接口，避免网络抖动造成回归。
            }
            catch (HttpRequestException) when (!string.IsNullOrEmpty(fallbackRoomId))
            {
                // 同上：只对可安全回退的纯数字房间号降级。
            }

            if (!string.IsNullOrEmpty(fallbackRoomId))
                return fallbackRoomId;

            throw new InvalidOperationException($"无法解析斗鱼房间 {pagePath} 的真实 rid");
        }

        internal static string? ExtractCanonicalRoomId(string? html)
        {
            if (string.IsNullOrWhiteSpace(html)) return null;

            // 同时兼容页面中的 `"rid":6979222`、`"rid":"6979222"`
            // 以及嵌入字符串后带反斜杠转义的 `\"rid\":6979222`。
            var match = Regex.Match(
                html,
                @"\\?""rid\\?""\s*:\s*\\?""?(\d+)",
                RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : null;
        }

        private static string? ExtractRoomPath(string input)
        {
            if (Regex.IsMatch(input, @"^\d+$"))
                return input;

            if (Uri.TryCreate(input, UriKind.Absolute, out var uri) &&
                (uri.Host.Equals("douyu.com", StringComparison.OrdinalIgnoreCase) ||
                 uri.Host.EndsWith(".douyu.com", StringComparison.OrdinalIgnoreCase)))
            {
                string path = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                return string.IsNullOrEmpty(path) ? null : Uri.UnescapeDataString(path);
            }

            var match = Regex.Match(
                input,
                @"(?:https?://)?(?:www\.|m\.)?douyu\.com/([^/?#]+)",
                RegexOptions.IgnoreCase);
            return match.Success ? Uri.UnescapeDataString(match.Groups[1].Value) : null;
        }
    }

    public class DouyuExtractor : BaseExtractor
    {
        private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/131.0.0.0 Safari/537.36";

        public DouyuExtractor(string? cookies) : base(cookies) { }

        public override async Task<string?> GetStreamUrlAsync(string url, string quality)
        {
            try
            {
                // 斗鱼数字路径既可能是真实 rid，也可能是数字靓号。始终先读取移动页，
                // 将 6657 这类展示号转换为真实 rid 后再参与签名和取流。
                string roomId = await DouyuRoomResolver.ResolveCanonicalRoomIdAsync(_httpClient, url);

                string rate = quality switch
                {
                    "OD" => "0",
                    "BD" => "0",
                    "UHD" => "3",
                    "HD" => "2",
                    "SD" => "1",
                    "LD" => "1",
                    _ => "0"
                };

                // 1. 获取动态加密密钥 (带 Cookie 鉴权)
                var encReq = new HttpRequestMessage(HttpMethod.Get, $"https://www.douyu.com/wgapi/livenc/liveweb/websec/getEncryption?did=10000000000000000000000000001501");
                encReq.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                encReq.Headers.TryAddWithoutValidation("Referer", $"https://www.douyu.com/{roomId}");
                if (!string.IsNullOrEmpty(_cookies)) encReq.Headers.TryAddWithoutValidation("Cookie", _cookies);
                
                using var encRes = await _httpClient.SendAsync(encReq);
                string encBody = await encRes.Content.ReadAsStringAsync();
                if (!encRes.IsSuccessStatusCode)
                    throw new Exception($"加密参数接口请求失败 (HTTP {(int)encRes.StatusCode})");

                var encJson = JsonNode.Parse(encBody);
                int encError = TryReadInt(encJson?["error"], out int parsedEncError) ? parsedEncError : -1;
                if (encError != 0)
                {
                    string encMessage = NormalizeUpstreamMessage(encJson?["msg"]?.ToString());
                    throw new Exception($"加密参数接口返回异常 (error={encError}, msg={encMessage})");
                }

                var white = encJson?["data"]
                    ?? throw new Exception("加密参数接口响应缺少 data 字段");
                long ts = GetTimestampUnix();
                string secret = white?["rand_str"]?.GetValue<string>() ?? "";
                
                bool isSpecial = false;
                var specialNode = white?["is_special"];
                if (specialNode != null)
                {
                    if (specialNode.GetValueKind() == System.Text.Json.JsonValueKind.True) isSpecial = true;
                    else if (specialNode.GetValueKind() == System.Text.Json.JsonValueKind.False) isSpecial = false;
                    else if (specialNode.GetValueKind() == System.Text.Json.JsonValueKind.Number && specialNode.AsValue().TryGetValue<int>(out int numVal)) isSpecial = numVal != 0;
                    else if (specialNode.ToString() == "1" || specialNode.ToString().Equals("true", StringComparison.OrdinalIgnoreCase)) isSpecial = true;
                }
                string salt = !isSpecial ? $"{roomId}{ts}" : "";

                int encTime = white?["enc_time"]?.GetValue<int>() ?? 0;
                string key = white?["key"]?.GetValue<string>() ?? "";
                
                for (int i = 0; i < encTime; i++)
                {
                    secret = GetMd5(secret + key);
                }
                string auth = GetMd5(secret + key + salt);

                var paramDict = new Dictionary<string, string>
                {
                    { "rate", rate },
                    { "ver", "219032101" },
                    { "iar", "0" },
                    { "ive", "0" },
                    { "rid", roomId },
                    { "hevc", "0" },
                    { "fa", "0" },
                    { "sov", "0" },
                    { "enc_data", white?["enc_data"]?.GetValue<string>() ?? "" },
                    { "tt", ts.ToString() },
                    { "did", "10000000000000000000000000001501" },
                    { "auth", auth }
                };

                // 2. 请求播放地址 (带全量 Cookie，解锁原画2K60/4K与最高码率)
                var playReq = new HttpRequestMessage(HttpMethod.Post, $"https://playweb.douyucdn.cn/lapi/live/getH5PlayV1/{roomId}");
                playReq.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                playReq.Headers.TryAddWithoutValidation("Origin", "https://www.douyu.com");
                playReq.Headers.TryAddWithoutValidation("Referer", $"https://www.douyu.com/{roomId}");
                if (!string.IsNullOrEmpty(_cookies)) playReq.Headers.TryAddWithoutValidation("Cookie", _cookies);
                playReq.Content = new FormUrlEncodedContent(paramDict);

                using var playRes = await _httpClient.SendAsync(playReq);
                string playBody = await playRes.Content.ReadAsStringAsync();
                if (!playRes.IsSuccessStatusCode)
                    throw new Exception($"播放接口请求失败 (rid={roomId}, HTTP {(int)playRes.StatusCode})");

                var playJson = JsonNode.Parse(playBody);
                return ExtractStreamUrlOrThrow(playJson, roomId);
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("Not Live") || ex.Message.Contains("Cookie Invalid")) throw;
                throw new Exception($"Douyu Error: {ex.Message}");
            }
        }

        internal static string ExtractStreamUrlOrThrow(JsonNode? playJson, string roomId)
        {
            if (!TryReadInt(playJson?["error"], out int errCode))
                throw new Exception($"播放接口响应缺少有效 error 字段 (rid={roomId})");

            string upstreamMessage = NormalizeUpstreamMessage(playJson?["msg"]?.ToString());
            if (errCode == 0)
            {
                var data = playJson?["data"];
                string rtmpUrl = data?["rtmp_url"]?.GetValue<string>() ?? "";
                string rtmpLive = data?["rtmp_live"]?.GetValue<string>() ?? "";
                if (!string.IsNullOrEmpty(rtmpUrl) && !string.IsNullOrEmpty(rtmpLive))
                    return $"{rtmpUrl.TrimEnd('/')}/{rtmpLive.TrimStart('/')}";

                throw new Exception($"播放接口未返回有效直播流地址 (rid={roomId}, error=0)");
            }

            // 斗鱼会对离线房间返回 error=-5、msg=房间未开播。消息语义优先，
            // 避免把有效 Cookie 下的正常离线状态误报为 Cookie 失效。
            if (errCode == 102 || errCode == 104 || IsOfflineMessage(upstreamMessage))
                throw new Exception($"Not Live (未开播; rid={roomId}, error={errCode}, msg={upstreamMessage})");

            // 只有上游明确表达登录或凭据失效时才归类为鉴权错误。
            // error=51 的具体语义并不稳定，不能仅凭错误码猜测 Cookie 已失效。
            if (IsAuthenticationMessage(upstreamMessage))
                throw new Exception($"Cookie Invalid (Cookie失效或需登录; rid={roomId}, error={errCode}, msg={upstreamMessage})");

            // 当前播放接口在消息缺失时也可能仅用 -5 表示离线，保留兼容兜底。
            if (errCode == -5)
                throw new Exception($"Not Live (未开播; rid={roomId}, error={errCode}, msg={upstreamMessage})");

            // 未知错误必须保留真实错误码，不能再伪装成“未开播”。
            throw new Exception($"播放接口返回异常 (rid={roomId}, error={errCode}, msg={upstreamMessage})");
        }

        private static bool TryReadInt(JsonNode? node, out int value)
        {
            value = 0;
            if (node is not JsonValue jsonValue) return false;
            if (jsonValue.TryGetValue<int>(out value)) return true;
            return int.TryParse(node.ToString(), out value);
        }

        private static string NormalizeUpstreamMessage(string? message)
        {
            string clean = Regex.Replace(message ?? "", @"\s+", " ").Trim();
            if (string.IsNullOrEmpty(clean)) return "unknown";
            return clean.Length <= 120 ? clean : clean[..120];
        }

        private static bool IsOfflineMessage(string message) =>
            message.Contains("未开播", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("已下播", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("直播已结束", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("直播结束", StringComparison.OrdinalIgnoreCase);

        private static bool IsAuthenticationMessage(string message)
        {
            if (message.Contains("Cookie", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("未登录", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("未登陆", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("请登录", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("请登陆", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("登录失效", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("登陆失效", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("需要登录", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("需要登陆", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            bool mentionsToken = message.Contains("token", StringComparison.OrdinalIgnoreCase);
            bool tokenInvalid = message.Contains("过期", StringComparison.OrdinalIgnoreCase) ||
                                message.Contains("失效", StringComparison.OrdinalIgnoreCase) ||
                                message.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
                                message.Contains("expired", StringComparison.OrdinalIgnoreCase);
            return mentionsToken && tokenInvalid;
        }
    }

    public class HuyaExtractor : BaseExtractor
    {
        private string? _sHlsUrl;
        private string? _sStreamName;
        private string? _sHlsUrlSuffix;
        private string? _sHlsAntiCode;
        private string _ratioParam = "";

        private readonly long _uid;
        private readonly long _initUuid;
        private readonly long _sdkSid;

        public HuyaExtractor(string? cookies) : base(cookies) 
        { 
            long t13 = GetTimestampUnixMs();
            _sdkSid = t13;
            _initUuid = (long)((t13 % 10000000000L * 1000) + (1000 * new Random().NextDouble())) % 4294967295L;
            _uid = new Random().NextInt64(1400000000000L, 1400010000000L);
        }

        public string GetFreshUrl()
        {
            if (string.IsNullOrEmpty(_sHlsUrl) || string.IsNullOrEmpty(_sStreamName))
                return "";
            string newAntiCode = GetAntiCode(_sHlsAntiCode!, _sStreamName!);
            return $"{_sHlsUrl}/{_sStreamName}.{_sHlsUrlSuffix}?{newAntiCode}&ratio={_ratioParam}";
        }

        private Dictionary<string, string> ParseQueryString(string query)
        {
            var dict = new Dictionary<string, string>();
            var parts = query.TrimStart('?').Split('&');
            foreach (var p in parts)
            {
                var kv = p.Split('=');
                if (kv.Length == 2) dict[kv[0]] = Uri.UnescapeDataString(kv[1]);
                else if (kv.Length == 1) dict[kv[0]] = "";
            }
            return dict;
        }

        private string GetAntiCode(string oldAntiCode, string streamName)
        {
            var query = ParseQueryString(oldAntiCode);
            
            long t13 = GetTimestampUnixMs();
            long seqId = _uid + _sdkSid;
            long targetUnixTime = (t13 + 110624) / 1000;
            string wsTime = targetUnixTime.ToString("x").ToLower();
            
            string fm = query.ContainsKey("fm") ? query["fm"] : "";
            string decodedFm = Base64Decode(fm);
            string wsSecretPf = decodedFm.Split('_')[0];
            
            string ctype = query.ContainsKey("ctype") ? query["ctype"] : "";
            string fs = query.ContainsKey("fs") ? query["fs"] : "";
            
            string wsSecretHash = GetMd5($"{seqId}|{ctype}|100");
            string wsSecret = $"{wsSecretPf}_{_uid}_{streamName}_{wsSecretHash}_{wsTime}";
            string wsSecretMd5 = GetMd5(wsSecret);
            
            return $"wsSecret={wsSecretMd5}&wsTime={wsTime}&seqid={seqId}&ctype={ctype}&ver=1&fs={fs}&uuid={_initUuid}&u={_uid}&t=100&sv=2403051612&sdk_sid={_sdkSid}&codec=264";
        }

        public override async Task<string?> GetStreamUrlAsync(string url, string quality)
        {
            try
            {
                // 1. 码率参数映射
                _ratioParam = quality switch
                {
                    "OD" => "",
                    "BD" => "",
                    "UHD" => "4000",
                    "HD" => "2000",
                    "SD" => "1000",
                    _ => ""
                };

                // 2. 解析房间号 (支持别名转换)
                string roomId = url.Split('?')[0].TrimEnd('/').Split('/').Last();
                if (roomId.Any(char.IsLetter))
                {
                    var mReq = new HttpRequestMessage(HttpMethod.Get, url.StartsWith("http") ? url : $"https://m.huya.com/{roomId}");
                    mReq.Headers.TryAddWithoutValidation("User-Agent", "ios/7.830 (ios 17.0; ; iPhone 15)");
                    if (!string.IsNullOrEmpty(_cookies)) mReq.Headers.TryAddWithoutValidation("Cookie", _cookies);
                    
                    var mRes = await _httpClient.SendAsync(mReq);
                    var html = await mRes.Content.ReadAsStringAsync();
                    var match = Regex.Match(html, @"ProfileRoom"":(.*?),""sPrivateHost");
                    if (match.Success) roomId = match.Groups[1].Value;
                }

                // 3. 请求虎牙移动端 Profile 缓存接口 (带 Cookie)
                var req = new HttpRequestMessage(HttpMethod.Get, $"https://mp.huya.com/cache.php?m=Live&do=profileRoom&roomid={roomId}&showSecret=1");
                req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/131.0.0.0");
                req.Headers.TryAddWithoutValidation("Referer", $"https://www.huya.com/{roomId}");
                if (!string.IsNullOrEmpty(_cookies)) req.Headers.TryAddWithoutValidation("Cookie", _cookies);
                
                var res = await _httpClient.SendAsync(req);
                var json = JsonNode.Parse(await res.Content.ReadAsStringAsync());

                string status = json?["data"]?["realLiveStatus"]?.GetValue<string>() ?? "";
                if (status != "ON") throw new Exception("Not Live (未开播)");

                string liveType = json?["data"]?["liveData"]?["gameHostName"]?.GetValue<string>() ?? "";
                if (liveType == "lol")
                {
                    // LOL 分区优先走 PC 页面流解析
                    var pcReq = new HttpRequestMessage(HttpMethod.Get, $"https://www.huya.com/{roomId}");
                    pcReq.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/131.0.0.0");
                    if (!string.IsNullOrEmpty(_cookies)) pcReq.Headers.TryAddWithoutValidation("Cookie", _cookies);
                    
                    var pcRes = await _httpClient.SendAsync(pcReq);
                    var pcHtml = await pcRes.Content.ReadAsStringAsync();
                    var pcMatch = Regex.Match(pcHtml, @"stream: (\{""data"".*?),""iWebDefaultBitRate""");
                    if (pcMatch.Success)
                    {
                        var pcJsonStr = pcMatch.Groups[1].Value + "}";
                        var pcJson = JsonNode.Parse(pcJsonStr);
                        var streamInfo = pcJson?["data"]?[0]?["gameStreamInfoList"]?[0];
                        if (streamInfo != null)
                        {
                            _sStreamName = streamInfo["sStreamName"]?.GetValue<string>() ?? "";
                            _sHlsUrl = streamInfo["sHlsUrl"]?.GetValue<string>() ?? "";
                            _sHlsUrlSuffix = streamInfo["sHlsUrlSuffix"]?.GetValue<string>() ?? "m3u8";
                            _sHlsAntiCode = streamInfo["sHlsAntiCode"]?.GetValue<string>() ?? "";
                            string newAntiCode = GetAntiCode(_sHlsAntiCode, _sStreamName);
                            return $"{_sHlsUrl}/{_sStreamName}.{_sHlsUrlSuffix}?{newAntiCode}&ratio={_ratioParam}";
                        }
                    }
                }

                // 4. 多 CDN 优先级优选 (TX > HW > AL > HS > 首选)
                var baseSteamInfoList = json?["data"]?["stream"]?["baseSteamInfoList"]?.AsArray();
                if (baseSteamInfoList != null && baseSteamInfoList.Count > 0)
                {
                    var selectedItem = baseSteamInfoList.FirstOrDefault(x => x?["sCdnType"]?.GetValue<string>() == "TX")
                                       ?? baseSteamInfoList.FirstOrDefault(x => x?["sCdnType"]?.GetValue<string>() == "HW")
                                       ?? baseSteamInfoList.FirstOrDefault(x => x?["sCdnType"]?.GetValue<string>() == "AL")
                                       ?? baseSteamInfoList[0];

                    if (selectedItem != null)
                    {
                        _sStreamName = selectedItem["sStreamName"]?.GetValue<string>() ?? "";
                        _sHlsUrl = selectedItem["sHlsUrl"]?.GetValue<string>() ?? "";
                        _sHlsUrlSuffix = selectedItem["sHlsUrlSuffix"]?.GetValue<string>() ?? "m3u8";
                        _sHlsAntiCode = selectedItem["sHlsAntiCode"]?.GetValue<string>() ?? "";
                        string newAntiCode = GetAntiCode(_sHlsAntiCode, _sStreamName);
                    string m3u8Url = $"{_sHlsUrl}/{_sStreamName}.{_sHlsUrlSuffix}?{newAntiCode}&ratio={_ratioParam}";
                        return m3u8Url;
                    }
                }
                
                throw new Exception("Cookie Invalid (Cookie失效或需登录)");
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("Not Live") || ex.Message.Contains("Cookie Invalid")) throw;
                throw new Exception($"Huya Error: {ex.Message}");
            }
        }
    }

    public class PlatformCookieStatus
    {
        public string Platform { get; set; } = "";
        public bool Configured { get; set; }
        public bool IsValid { get; set; }
        public bool IsNetworkError { get; set; }
        public string Username { get; set; } = "";
        public string Message { get; set; } = "";
        public DateTime? LastChecked { get; set; }
    }

    public static class CookieVerifier
    {
        private static HttpClient CreateHttpClient(int timeoutSeconds = 8)
        {
            var handler = new SocketsHttpHandler
            {
                UseCookies = false,
                AllowAutoRedirect = true,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
                }
            };

            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(timeoutSeconds),
                DefaultRequestVersion = HttpVersion.Version11,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
            return client;
        }

        public static async Task<PlatformCookieStatus> VerifyAsync(string platform, string? cookies)
        {
            return (platform?.ToLower() ?? "") switch
            {
                "huya" => await VerifyWithRetryAsync(() => VerifyHuyaOnceAsync(cookies)),
                "douyu" => await VerifyWithRetryAsync(() => VerifyDouyuOnceAsync(cookies)),
                "bilibili" => await VerifyWithRetryAsync(() => VerifyBilibiliOnceAsync(cookies)),
                _ => new PlatformCookieStatus { Platform = platform ?? "", Configured = false, IsValid = false, Message = "未知平台" }
            };
        }

        private static async Task<PlatformCookieStatus> VerifyWithRetryAsync(Func<Task<PlatformCookieStatus>> verifyFunc, int maxRetries = 3)
        {
            PlatformCookieStatus lastStatus = new();
            for (int i = 1; i <= maxRetries; i++)
            {
                lastStatus = await verifyFunc();

                // 若未配置、验证成功、或明确检测到凭据失效（非网络通信错误），无需重试，直接返回
                if (!lastStatus.Configured || lastStatus.IsValid || !lastStatus.IsNetworkError)
                {
                    return lastStatus;
                }

                // 若为网络波动错误，等待短暂间隔进行下一次复判
                if (i < maxRetries)
                {
                    await Task.Delay(1000 * i);
                }
            }

            return lastStatus;
        }

        public static async Task<PlatformCookieStatus> VerifyBilibiliOnceAsync(string? cookies)
        {
            string clean = BaseExtractor.SanitizeAsciiHeader(cookies);
            var status = new PlatformCookieStatus
            {
                Platform = "bilibili",
                Configured = !string.IsNullOrWhiteSpace(clean),
                LastChecked = DateTime.Now
            };

            if (!status.Configured)
            {
                status.IsValid = false;
                status.Message = "未配置";
                return status;
            }

            try
            {
                using var client = CreateHttpClient(timeoutSeconds: 8);
                var req = new HttpRequestMessage(HttpMethod.Get, "https://api.bilibili.com/x/web-interface/nav")
                {
                    Version = HttpVersion.Version11
                };
                req.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
                req.Headers.TryAddWithoutValidation("Cookie", clean);

                var res = await client.SendAsync(req);
                if ((int)res.StatusCode >= 500 || (int)res.StatusCode == 429)
                {
                    status.IsNetworkError = true;
                    status.Message = $"服务端暂时不可达 (HTTP {(int)res.StatusCode})";
                    return status;
                }

                if (!res.IsSuccessStatusCode)
                {
                    status.IsValid = false;
                    status.IsNetworkError = false;
                    status.Message = $"认证响应异常 (HTTP {(int)res.StatusCode})";
                    return status;
                }

                var json = JsonNode.Parse(await res.Content.ReadAsStringAsync());
                int code = json?["code"]?.GetValue<int>() ?? -1;
                bool isLogin = json?["data"]?["isLogin"]?.GetValue<bool>() ?? false;

                if (code == 0 && isLogin)
                {
                    status.IsValid = true;
                    status.IsNetworkError = false;
                    status.Username = json?["data"]?["uname"]?.GetValue<string>() ?? "";
                    int vipStatus = json?["data"]?["vipStatus"]?.GetValue<int>() ?? 0;
                    status.Message = vipStatus == 1 ? "大会员已授权" : "已授权有效";
                }
                else
                {
                    status.IsValid = false;
                    status.IsNetworkError = false;
                    status.Message = "Cookie已失效或未登录";
                }
            }
            catch (Exception ex)
            {
                status.IsNetworkError = true;
                status.Message = $"网络检测异常: {ex.Message}";
            }

            return status;
        }

        public static async Task<PlatformCookieStatus> VerifyDouyuOnceAsync(string? cookies)
        {
            string clean = BaseExtractor.SanitizeAsciiHeader(cookies);
            var status = new PlatformCookieStatus
            {
                Platform = "douyu",
                Configured = !string.IsNullOrWhiteSpace(clean),
                LastChecked = DateTime.Now
            };

            if (!status.Configured)
            {
                status.IsValid = false;
                status.Message = "未配置";
                return status;
            }

            try
            {
                using var client = CreateHttpClient(timeoutSeconds: 8);
                // 斗鱼旧的 /wgapi/member/user/userInfo 已返回 404。当前 Web 首页使用
                // follow/top3 获取登录用户的关注数据：已登录返回 error=0，未登录返回 error=-1。
                var req = new HttpRequestMessage(HttpMethod.Get, "https://www.douyu.com/wgapi/livenc/liveweb/follow/top3")
                {
                    Version = HttpVersion.Version11
                };
                req.Headers.TryAddWithoutValidation("Referer", "https://www.douyu.com/");
                req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
                req.Headers.TryAddWithoutValidation("Cookie", clean);

                var res = await client.SendAsync(req);
                if ((int)res.StatusCode >= 500 || (int)res.StatusCode == 429)
                {
                    status.IsNetworkError = true;
                    status.Message = $"服务端暂时不可达 (HTTP {(int)res.StatusCode})";
                    return status;
                }

                if (!res.IsSuccessStatusCode)
                {
                    status.IsValid = false;
                    status.IsNetworkError = true;
                    status.Message = $"斗鱼认证接口暂时不可用 (HTTP {(int)res.StatusCode})";
                    return status;
                }

                ApplyDouyuVerificationResponse(
                    status,
                    clean,
                    await res.Content.ReadAsStringAsync());
            }
            catch (Exception ex)
            {
                status.IsNetworkError = true;
                status.Message = $"网络检测异常: {ex.Message}";
            }

            return status;
        }

        internal static void ApplyDouyuVerificationResponse(
            PlatformCookieStatus status,
            string cookieHeader,
            string responseBody)
        {
            try
            {
                var json = JsonNode.Parse(responseBody);
                bool hasErrorCode = int.TryParse(json?["error"]?.ToString(), out int error);
                string upstreamMessage = json?["msg"]?.ToString() ?? "";

                if (hasErrorCode && error == 0)
                {
                    status.IsValid = true;
                    status.IsNetworkError = false;
                    status.Username = GetDouyuUsernameFromCookie(cookieHeader);
                    status.Message = "已授权有效";
                }
                else if ((hasErrorCode && error == -1)
                    || upstreamMessage.Contains("未登录", StringComparison.OrdinalIgnoreCase)
                    || upstreamMessage.Contains("未登陆", StringComparison.OrdinalIgnoreCase)
                    || upstreamMessage.Contains("token已过期", StringComparison.OrdinalIgnoreCase))
                {
                    status.IsValid = false;
                    status.IsNetworkError = false;
                    status.Message = "Cookie已失效或未登录";
                }
                else
                {
                    status.IsValid = false;
                    status.IsNetworkError = true;
                    status.Message = hasErrorCode
                        ? $"斗鱼认证响应异常 (error={error})"
                        : "斗鱼认证响应格式异常";
                }
            }
            catch (JsonException)
            {
                status.IsValid = false;
                status.IsNetworkError = true;
                status.Message = "斗鱼认证响应格式异常";
            }
        }

        private static string GetDouyuUsernameFromCookie(string cookieHeader)
        {
            foreach (string key in new[] { "acf_nickname", "acf_username", "acf_uid" })
            {
                string value = GetCookieValue(cookieHeader, key);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return "";
        }

        private static string GetCookieValue(string cookieHeader, string key)
        {
            foreach (string part in cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int separatorIndex = part.IndexOf('=');
                if (separatorIndex <= 0) continue;

                string candidateKey = part[..separatorIndex].Trim();
                if (!candidateKey.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;

                string encodedValue = part[(separatorIndex + 1)..].Trim();
                string decodedValue = HttpUtility.UrlDecode(encodedValue, Encoding.UTF8) ?? encodedValue;
                return new string(decodedValue
                    .Where(c => !char.IsControl(c))
                    .Take(80)
                    .ToArray())
                    .Trim();
            }

            return "";
        }

        public static async Task<PlatformCookieStatus> VerifyHuyaOnceAsync(string? cookies)
        {
            string clean = BaseExtractor.SanitizeAsciiHeader(cookies);
            var status = new PlatformCookieStatus
            {
                Platform = "huya",
                Configured = !string.IsNullOrWhiteSpace(clean),
                LastChecked = DateTime.Now
            };

            if (!status.Configured)
            {
                status.IsValid = false;
                status.Message = "未配置";
                return status;
            }

            try
            {
                using var client = CreateHttpClient(timeoutSeconds: 8);

                // 优先检测 mp.huya.com 接口
                try
                {
                    var req1 = new HttpRequestMessage(HttpMethod.Get, "https://mp.huya.com/cache.php?m=My")
                    {
                        Version = HttpVersion.Version11
                    };
                    req1.Headers.TryAddWithoutValidation("Cookie", clean);
                    var res1 = await client.SendAsync(req1);
                    if (res1.IsSuccessStatusCode)
                    {
                        var json1 = JsonNode.Parse(await res1.Content.ReadAsStringAsync());
                        int status1 = json1?["status"]?.GetValue<int>() ?? 0;
                        if (status1 == 200 && json1?["data"]?["yyid"] != null)
                        {
                            status.IsValid = true;
                            status.IsNetworkError = false;
                            status.Username = json1["data"]?["nick"]?.GetValue<string>() ?? json1["data"]?["yyid"]?.ToString() ?? "";
                            status.Message = "已授权有效";
                            return status;
                        }
                    }
                }
                catch { }

                // 备用检测 udb.huya.com 接口
                try
                {
                    var req2 = new HttpRequestMessage(HttpMethod.Get, "https://udb.huya.com/udbserver/udb/getuserinfo.php")
                    {
                        Version = HttpVersion.Version11
                    };
                    req2.Headers.TryAddWithoutValidation("Referer", "https://www.huya.com/");
                    req2.Headers.TryAddWithoutValidation("Cookie", clean);

                    var res2 = await client.SendAsync(req2);
                    if (res2.IsSuccessStatusCode)
                    {
                        var content = await res2.Content.ReadAsStringAsync();
                        var json = JsonNode.Parse(content);
                        int code = json?["returncode"]?.GetValue<int>() ?? -1;
                        if (code == 0)
                        {
                            status.IsValid = true;
                            status.IsNetworkError = false;
                            status.Username = json?["nick"]?.GetValue<string>() ?? json?["yyuid"]?.ToString() ?? "";
                            status.Message = "已授权有效";
                            return status;
                        }
                    }
                }
                catch { }

                // 备用检测：提取 Cookie 中携带的 yyuid
                var matchUid = Regex.Match(clean, @"(?:yyuid|udb_uid)=(\d+)");
                if (matchUid.Success)
                {
                    status.IsValid = true;
                    status.IsNetworkError = false;
                    status.Username = $"UID: {matchUid.Groups[1].Value}";
                    status.Message = "已授权有效 (凭据包含有效UID)";
                    return status;
                }

                status.IsValid = false;
                status.IsNetworkError = false;
                status.Message = "Cookie已失效或未登录";
            }
            catch (Exception ex)
            {
                status.IsValid = false;
                status.Message = $"检测异常: {ex.Message}";
            }

            return status;
        }
    }
}
