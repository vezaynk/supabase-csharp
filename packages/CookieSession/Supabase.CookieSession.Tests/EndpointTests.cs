using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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

    private TestApp(WebApplication app, HttpClient client, ListLogger logger)
    {
        this.app = app;
        this.Client = client;
        this.Logger = logger;
        this.Origin = client.BaseAddress!.GetLeftPart(UriPartial.Authority);
    }

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
        var app = builder.Build();
        app.UseAntiforgery();
        app.MapCookieSessionEndpoints();
        await app.StartAsync();
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true };
        var client = new HttpClient(handler) { BaseAddress = new Uri(app.Urls.Single()) };
        return new TestApp(app, client, logger.Logger);
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

    public async Task<string> AntiforgeryAsync()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/auth/session/csrf");
        request.Headers.TryAddWithoutValidation("Origin", this.Origin);
        var response = await this.Client.SendAsync(request);
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
