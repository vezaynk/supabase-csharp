using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Supabase.CookieSession.Tests.Support;

namespace Supabase.CookieSession.Tests;

[TestClass]
[TestCategory("Unit")]
public class EndpointTests
{
    [TestMethod]
    public async Task SignIn_ShouldSetAnHttpOnlyRefreshCookieAndReturnOnlyTheAccessToken()
    {
        await using var host = await TestApp.Start(new FakeAuth());
        var response = await host.SendAsync(HttpMethod.Post, "/auth/session/signin", new { email = "a@example.test", password = "password-secret" });
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl?.NoStore.Should().BeTrue();
        body.Should().Contain("access-secret-value");
        body.Should().NotContain("refresh-secret-value");
        body.Should().NotContain("password-secret");
        var cookies = string.Join("\n", response.Headers.GetValues("Set-Cookie"));
        cookies.Should().Contain("httponly");
        cookies.Should().Contain("samesite=lax");
        cookies.Should().NotContain("refresh-secret-value");
        cookies.Should().NotContain("access-secret-value");
        string.Join("\n", host.Logger.Lines).Should().NotContain("refresh-secret-value").And.NotContain("access-secret-value").And.NotContain("password-secret");
    }

    [TestMethod]
    public async Task Token_ShouldRotateOnce_GivenTwoConcurrentBrowsersPresentingTheSameCookie()
    {
        var auth = new FakeAuth { Hold = true };
        await using var host = await TestApp.Start(auth);
        await host.SendAsync(HttpMethod.Post, "/auth/session/signin", new { email = "a@example.test", password = "pw" });
        auth.Hold = true;
        auth.Next = new IssuedTokens
        {
            AccessToken = "access-rotated",
            RefreshToken = "refresh-rotated",
            ExpiresIn = 1800,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
            UserId = "user-a",
            Email = "a@example.test",
        };

        var first = host.SendAsync(HttpMethod.Post, "/auth/session/token", new { });
        var second = host.SendAsync(HttpMethod.Post, "/auth/session/token", new { });
        await Waits.Until(() => auth.RefreshCalls == 1);
        auth.Gate.SetResult();
        var responses = await Task.WhenAll(first, second);
        var bodies = await Task.WhenAll(responses.Select(response => response.Content.ReadAsStringAsync()));

        auth.RefreshCalls.Should().Be(1);
        bodies.Should().OnlyContain(body => body.Contains("access-rotated", StringComparison.Ordinal) && !body.Contains("refresh", StringComparison.OrdinalIgnoreCase));
        responses.Should().OnlyContain(response => response.Headers.CacheControl != null && response.Headers.CacheControl.NoStore);
    }

    [TestMethod]
    public async Task Token_ShouldClearTheCookie_GivenGoTrueRejectsAndNoNewerGenerationExists()
    {
        var auth = new FakeAuth();
        await using var host = await TestApp.Start(auth);
        await host.SendAsync(HttpMethod.Post, "/auth/session/signin", new { email = "a@example.test", password = "pw" });
        auth.Reject = true;
        var response = await host.SendAsync(HttpMethod.Post, "/auth/session/token", new { });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        string.Join("\n", response.Headers.GetValues("Set-Cookie")).Should().NotContain("refresh-secret-value");
    }

    [TestMethod]
    public async Task Token_ShouldRejectMissingOriginAndMissingAntiforgery()
    {
        await using var host = await TestApp.Start(new FakeAuth());
        var origin = new HttpRequestMessage(HttpMethod.Post, "/auth/session/token");
        origin.Content = JsonContent.Create(new { });
        (await host.Client.SendAsync(origin)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var token = await host.AntiforgeryAsync();
        var forged = new HttpRequestMessage(HttpMethod.Post, "/auth/session/signin");
        forged.Headers.TryAddWithoutValidation("Origin", host.Origin);
        forged.Content = JsonContent.Create(new { email = "a@example.test", password = "pw" });
        (await host.Client.SendAsync(forged)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        token.Should().NotBeNullOrEmpty();
    }

    [TestMethod]
    public async Task Config_ShouldExposeOnlyThePublicProject()
    {
        await using var host = await TestApp.Start(new FakeAuth());
        var response = await host.SendAsync(HttpMethod.Get, "/auth/session/config", null);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("http://project.test");
        body.Should().Contain("sb_publishable_testkey");
        body.Should().NotContain("refresh");
        body.Should().NotContain("service_role");
    }

    [TestMethod]
    public async Task Token_ShouldAskForARetry_GivenAStaleGenerationAfterTheSingleFlightWindow()
    {
        var auth = new FakeAuth();
        await using var host = await TestApp.Start(auth);
        var now = DateTimeOffset.Parse("2026-11-10T00:00:00Z");
        host.Get<RefreshAuthority>().Clock = () => now;
        await host.SendAsync(HttpMethod.Post, "/auth/session/signin", new { email = "a@example.test", password = "pw" });
        var stale = host.CopyCookies();
        await host.SendAsync(HttpMethod.Post, "/auth/session/token", new { });
        now = now.AddSeconds(31);
        var calls = auth.RefreshCalls;

        using var retry = host.CreateClient(stale);
        var request = new HttpRequestMessage(HttpMethod.Post, "/auth/session/token");
        request.Headers.TryAddWithoutValidation("Origin", host.Origin);
        request.Headers.TryAddWithoutValidation("RequestVerificationToken", await host.AntiforgeryAsync(retry));
        request.Content = JsonContent.Create(new { });
        var response = await retry.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, body);
        auth.RefreshCalls.Should().Be(calls, "a stale generation must not be sent to GoTrue after the window");
        response.Headers.Should().NotContain(header => header.Key == "Set-Cookie");
        (await host.SendAsync(HttpMethod.Post, "/auth/session/token", new { })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task Token_ShouldRejectHttpsOriginOnAnHttpApp_GivenNoAllowListAndNoForwardedProto()
    {
        await using var host = await TestApp.Start(new FakeAuth());
        var response = await this.PostTokenAsync(host, host.HttpsOrigin, null);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [TestMethod]
    public async Task Token_ShouldAllowHttpsOrigin_GivenTheAllowListOverridesTheScheme()
    {
        await using var host = await TestApp.Start(new FakeAuth());
        host.Get<CookieSessionOptions>().AllowedOrigins.Add(host.HttpsOrigin);
        var response = await this.PostTokenAsync(host, host.HttpsOrigin, null);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task Token_ShouldAllowHttpsOrigin_GivenForwardedProtoAndHost()
    {
        await using var host = await TestApp.Start(new FakeAuth());
        var response = await this.PostTokenAsync(host, host.HttpsOrigin, new Dictionary<string, string>
        {
            ["X-Forwarded-Proto"] = "https",
            ["X-Forwarded-Host"] = new Uri(host.HttpsOrigin).Authority,
        });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task Token_ShouldRejectAForeignHttpsOrigin_GivenForwardedProto()
    {
        await using var host = await TestApp.Start(new FakeAuth());
        var response = await this.PostTokenAsync(host, "https://evil.example", new Dictionary<string, string>
        {
            ["X-Forwarded-Proto"] = "https",
            ["X-Forwarded-Host"] = new Uri(host.HttpsOrigin).Authority,
        });
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private async Task<HttpResponseMessage> PostTokenAsync(TestApp host, string origin, Dictionary<string, string>? extra)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/auth/session/token");
        request.Headers.TryAddWithoutValidation("Origin", origin);
        request.Content = JsonContent.Create(new { });
        if (extra is not null)
        {
            foreach (var header in extra)
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return await host.Client.SendAsync(request);
    }

    [TestMethod]
    public void AccessGrant_ShouldNotSerializeARefreshToken()
    {
        var json = JsonSerializer.Serialize(new AccessGrant
        {
            AccessToken = "access-secret-value",
            ExpiresAt = DateTimeOffset.Parse("2026-11-10T01:00:00Z"),
            ExpiresIn = 3600,
            User = new PublicUser { Id = "user-a", Email = "a@example.test" },
        });
        json.Should().Contain("access_token");
        json.Should().NotContain("refresh");
    }
}

internal sealed class StubJs : Microsoft.JSInterop.IJSRuntime
{
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        throw new InvalidOperationException("JS interop is not used by the cookie endpoints.");

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
        throw new InvalidOperationException("JS interop is not used by the cookie endpoints.");
}

internal sealed class TestApp : IAsyncDisposable
{
    private readonly WebApplication app;

    private readonly HttpClientHandler handler;

    private TestApp(WebApplication app, HttpClientHandler handler, HttpClient client, ListLogger logger)
    {
        this.app = app;
        this.handler = handler;
        this.Client = client;
        this.Logger = logger;
        this.Origin = client.BaseAddress!.GetLeftPart(UriPartial.Authority);
    }

    public string HttpsOrigin => "https://" + new Uri(this.Origin).Authority;

    public HttpClient Client { get; }

    public ListLogger Logger { get; }

    public string Origin { get; }

    public static async Task<TestApp> Start(FakeAuth auth, Action<CookieSessionOptions>? configure = null)
    {
        var logger = new ListLoggerProvider();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logger);
        builder.Services.AddDataProtection();
        builder.Services.AddAntiforgery();
        builder.Services.AddScoped<Microsoft.JSInterop.IJSRuntime, StubJs>();
        builder.Services.AddCookieSession(options =>
        {
            options.SupabaseUrl = "http://project.test";
            options.AnonKey = "sb_publishable_testkey";
            options.SecureCookies = false;
            options.SingleFlightTtl = TimeSpan.FromSeconds(30);
            configure?.Invoke(options);
        });
        builder.Services.AddSingleton<IAuthTokenClient>(auth);
        builder.Services.Configure<ForwardedHeadersOptions>(options => options.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost);
        var app = builder.Build();
        app.UseForwardedHeaders();
        app.UseAntiforgery();
        app.MapCookieSessionEndpoints();
        await app.StartAsync();
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true };
        var client = new HttpClient(handler) { BaseAddress = new Uri(app.Urls.Single()) };
        return new TestApp(app, handler, client, logger.Logger);
    }

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("Origin", this.Origin);
        if (method == HttpMethod.Post)
        {
            var token = await this.AntiforgeryAsync();
            request.Headers.TryAddWithoutValidation("RequestVerificationToken", token);
            request.Content = JsonContent.Create(body ?? new { });
        }

        return await this.Client.SendAsync(request);
    }

    public T Get<T>() where T : notnull => this.app.Services.GetRequiredService<T>();

    public CookieContainer CopyCookies()
    {
        var copy = new CookieContainer();
        var uri = this.Client.BaseAddress!;
        foreach (Cookie cookie in this.handler.CookieContainer.GetCookies(uri))
            copy.Add(new Cookie(cookie.Name, cookie.Value, string.IsNullOrEmpty(cookie.Path) ? "/" : cookie.Path, uri.Host));
        return copy;
    }

    public HttpClient CreateClient(CookieContainer cookies) =>
        new(new HttpClientHandler { CookieContainer = cookies, UseCookies = true }) { BaseAddress = this.Client.BaseAddress };

    public Task<string> AntiforgeryAsync() => this.AntiforgeryAsync(this.Client);

    public async Task<string> AntiforgeryAsync(HttpClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/auth/session/csrf");
        request.Headers.TryAddWithoutValidation("Origin", this.Origin);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        return document.GetProperty("token").GetString() ?? "";
    }

    public async ValueTask DisposeAsync()
    {
        await this.app.StopAsync();
        await this.app.DisposeAsync();
        this.Client.Dispose();
    }
}
