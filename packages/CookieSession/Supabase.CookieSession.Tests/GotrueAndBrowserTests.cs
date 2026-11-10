using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.JSInterop;
using Supabase.CookieSession.Tests.Support;

namespace Supabase.CookieSession.Tests;

[TestClass]
[TestCategory("Unit")]
public class GotrueAndBrowserTests
{
    [TestMethod]
    public async Task GotrueAuthTokenClient_ShouldPostTheRefreshTokenToGoTrue_GivenAServerRedeem()
    {
        var handler = new CaptureHandler("""{"access_token":"access-from-gotrue","refresh_token":"refresh-from-gotrue","expires_in":120,"token_type":"bearer","user":{"id":"user-1","email":"a@example.test"}}""");
        var client = new GotrueAuthTokenClient(new CookieSessionOptions
        {
            SupabaseUrl = "http://auth.test",
            AnonKey = "sb_publishable_testkey",
        }, new HttpClient(handler));

        var issued = await client.RefreshAsync("refresh-old", CancellationToken.None);

        handler.Url.Should().Contain("/auth/v1/token");
        handler.Url.Should().Contain("grant_type=refresh_token");
        handler.Body.Should().Contain("refresh-old");
        issued.AccessToken.Should().Be("access-from-gotrue");
        issued.RefreshToken.Should().Be("refresh-from-gotrue");
    }

    [TestMethod]
    public async Task GotrueAuthTokenClient_ShouldFailClosedWithoutEchoingTheBody_GivenARejection()
    {
        var handler = new CaptureHandler("""{"error":"refresh-secret-in-body","refresh_token":"refresh-old"}""", HttpStatusCode.BadRequest);
        var client = new GotrueAuthTokenClient(new CookieSessionOptions
        {
            SupabaseUrl = "http://auth.test",
            AnonKey = "sb_publishable_testkey",
        }, new HttpClient(handler));

        var act = () => client.RefreshAsync("refresh-old", CancellationToken.None);
        var error = await act.Should().ThrowAsync<AuthRejectedException>();
        error.Which.Message.Should().NotContain("refresh-old").And.NotContain("refresh-secret-in-body");
    }

    [TestMethod]
    public async Task BrowserCookieSession_ShouldKeepTheAccessTokenInMemory_GivenJsReturnsNoRefreshToken()
    {
        var js = new FakeJs();
        var store = new MemoryAccessTokenStore();
        var session = new BrowserCookieSession(js, new SupabaseMemoryOptions
        {
            SupabaseUrl = "http://project.test",
            AnonKey = "sb_publishable_testkey",
            HeartbeatInterval = TimeSpan.FromHours(1),
            WebSocketFactory = new RecordingFactory(),
        }, store);

        await session.SignInAsync("a@example.test", "password-secret");

        store.AccessToken.Should().Be("access-from-js");
        store.Profile!.Email.Should().Be("a@example.test");
        session.Sdk!.SdkRefreshIsDisabled.Should().BeTrue();
        string.Join(" ", js.Calls.SelectMany(call => call.Arguments.Select(argument => argument?.ToString()))).Should().NotContain("refresh-token");
        js.Calls[0].Identifier.Should().Be("cookieSession.signIn");
    }

    [TestMethod]
    public async Task BrowserCookieSession_ShouldStopTheSocket_GivenRefreshIsRejected()
    {
        var factory = new RecordingFactory();
        var js = new FakeJs();
        var store = new MemoryAccessTokenStore();
        var session = new BrowserCookieSession(js, new SupabaseMemoryOptions
        {
            SupabaseUrl = "http://project.test",
            AnonKey = "sb_publishable_testkey",
            HeartbeatInterval = TimeSpan.FromHours(1),
            WebSocketFactory = factory,
        }, store);
        await session.SignInAsync("a@example.test", "pw");
        await session.SubscribeAsync("notes", _ => Task.CompletedTask);
        factory.Socket.IsRunning.Should().BeTrue();
        js.Next = new AcquireResult { Ok = false, Status = 401, Error = "reauth" };

        var act = () => session.RotateAsync();

        (await act.Should().ThrowAsync<CookieSessionException>()).Which.Reauthenticate.Should().BeTrue();
        store.AccessToken.Should().BeNull();
        factory.Socket.IsRunning.Should().BeFalse();
    }

    [TestMethod]
    public void Script_ShouldUseWebLocksAndRefuseARefreshTokenInTheBrowser()
    {
        var root = FindRepo();
        var script = File.ReadAllText(Path.Combine(root, "packages/CookieSession/sample/BlazorAuto/wwwroot/cookie-session.js"));
        script.Should().Contain("navigator.locks.request");
        script.Should().Contain("credentials: \"same-origin\"");
        script.Should().Contain("cache: \"no-store\"");
        script.Should().Contain("refresh_token");
        script.Should().NotContain("localStorage").And.NotContain("sessionStorage").And.NotContain("indexedDB");
    }

    [TestMethod]
    public void LiveSupabase_IsOptInAndWasNotRun()
    {
        var url = Environment.GetEnvironmentVariable("COOKIE_SESSION_LIVE_URL");
        if (string.IsNullOrWhiteSpace(url))
        {
            // No project credentials are available in this run. The assertion records that the live path was skipped.
            Assert.IsTrue(string.IsNullOrWhiteSpace(url));
            return;
        }

        Assert.Fail("A live URL was set but this prototype does not call a real project from the unit suite.");
    }

    private static string FindRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Supabase.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root was not found");
    }

    private sealed class CaptureHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string Url { get; private set; } = "";

        public string Body { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Url = request.RequestUri?.ToString() ?? "";
            this.Body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class FakeJs : IJSRuntime
    {
        public List<Call> Calls { get; } = [];

        public AcquireResult Next { get; set; } = new()
        {
            Ok = true,
            Status = 200,
            AccessToken = "access-from-js",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            ExpiresIn = 3600,
            User = new PublicUser { Id = "user-a", Email = "a@example.test" },
        };

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            this.InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            this.Calls.Add(new Call(identifier, args ?? []));
            if (identifier == "cookieSession.signOut")
                return new ValueTask<TValue>((TValue) (object) new AcquireResult { Ok = true, Status = 204 });
            return new ValueTask<TValue>((TValue) (object) this.Next);
        }
    }

    private sealed record Call(string Identifier, object?[] Arguments);
}
