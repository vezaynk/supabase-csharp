using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace Supabase.CookieSession.Tests;

[TestClass]
[TestCategory("Unit")]
public class CookieJarTests
{
    private const string RefreshToken = "refresh-secret-value";

    [TestMethod]
    public void TryWrite_ShouldRoundTripAndHideTheRefreshToken_GivenChunkedHttpOnlyCookies()
    {
        var jar = this.Jar(chunkChars: 40, secure: true);
        var context = new DefaultHttpContext();
        var cookie = new RefreshCookie
        {
            SessionId = "session-a",
            Generation = 1,
            RefreshToken = RefreshToken + new string('x', 200),
            UserId = "user-a",
            Email = "a@example.test",
        };

        jar.TryWrite(context.Response, cookie).Should().Be(CookieWriteResult.Written);

        var headers = context.Response.Headers.SetCookie.Where(value => value is not null).Select(value => value!).ToArray();
        headers.Should().NotBeEmpty();
        var combined = string.Join("\n", headers);
        combined.Should().Contain("httponly");
        combined.Should().Contain("secure");
        combined.Should().Contain("samesite=lax");
        combined.Should().NotContain(cookie.RefreshToken);
        combined.Should().NotContain("access-secret-value");

        var read = jar.Read(this.Request(headers));
        read.Should().NotBeNull();
        read!.RefreshToken.Should().Be(cookie.RefreshToken);
        read.Generation.Should().Be(1);
        read.UserId.Should().Be("user-a");
    }

    [TestMethod]
    public void TryWrite_ShouldRejectAnOlderGeneration_GivenANewerCookieAlreadyWritten()
    {
        var jar = this.Jar();
        var first = new DefaultHttpContext();
        jar.TryWrite(first.Response, this.Cookie(2, "refresh-newer")).Should().Be(CookieWriteResult.Written);
        var stale = new DefaultHttpContext();
        jar.TryWrite(stale.Response, this.Cookie(1, "refresh-older")).Should().Be(CookieWriteResult.Rejected);
        stale.Response.Headers.SetCookie.Should().BeEmpty();

        var again = new DefaultHttpContext();
        jar.TryWrite(again.Response, this.Cookie(2, "refresh-newer")).Should().Be(CookieWriteResult.Idempotent);
        jar.TryWrite(again.Response, this.Cookie(2, "refresh-different")).Should().Be(CookieWriteResult.Rejected);
    }

    [TestMethod]
    public void StillCurrent_ShouldBeFalse_GivenANewerGenerationWasStored()
    {
        var jar = this.Jar();
        jar.TryWrite(new DefaultHttpContext().Response, this.Cookie(2, "refresh-newer"));
        jar.StillCurrent("session-a", 1).Should().BeFalse();
        jar.StillCurrent("session-a", 2).Should().BeTrue();
        jar.StillCurrent("session-b", 1).Should().BeTrue();
    }

    [TestMethod]
    public void Clear_ShouldRevokeAndDropTheCookie()
    {
        var jar = this.Jar();
        var written = new DefaultHttpContext();
        jar.TryWrite(written.Response, this.Cookie(1, RefreshToken));
        var clearing = new DefaultHttpContext();
        jar.Clear(clearing.Response, "session-a");
        jar.IsRevoked("session-a").Should().BeTrue();
        jar.TryWrite(new DefaultHttpContext().Response, this.Cookie(3, "refresh-later")).Should().Be(CookieWriteResult.Rejected);
        string.Join("\n", clearing.Response.Headers.SetCookie.Select(value => value ?? "")).Should().NotContain(RefreshToken);
    }

    private CookieJar Jar(int chunkChars = 3500, bool secure = false) =>
        new(new EphemeralDataProtectionProvider(), new CookieSessionOptions
        {
            CookieName = "sb-refresh",
            ChunkChars = chunkChars,
            SecureCookies = secure,
            SameSite = SameSiteMode.Lax,
        });

    private RefreshCookie Cookie(long generation, string refresh) => new()
    {
        SessionId = "session-a",
        Generation = generation,
        RefreshToken = refresh,
        UserId = "user-a",
    };

    private HttpRequest Request(IEnumerable<string> setCookies)
    {
        var context = new DefaultHttpContext();
        var pairs = setCookies.Select(header => header.Split(';', 2)[0]);
        context.Request.Headers.Cookie = string.Join("; ", pairs);
        return context.Request;
    }
}
