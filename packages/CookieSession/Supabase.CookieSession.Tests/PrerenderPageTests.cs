using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Supabase.CookieSession.Tests;

/// <summary>
/// HTTP prerender of the sample against an in-process fake GoTrue and PostgREST.
/// This is not a live Supabase project.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class PrerenderPageTests
{
    private static FakeUpstream upstream = null!;
    private static SampleFactory factory = null!;

    [ClassInitialize]
    public static void Start(TestContext context)
    {
        upstream = new FakeUpstream();
        factory = new SampleFactory(upstream);
    }

    [ClassCleanup]
    public static void Stop()
    {
        factory.Dispose();
        upstream.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Environment.SetEnvironmentVariable("SUPABASE_URL", null);
        Environment.SetEnvironmentVariable("SUPABASE_ANON_KEY", null);
    }

    [TestMethod]
    public void ShouldRun_ShouldIgnoreAuthAndAssetRequests()
    {
        PrerenderSessionBootstrap.ShouldRun(Request("GET", "/")).Should().BeTrue();
        PrerenderSessionBootstrap.ShouldRun(Request("GET", "/server")).Should().BeTrue();
        PrerenderSessionBootstrap.ShouldRun(Request("POST", "/")).Should().BeFalse();
        PrerenderSessionBootstrap.ShouldRun(Request("POST", "/auth/session/token")).Should().BeFalse();
        PrerenderSessionBootstrap.ShouldRun(Request("GET", "/_framework/blazor.web.js")).Should().BeFalse();
        PrerenderSessionBootstrap.ShouldRun(Request("GET", "/app.css")).Should().BeFalse();
    }

    [TestMethod]
    public async Task Establish_ShouldNotRotate_GivenTheResponseHasAlreadyStarted()
    {
        var callsBefore = upstream.RefreshCalls;
        await using var scope = factory.Services.CreateAsyncScope();
        var session = scope.ServiceProvider.GetRequiredService<HybridCookieSession>();
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(new StartedResponse());
        context.Request.Headers.Cookie = "sb-refresh=present; sb-refresh-n=1";

        await session.EstablishAsync(context);

        upstream.RefreshCalls.Should().Be(callsBefore);
        context.Response.Headers.SetCookie.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Get_ShouldRenderProfileAndNotes_GivenAnInteractiveAutoPrerender() =>
        await this.AssertAuthenticatedPageAsync("/");

    [TestMethod]
    public async Task Get_ShouldRenderProfileAndNotes_GivenAnInteractiveServerPrerender() =>
        await this.AssertAuthenticatedPageAsync("/server");

    [TestMethod]
    public async Task Get_ShouldRenderProfileAndNotes_GivenStaticSsr() =>
        await this.AssertAuthenticatedPageAsync("/static");

    [TestMethod]
    public async Task Get_ShouldRenderTheSignInForm_GivenNoCookie()
    {
        var calls = upstream.RefreshCalls;
        using var client = this.Client();
        var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("Sign in");
        html.Should().NotContain("a@example.test");
        upstream.RefreshCalls.Should().Be(calls);
    }

    [TestMethod]
    public async Task Get_ShouldClearACorruptCookieWithoutFailing()
    {
        using var client = this.Client(handleCookies: false);
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.TryAddWithoutValidation("Cookie", "sb-refresh=not-a-protected-cookie; sb-refresh-n=1");
        var response = await client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("Sign in");
        html.Should().NotContain("not-a-protected-cookie");
        string.Join("\n", response.Headers.GetValues("Set-Cookie")).ToLowerInvariant().Should().Contain("sb-refresh");
    }

    [TestMethod]
    public async Task Get_ShouldClearTheCookieAndStaySignedOut_GivenGoTrueRejectsTheRefresh()
    {
        using var client = this.Client();
        await this.SignInAsync(client, "a@example.test");
        upstream.RejectRefresh = true;
        try
        {
            var response = await client.GetAsync("/");
            var html = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            html.Should().Contain("Sign in");
            html.Should().NotContain("a@example.test");
            html.Should().NotContain("refresh-");
            string.Join("\n", response.Headers.GetValues("Set-Cookie")).ToLowerInvariant().Should().Contain("sb-refresh");
        }
        finally
        {
            upstream.RejectRefresh = false;
        }
    }

    [TestMethod]
    public async Task Get_ShouldKeepTheCookie_GivenATransientRefreshFailure()
    {
        using var client = this.Client();
        await this.SignInAsync(client, "a@example.test");
        var refresh = upstream.LastRefresh;
        upstream.TransientRefresh = true;
        try
        {
            var failed = await client.GetAsync("/");
            var html = await failed.Content.ReadAsStringAsync();
            failed.StatusCode.Should().Be(HttpStatusCode.OK);
            html.Should().Contain("Sign in");
            html.Should().NotContain(refresh);
            if (failed.Headers.TryGetValues("Set-Cookie", out var cookies))
                string.Join("\n", cookies).Should().NotContain(refresh);
        }
        finally
        {
            upstream.TransientRefresh = false;
        }

        var recovered = await client.GetAsync("/");
        var recoveredHtml = await recovered.Content.ReadAsStringAsync();
        recovered.StatusCode.Should().Be(HttpStatusCode.OK);
        recoveredHtml.Should().Contain("a@example.test");
        recoveredHtml.Should().Contain("seed-note-a");
    }

    [TestMethod]
    public async Task TokenFailure_ShouldKeepItsStatus_GivenTheSampleStatusPagePipeline()
    {
        using var client = this.Client();
        var request = new HttpRequestMessage(HttpMethod.Post, "/auth/session/token");
        request.Content = JsonContent.Create(new { });
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        body.Should().NotContain("incorrect Content-type");
    }

    [TestMethod]
    public async Task Token_ShouldRedeemTheRotatedCookie_GivenThePrerenderResponseUpdatedIt()
    {
        using var client = this.Client();
        await this.SignInAsync(client, "a@example.test");
        var original = upstream.LastRefresh;
        var response = await client.GetAsync("/");
        response.EnsureSuccessStatusCode();
        var rotated = upstream.LastRefresh;
        rotated.Should().NotBe(original);

        var token = await this.AntiforgeryAsync(client);
        var request = new HttpRequestMessage(HttpMethod.Post, "/auth/session/token");
        request.Headers.TryAddWithoutValidation("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        request.Headers.TryAddWithoutValidation("RequestVerificationToken", token);
        request.Content = JsonContent.Create(new { });
        var again = await client.SendAsync(request);
        again.StatusCode.Should().Be(HttpStatusCode.OK);
        upstream.RefreshTokens.Last().Should().Be(rotated);
        rotated.Should().NotBe(original);
        var body = await again.Content.ReadAsStringAsync();
        body.Should().NotContain(rotated);
        body.Should().NotContain(original);
    }

    [TestMethod]
    public async Task Get_ShouldNotMixTwoUsersNotes()
    {
        using var first = this.Client();
        using var second = this.Client();
        await this.SignInAsync(first, "a@example.test");
        await this.SignInAsync(second, "b@example.test");
        var a = await (await first.GetAsync("/")).Content.ReadAsStringAsync();
        var b = await (await second.GetAsync("/server")).Content.ReadAsStringAsync();
        a.Should().Contain("a@example.test").And.Contain("seed-note-a");
        a.Should().NotContain("seed-note-b").And.NotContain("b@example.test");
        b.Should().Contain("b@example.test").And.Contain("seed-note-b");
        b.Should().NotContain("seed-note-a").And.NotContain("a@example.test");
    }

    private async Task AssertAuthenticatedPageAsync(string path)
    {
        using var client = this.Client();
        await this.SignInAsync(client, "a@example.test");
        var calls = upstream.RefreshCalls;
        var notes = upstream.NotesCalls;
        var response = await client.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (upstream.RefreshCalls - calls).Should().Be(1, "the document request redeems once, and the component reuses that request");
        (upstream.NotesCalls - notes).Should().Be(1);
        html.Should().Contain("a@example.test");
        html.Should().Contain("user-a");
        html.Should().Contain("seed-note-a");
        html.Should().Contain("not started until interactive");
        html.Should().NotContain("Sign in");
        html.Should().NotContain("joined notes");
        html.Should().NotContain(upstream.LastAccess);
        html.Should().NotContain(upstream.LastRefresh);
        var setCookie = string.Join("\n", response.Headers.GetValues("Set-Cookie"));
        setCookie.ToLowerInvariant().Should().Contain("httponly");
        setCookie.Should().NotContain(upstream.LastRefresh);
        setCookie.Should().NotContain(upstream.LastAccess);
        upstream.LastNotesAuthorization.Should().Be("Bearer " + upstream.LastAccess);
    }

    private HttpClient Client(bool handleCookies = true) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = handleCookies,
            AllowAutoRedirect = false,
        });

    private async Task SignInAsync(HttpClient client, string email)
    {
        var token = await this.AntiforgeryAsync(client);
        var request = new HttpRequestMessage(HttpMethod.Post, "/auth/session/signin");
        request.Headers.TryAddWithoutValidation("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        request.Headers.TryAddWithoutValidation("RequestVerificationToken", token);
        request.Content = JsonContent.Create(new { email, password = "pw" });
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().NotContain(upstream.LastRefresh);
    }

    private async Task<string> AntiforgeryAsync(HttpClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/auth/session/csrf");
        request.Headers.TryAddWithoutValidation("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        return document.GetProperty("token").GetString() ?? "";
    }

    private static HttpRequest Request(string method, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        return context.Request;
    }

    private sealed class SampleFactory : WebApplicationFactory<Program>
    {
        private readonly FakeUpstream upstream;

        public SampleFactory(FakeUpstream upstream) => this.upstream = upstream;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Environment.SetEnvironmentVariable("SUPABASE_URL", this.upstream.Url);
            Environment.SetEnvironmentVariable("SUPABASE_ANON_KEY", "sb_publishable_testkey");
            builder.UseEnvironment("Development");
        }
    }

    private sealed class StartedResponse : IHttpResponseFeature
    {
        public int StatusCode { get; set; }

        public string? ReasonPhrase { get; set; }

        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();

        public Stream Body { get; set; } = new MemoryStream();

        public bool HasStarted => true;

        public void OnCompleted(Func<object, Task> callback, object state)
        {
        }

        public void OnStarting(Func<object, Task> callback, object state)
        {
        }
    }
}
