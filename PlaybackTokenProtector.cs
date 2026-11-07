#nullable enable

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// 为 Jellyfin/IPTV 生成独立的 6 位数字播放令牌，并兼容升级前的长令牌。
/// 路由校验只使用 SHA-256 摘要；为便于已登录管理员再次复制订阅地址，
/// 令牌原文另以管理员密码派生密钥进行 AES-256-GCM 加密，绝不明文落盘。
/// </summary>
public static class PlaybackTokenProtector
{
    private const int EncryptionIterations = 310_000;
    private const int SaltBytes = 16;
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private static readonly byte[] AssociatedData = Encoding.UTF8.GetBytes("LiveStreamGateway/playback-token/v1");

    public static PlaybackTokenCredentials Create(string adminPassword)
    {
        using var protection = CreateSessionProtection(adminPassword);
        return Create(protection);
    }

    internal static PlaybackTokenCredentials Create(PlaybackTokenEncryptionKey protection)
    {
        string token = RandomNumberGenerator.GetInt32(0, 1_000_000)
            .ToString("D6", CultureInfo.InvariantCulture);
        return new PlaybackTokenCredentials(
            token,
            HashToken(token),
            protection.Protect(token));
    }

    public static string HashToken(string token) =>
        $"sha256${Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)))}";

    public static bool ValidateToken(string? token, string? encodedHash)
    {
        if (!IsSupportedTokenFormat(token) || string.IsNullOrWhiteSpace(encodedHash))
            return false;
        string validatedToken = token!;

        try
        {
            string[] parts = encodedHash.Split('$');
            if (parts.Length != 2 || !parts[0].Equals("sha256", StringComparison.Ordinal))
                return false;
            byte[] expected = Convert.FromBase64String(parts[1]);
            byte[] actual = SHA256.HashData(Encoding.UTF8.GetBytes(validatedToken));
            return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    public static string Protect(string token, string adminPassword)
    {
        using var protection = CreateSessionProtection(adminPassword);
        return protection.Protect(token);
    }

    // 已登录会话仅保留派生密钥，不保存管理员明文密码；沿用现有密文格式以兼容重新登录和改密。
    internal static PlaybackTokenEncryptionKey CreateSessionProtection(string adminPassword, string? encoded = null)
    {
        int iterations = EncryptionIterations;
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        if (!string.IsNullOrWhiteSpace(encoded))
        {
            try
            {
                string[] parts = encoded.Split('$');
                if (parts.Length == 6 && parts[0] == "aes-256-gcm" &&
                    int.TryParse(parts[1], out int existingIterations) && existingIterations is >= 100_000 and <= 2_000_000)
                {
                    byte[] existingSalt = Convert.FromBase64String(parts[2]);
                    if (existingSalt.Length >= SaltBytes)
                    {
                        iterations = existingIterations;
                        salt = existingSalt;
                    }
                }
            }
            catch (FormatException)
            {
                // 损坏或缺失的令牌可由已登录管理员直接轮换修复。
            }
        }

        byte[] key = Rfc2898DeriveBytes.Pbkdf2(
            adminPassword,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            KeyBytes);
        return new PlaybackTokenEncryptionKey(iterations, salt, key);
    }

    internal static string Protect(string token, int iterations, byte[] salt, byte[] key)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        byte[] plaintext = Encoding.UTF8.GetBytes(token);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[TagBytes];

        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData);
            return $"aes-256-gcm${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(nonce)}${Convert.ToBase64String(ciphertext)}${Convert.ToBase64String(tag)}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public static bool TryUnprotect(string? encoded, string adminPassword, out string token)
    {
        token = "";
        byte[]? key = null;
        byte[]? plaintext = null;
        try
        {
            if (string.IsNullOrWhiteSpace(encoded)) return false;
            string[] parts = encoded.Split('$');
            if (parts.Length != 6 || !parts[0].Equals("aes-256-gcm", StringComparison.Ordinal))
                return false;
            if (!int.TryParse(parts[1], out int iterations) || iterations < 100_000 || iterations > 2_000_000)
                return false;

            byte[] salt = Convert.FromBase64String(parts[2]);
            byte[] nonce = Convert.FromBase64String(parts[3]);
            byte[] ciphertext = Convert.FromBase64String(parts[4]);
            byte[] tag = Convert.FromBase64String(parts[5]);
            if (salt.Length < SaltBytes || nonce.Length != NonceBytes || tag.Length != TagBytes || ciphertext.Length is < 6 or > 256)
                return false;

            key = Rfc2898DeriveBytes.Pbkdf2(
                adminPassword,
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                KeyBytes);
            plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData);
            token = Encoding.UTF8.GetString(plaintext);
            return IsSupportedTokenFormat(token);
        }
        catch
        {
            token = "";
            return false;
        }
        finally
        {
            if (key != null) CryptographicOperations.ZeroMemory(key);
            if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public static bool IsSupportedTokenFormat(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;

        if (token.Length == 6)
        {
            foreach (char character in token)
            {
                if (character is < '0' or > '9') return false;
            }
            return true;
        }

        // 兼容升级前生成的 Base64URL 长令牌；现有地址在主动轮换前继续有效。
        if (token.Length is < 32 or > 128) return false;
        foreach (char character in token)
        {
            bool allowed = character is >= 'a' and <= 'z' or
                           >= 'A' and <= 'Z' or
                           >= '0' and <= '9' or '-' or '_';
            if (!allowed) return false;
        }
        return true;
    }
}

public sealed record PlaybackTokenCredentials(string Token, string Hash, string Encrypted);

/// <summary>仅在管理会话有效期内驻留内存的恢复密钥，注销、过期或作废时清零。</summary>
internal sealed class PlaybackTokenEncryptionKey(int iterations, byte[] salt, byte[] key) : IDisposable
{
    private readonly object _syncRoot = new();
    private bool _disposed;

    internal string Protect(string token)
    {
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return PlaybackTokenProtector.Protect(token, iterations, salt, key);
        }
    }

    internal bool TryClone(out PlaybackTokenEncryptionKey? copy)
    {
        lock (_syncRoot)
        {
            copy = _disposed ? null : new PlaybackTokenEncryptionKey(iterations, (byte[])salt.Clone(), (byte[])key.Clone());
            return copy != null;
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed) return;
            _disposed = true;
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
