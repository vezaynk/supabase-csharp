using FluentAssertions;

namespace Supabase.CookieSession.Tests;

[TestClass]
[TestCategory("Unit")]
public class RefreshAuthorityTests
{
    [TestMethod]
    public async Task Redeem_ShouldCallGoTrueOnce_GivenTwoWaitersForTheSameGeneration()
    {
        var auth = new Support.FakeAuth { Hold = true };
        var authority = new RefreshAuthority(auth, new CookieSessionOptions { SingleFlightTtl = TimeSpan.FromSeconds(10) });
        var presented = this.Cookie("session-a", 1, "refresh-old");
        var writes = new List<RefreshCookie>();

        var first = authority.RedeemAsync(presented, (cookie, _) =>
        {
            lock (writes)
                writes.Add(cookie);
            return Task.FromResult(CookieWriteResult.Written);
        }, CancellationToken.None);
        var second = authority.RedeemAsync(presented, (cookie, _) =>
        {
            lock (writes)
                writes.Add(cookie);
            return Task.FromResult(CookieWriteResult.Written);
        }, CancellationToken.None);

        await Support.Waits.Until(() => auth.RefreshCalls == 1);
        auth.Gate.SetResult();
        var grants = await Task.WhenAll(first, second);

        auth.RefreshCalls.Should().Be(1);
        grants.Select(grant => grant.AccessToken).Should().AllBe("access-secret-value");
        grants[0].Should().NotBeNull();
        writes.Should().OnlyContain(cookie => cookie.RefreshToken == "refresh-secret-value" && cookie.Generation == 2);
        grants[0].ToString().Should().NotContain("refresh-secret-value");
    }

    [TestMethod]
    public async Task Redeem_ShouldNotReuseAnotherUsersFlight()
    {
        var auth = new Support.FakeAuth();
        var authority = new RefreshAuthority(auth, new CookieSessionOptions());
        auth.Next = this.Tokens("access-a", "refresh-a", "user-a");
        var userA = await authority.RedeemAsync(this.Cookie("session-a", 1, "refresh-a0"), (_, _) => Task.FromResult(CookieWriteResult.Written), CancellationToken.None);
        auth.Next = this.Tokens("access-b", "refresh-b", "user-b");
        var userB = await authority.RedeemAsync(this.Cookie("session-b", 1, "refresh-b0"), (_, _) => Task.FromResult(CookieWriteResult.Written), CancellationToken.None);

        auth.RefreshCalls.Should().Be(2);
        userA.AccessToken.Should().Be("access-a");
        userA.User.Id.Should().Be("user-a");
        userB.AccessToken.Should().Be("access-b");
        userB.User.Id.Should().Be("user-b");
    }

    [TestMethod]
    public async Task Redeem_ShouldCallAgain_GivenTheSingleFlightTtlHasPassed()
    {
        var auth = new Support.FakeAuth();
        var now = DateTimeOffset.Parse("2026-11-10T00:00:00Z");
        var authority = new RefreshAuthority(auth, new CookieSessionOptions { SingleFlightTtl = TimeSpan.FromSeconds(10) })
        {
            Clock = () => now,
        };
        var presented = this.Cookie("session-a", 4, "refresh-old");
        await authority.RedeemAsync(presented, (_, _) => Task.FromResult(CookieWriteResult.Written), CancellationToken.None);
        now = now.AddSeconds(11);
        await authority.RedeemAsync(presented, (_, _) => Task.FromResult(CookieWriteResult.Written), CancellationToken.None);
        auth.RefreshCalls.Should().Be(2, "the single-flight memory is not a durable token cache");
    }

    [TestMethod]
    public async Task Redeem_ShouldFailClosed_GivenTheCookieWriteIsRejected()
    {
        var authority = new RefreshAuthority(new Support.FakeAuth(), new CookieSessionOptions());
        var act = () => authority.RedeemAsync(this.Cookie("session-a", 1, "refresh-old"), (_, _) => Task.FromResult(CookieWriteResult.Rejected), CancellationToken.None);
        var error = await act.Should().ThrowAsync<CookieSessionException>();
        error.Which.Reauthenticate.Should().BeTrue();
        error.Which.Message.Should().NotContain("refresh-secret-value");
    }

    [TestMethod]
    public async Task Redeem_ShouldFailClosedWithoutANewCookie_GivenGoTrueRejectsTheRefreshToken()
    {
        var authority = new RefreshAuthority(new Support.FakeAuth { Reject = true }, new CookieSessionOptions());
        var writes = 0;
        var act = () => authority.RedeemAsync(this.Cookie("session-a", 1, "refresh-old"), (_, _) =>
        {
            writes++;
            return Task.FromResult(CookieWriteResult.Written);
        }, CancellationToken.None);
        (await act.Should().ThrowAsync<CookieSessionException>()).Which.Reauthenticate.Should().BeTrue();
        writes.Should().Be(0);
    }

    private RefreshCookie Cookie(string sessionId, long generation, string refresh) => new()
    {
        SessionId = sessionId,
        Generation = generation,
        RefreshToken = refresh,
        UserId = sessionId,
    };

    private IssuedTokens Tokens(string access, string refresh, string user) => new()
    {
        AccessToken = access,
        RefreshToken = refresh,
        ExpiresIn = 3600,
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        UserId = user,
        Email = user + "@example.test",
    };
}
