using Microsoft.AspNetCore.Http;

namespace LiveStreamGateway.Tests;

[CollectionDefinition("Playback token auth globals", DisableParallelization = true)]
public sealed class PlaybackTokenAuthCollection;

[Collection("Playback token auth globals")]
public sealed class PlaybackTokenRotationTests
{
    private const string Password = "original-admin-password";

    [Fact]
    public void AnonymousOrForgedSession_CannotObtainRotationKey()
    {
        using var fixture = new AuthFixture();

        Assert.False(fixture.Auth.TryGetPlaybackTokenProtection(new DefaultHttpContext().Request, out var anonymous));
        Assert.Null(anonymous);
        Assert.False(fixture.Auth.TryGetPlaybackTokenProtection(Request("forged-session"), out var forged));
        Assert.Null(forged);
    }

    [Fact]
    public void LoggedInSession_CanRotateWithoutPassword_AndReLoginRecoversNewToken()
    {
        using var fixture = new AuthFixture();
        string oldToken = fixture.Credentials.Token;
        var firstRequest = Request(fixture.Auth.CreateSession(Password));
        var otherRequest = Request(fixture.Auth.CreateSession(Password));

        Assert.True(fixture.Auth.TryGetPlaybackTokenProtection(firstRequest, out var protection));
        using (protection)
        {
            PlaybackTokenCredentials rotated;
            do rotated = PlaybackTokenProtector.Create(protection!);
            while (rotated.Token == oldToken);
            Assert.Matches("^[0-9]{6}$", rotated.Token);
            Assert.True(PlaybackTokenProtector.ValidateToken(rotated.Token, rotated.Hash));
            Assert.False(PlaybackTokenProtector.ValidateToken(oldToken, rotated.Hash));
            Assert.True(PlaybackTokenProtector.TryUnprotect(rotated.Encrypted, Password, out string recovered));
            Assert.Equal(rotated.Token, recovered);
            Assert.False(PlaybackTokenProtector.TryUnprotect(rotated.Encrypted, "wrong-admin-password", out _));
            Assert.NotEqual(fixture.Credentials.Encrypted.Split('$')[3], rotated.Encrypted.Split('$')[3]);
            Globals.Config.PlaybackTokenHash = rotated.Hash;
            Globals.Config.PlaybackTokenEncrypted = rotated.Encrypted;

            fixture.Auth.InvalidateAllSessions();
            var renewedRequest = Request(fixture.Auth.CreateSession(protection!, rotated.Token));

            Assert.False(fixture.Auth.IsAuthenticated(firstRequest));
            Assert.False(fixture.Auth.IsAuthenticated(otherRequest));
            Assert.True(fixture.Auth.IsAuthenticated(renewedRequest));
            Assert.True(fixture.Auth.TryGetPlaybackToken(renewedRequest, out string sessionToken));
            Assert.Equal(rotated.Token, sessionToken);
            Assert.True(fixture.Auth.TryGetPlaybackTokenProtection(renewedRequest, out var nextProtection));
            using (nextProtection)
            {
                var next = PlaybackTokenProtector.Create(nextProtection!);
                Assert.True(PlaybackTokenProtector.TryUnprotect(next.Encrypted, Password, out _));
            }

            var reLogin = Request(fixture.Auth.CreateSession(Password));
            Assert.True(fixture.Auth.TryGetPlaybackToken(reLogin, out string loginToken));
            Assert.Equal(rotated.Token, loginToken);
        }
    }

    [Fact]
    public void LegacyLongToken_RemainsReadableUntilExplicitRotation()
    {
        using var fixture = new AuthFixture();
        string oldToken = new('A', 43);
        Globals.Config.PlaybackTokenHash = PlaybackTokenProtector.HashToken(oldToken);
        Globals.Config.PlaybackTokenEncrypted = PlaybackTokenProtector.Protect(oldToken, Password);
        var request = Request(fixture.Auth.CreateSession(Password));

        Assert.True(fixture.Auth.TryGetPlaybackToken(request, out string recovered));
        Assert.Equal(oldToken, recovered);
        Assert.True(fixture.Auth.TryGetPlaybackTokenProtection(request, out var protection));
        using (protection)
        {
            var rotated = PlaybackTokenProtector.Create(protection!);
            Assert.Matches("^[0-9]{6}$", rotated.Token);
            Assert.True(PlaybackTokenProtector.TryUnprotect(rotated.Encrypted, Password, out _));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("broken-ciphertext")]
    [InlineData("aes-256-gcm$310000$invalid-base64$nonce$ciphertext$tag")]
    public void MissingOrCorruptCiphertext_CanBeReplacedByLoggedInSession(string encrypted)
    {
        using var fixture = new AuthFixture();
        Globals.Config.PlaybackTokenEncrypted = encrypted;
        var request = Request(fixture.Auth.CreateSession(Password));

        Assert.False(fixture.Auth.TryGetPlaybackToken(request, out _));
        Assert.True(fixture.Auth.TryGetPlaybackTokenProtection(request, out var protection));
        using (protection)
        {
            var rotated = PlaybackTokenProtector.Create(protection!);
            Assert.True(PlaybackTokenProtector.TryUnprotect(rotated.Encrypted, Password, out string recovered));
            Assert.Equal(rotated.Token, recovered);
        }
    }

    [Fact]
    public void RevokedSession_CannotObtainRotationKey()
    {
        using var fixture = new AuthFixture();
        var request = Request(fixture.Auth.CreateSession(Password));
        fixture.Auth.RevokeSession(request);

        Assert.False(fixture.Auth.IsAuthenticated(request));
        Assert.False(fixture.Auth.TryGetPlaybackTokenProtection(request, out _));
    }

    [Fact]
    public void PasswordChange_PreservesTokenAndOnlyNewPasswordDecryptsSubsequentRotation()
    {
        using var fixture = new AuthFixture();
        var oldRequest = Request(fixture.Auth.CreateSession(Password));
        const string newPassword = "replacement-admin-password";
        Globals.Config.AdminPasswordHash = AdminAuthService.HashPassword(newPassword);
        Globals.Config.PlaybackTokenEncrypted = PlaybackTokenProtector.Protect(fixture.Credentials.Token, newPassword);
        fixture.Auth.InvalidateAllSessions();
        var newRequest = Request(fixture.Auth.CreateSession(newPassword));

        Assert.False(fixture.Auth.TryGetPlaybackTokenProtection(oldRequest, out _));
        Assert.True(fixture.Auth.TryGetPlaybackToken(newRequest, out string recovered));
        Assert.Equal(fixture.Credentials.Token, recovered);
        Assert.True(fixture.Auth.TryGetPlaybackTokenProtection(newRequest, out var protection));
        using (protection)
        {
            var rotated = PlaybackTokenProtector.Create(protection!);
            Assert.True(PlaybackTokenProtector.TryUnprotect(rotated.Encrypted, newPassword, out _));
            Assert.False(PlaybackTokenProtector.TryUnprotect(rotated.Encrypted, Password, out _));
        }
    }

    [Fact]
    public void DisposedProtection_CannotBeClonedOrUsed()
    {
        using var protection = PlaybackTokenProtector.CreateSessionProtection(Password);
        protection.Dispose();

        Assert.False(protection.TryClone(out var clone));
        Assert.Null(clone);
        Assert.Throws<ObjectDisposedException>(() => PlaybackTokenProtector.Create(protection));
    }

    private static HttpRequest Request(string token)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = $"{AdminAuthService.SessionCookieName}={token}";
        return context.Request;
    }

    private sealed class AuthFixture : IDisposable
    {
        private readonly AppConfig _original;
        internal AdminAuthService Auth { get; } = new();
        internal PlaybackTokenCredentials Credentials { get; } = PlaybackTokenProtector.Create(Password);

        internal AuthFixture()
        {
            lock (Globals.ConfigLock)
            {
                _original = Globals.Config;
                Globals.Config = new AppConfig
                {
                    AdminPasswordHash = AdminAuthService.HashPassword(Password),
                    PlaybackTokenHash = Credentials.Hash,
                    PlaybackTokenEncrypted = Credentials.Encrypted
                };
            }
        }

        public void Dispose()
        {
            Auth.Dispose();
            lock (Globals.ConfigLock)
                Globals.Config = _original;
        }
    }
}
