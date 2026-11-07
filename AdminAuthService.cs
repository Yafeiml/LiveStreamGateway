#nullable enable

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

/// <summary>
/// 管理端密码与内存会话服务。
/// 密码只以 PBKDF2 哈希写入 config.json；会话令牌只通过 HttpOnly Cookie 传递。
/// </summary>
public sealed class AdminAuthService : IDisposable
{
    public const string SessionCookieName = "lsg_admin_session";
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);

    private const int PasswordHashIterations = 310_000;
    private const int PasswordSaltBytes = 16;
    private const int PasswordHashBytes = 32;
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan AttemptWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, AdminSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LoginAttemptState> _loginAttempts = new(StringComparer.Ordinal);

    public bool IsPasswordConfigured
    {
        get
        {
            lock (Globals.ConfigLock)
                return !string.IsNullOrWhiteSpace(Globals.Config.AdminPasswordHash);
        }
    }

    public static string HashPassword(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(PasswordSaltBytes);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            PasswordHashIterations,
            HashAlgorithmName.SHA256,
            PasswordHashBytes);

        return $"pbkdf2-sha256${PasswordHashIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static string? ValidateNewPassword(string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
            return "密码不能为空";
        if (password.Length < 12)
            return "密码至少需要 12 个字符";
        if (password.Length > 256)
            return "密码不能超过 256 个字符";
        return null;
    }

    public bool TryValidateCredentials(
        string? password,
        string clientKey,
        out bool rateLimited,
        out TimeSpan retryAfter)
    {
        rateLimited = false;
        retryAfter = TimeSpan.Zero;

        var state = _loginAttempts.GetOrAdd(clientKey, _ => new LoginAttemptState());
        DateTime now = DateTime.UtcNow;

        lock (state.SyncRoot)
        {
            if (state.BlockedUntilUtc > now)
            {
                rateLimited = true;
                retryAfter = state.BlockedUntilUtc - now;
                return false;
            }

            state.Failures.RemoveAll(at => now - at > AttemptWindow);
        }

        bool valid = VerifyConfiguredPassword(password);
        lock (state.SyncRoot)
        {
            if (valid)
            {
                state.Failures.Clear();
                state.BlockedUntilUtc = DateTime.MinValue;
                _loginAttempts.TryRemove(clientKey, out _);
                return true;
            }

            state.Failures.Add(now);
            if (state.Failures.Count >= MaxFailedAttempts)
            {
                state.Failures.Clear();
                state.BlockedUntilUtc = now.Add(LockoutDuration);
                rateLimited = true;
                retryAfter = LockoutDuration;
            }
        }

        return false;
    }

    public bool VerifyConfiguredPassword(string? password)
    {
        if (string.IsNullOrEmpty(password)) return false;

        string encoded;
        lock (Globals.ConfigLock)
            encoded = Globals.Config.AdminPasswordHash ?? "";

        try
        {
            string[] parts = encoded.Split('$');
            if (parts.Length != 4 || !parts[0].Equals("pbkdf2-sha256", StringComparison.Ordinal))
                return false;
            if (!int.TryParse(parts[1], out int iterations) || iterations < 100_000 || iterations > 2_000_000)
                return false;

            byte[] salt = Convert.FromBase64String(parts[2]);
            byte[] expected = Convert.FromBase64String(parts[3]);
            if (salt.Length < 16 || expected.Length != PasswordHashBytes)
                return false;

            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    public string CreateSession(string? adminPassword = null)
    {
        CleanupExpiredSessions();
        string token = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        string playbackToken = "";
        PlaybackTokenEncryptionKey? protection = null;
        if (!string.IsNullOrEmpty(adminPassword))
        {
            string encryptedPlaybackToken;
            string playbackTokenHash;
            lock (Globals.ConfigLock)
            {
                encryptedPlaybackToken = Globals.Config.PlaybackTokenEncrypted ?? "";
                playbackTokenHash = Globals.Config.PlaybackTokenHash ?? "";
            }

            if (PlaybackTokenProtector.TryUnprotect(encryptedPlaybackToken, adminPassword, out string recovered) &&
                PlaybackTokenProtector.ValidateToken(recovered, playbackTokenHash))
                playbackToken = recovered;
            protection = PlaybackTokenProtector.CreateSessionProtection(adminPassword, encryptedPlaybackToken);
        }

        _sessions[GetSessionKey(token)] = new AdminSession(DateTime.UtcNow.Add(SessionLifetime), playbackToken, protection);
        return token;
    }

    internal bool TryGetPlaybackTokenProtection(HttpRequest request, out PlaybackTokenEncryptionKey? protection)
    {
        protection = null;
        if (!request.Cookies.TryGetValue(SessionCookieName, out string? token) || string.IsNullOrWhiteSpace(token))
            return false;
        string key = GetSessionKey(token);
        if (!_sessions.TryGetValue(key, out AdminSession? session))
            return false;
        if (session.ExpiresAtUtc <= DateTime.UtcNow)
        {
            RemoveSession(key);
            return false;
        }
        return session.Protection?.TryClone(out protection) == true;
    }

    internal string CreateSession(PlaybackTokenEncryptionKey protection, string playbackToken)
    {
        if (!protection.TryClone(out PlaybackTokenEncryptionKey? copy))
            throw new ObjectDisposedException(nameof(protection));
        CleanupExpiredSessions();
        string token = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        _sessions[GetSessionKey(token)] = new AdminSession(DateTime.UtcNow.Add(SessionLifetime), playbackToken, copy);
        return token;
    }

    public bool IsAuthenticated(HttpRequest request)
    {
        if (!request.Cookies.TryGetValue(SessionCookieName, out string? token) || string.IsNullOrWhiteSpace(token))
            return false;

        string key = GetSessionKey(token);
        if (!_sessions.TryGetValue(key, out AdminSession? session))
            return false;
        if (session.ExpiresAtUtc <= DateTime.UtcNow)
        {
            RemoveSession(key);
            return false;
        }

        return true;
    }

    public bool TryGetPlaybackToken(HttpRequest request, out string token)
    {
        token = "";
        if (!request.Cookies.TryGetValue(SessionCookieName, out string? sessionToken) || string.IsNullOrWhiteSpace(sessionToken))
            return false;
        if (!_sessions.TryGetValue(GetSessionKey(sessionToken), out AdminSession? session) ||
            session.ExpiresAtUtc <= DateTime.UtcNow ||
            string.IsNullOrWhiteSpace(session.PlaybackToken))
            return false;

        token = session.PlaybackToken;
        return true;
    }

    public void RevokeSession(HttpRequest request)
    {
        if (request.Cookies.TryGetValue(SessionCookieName, out string? token) && !string.IsNullOrWhiteSpace(token))
            RemoveSession(GetSessionKey(token));
    }

    public void InvalidateAllSessions()
    {
        foreach (string key in _sessions.Keys)
            RemoveSession(key);
    }

    public void Dispose() => InvalidateAllSessions();

    private void RemoveSession(string key)
    {
        if (_sessions.TryRemove(key, out AdminSession? session))
            session.Protection?.Dispose();
    }

    public static void AppendSessionCookie(HttpResponse response, string token, bool isHttps)
    {
        response.Cookies.Append(SessionCookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = isHttps,
            SameSite = SameSiteMode.Strict,
            IsEssential = true,
            Path = "/",
            MaxAge = SessionLifetime,
            Expires = DateTimeOffset.UtcNow.Add(SessionLifetime)
        });
    }

    public static void DeleteSessionCookie(HttpResponse response, bool isHttps)
    {
        response.Cookies.Delete(SessionCookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = isHttps,
            SameSite = SameSiteMode.Strict,
            IsEssential = true,
            Path = "/"
        });
    }

    public static bool IsLoopbackRequest(HttpContext context)
    {
        IPAddress? address = context.Connection.RemoteIpAddress;
        if (address == null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return IPAddress.IsLoopback(address);
    }

    private void CleanupExpiredSessions()
    {
        DateTime now = DateTime.UtcNow;
        foreach (var item in _sessions)
        {
            if (item.Value.ExpiresAtUtc <= now)
                RemoveSession(item.Key);
        }
    }

    private static string GetSessionKey(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class LoginAttemptState
    {
        public object SyncRoot { get; } = new();
        public List<DateTime> Failures { get; } = [];
        public DateTime BlockedUntilUtc { get; set; } = DateTime.MinValue;
    }

    private sealed record AdminSession(DateTime ExpiresAtUtc, string PlaybackToken, PlaybackTokenEncryptionKey? Protection);
}

public sealed class AuthLoginRequest
{
    public string Password { get; set; } = "";
}

public sealed class AuthSetupRequest
{
    public string Password { get; set; } = "";
}

public sealed class AuthPasswordChangeRequest
{
    public string CurrentPassword { get; set; } = "";
    public string NewPassword { get; set; } = "";
}

public sealed class PlaybackTokenAuthRequest
{
    public bool Enabled { get; set; } = true;
}

/// <summary>必须在会话鉴权之前调用，让等候中的旧会话在密钥变更后重新接受鉴权。</summary>
internal sealed class AdminAuthMutationGate : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        bool requiresGate = HttpMethods.IsPost(context.Request.Method) &&
            (context.Request.Path.StartsWithSegments("/api/auth", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(context.Request.Path.Value?.TrimEnd('/'), "/api/playback-token/rotate", StringComparison.OrdinalIgnoreCase));
        if (!requiresGate)
        {
            await next(context);
            return;
        }

        await _gate.WaitAsync(context.RequestAborted);
        try
        {
            await next(context);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
