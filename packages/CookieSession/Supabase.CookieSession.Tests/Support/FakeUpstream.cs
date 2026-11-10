using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Supabase.CookieSession.Tests;

/// <summary>In-process stand-in for GoTrue and PostgREST. Not a live Supabase stack.</summary>
internal sealed class FakeUpstream : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly ConcurrentDictionary<string, string> refreshOwner = new();
    private readonly ConcurrentDictionary<string, string> accessOwner = new();
    private int sequence;

    public FakeUpstream()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        this.app = builder.Build();
        this.app.MapPost("/auth/v1/token", this.Token);
        this.app.MapGet("/rest/v1/notes", this.Notes);
        this.app.StartAsync().GetAwaiter().GetResult();
        this.Url = this.app.Urls.Single().TrimEnd('/');
    }

    public string Url { get; }

    public int RefreshCalls;

    public int NotesCalls;

    public bool RejectRefresh;

    public bool TransientRefresh;

    public string LastAccess { get; private set; } = "";

    public string LastRefresh { get; private set; } = "";

    public string LastNotesAuthorization { get; private set; } = "";

    public ConcurrentQueue<string> RefreshTokens { get; } = new();

    public async ValueTask DisposeAsync()
    {
        await this.app.StopAsync();
        await this.app.DisposeAsync();
    }

    private IResult Token(HttpContext http)
    {
        var body = new StreamReader(http.Request.Body).ReadToEndAsync().GetAwaiter().GetResult();
        var grant = http.Request.Query["grant_type"].ToString();
        if (grant == "password")
        {
            var email = JsonDocument.Parse(body).RootElement.GetProperty("email").GetString() ?? "";
            var user = email.StartsWith("b", StringComparison.OrdinalIgnoreCase) ? "user-b" : "user-a";
            return Results.Json(this.Issue(user, email));
        }

        if (grant == "refresh_token")
        {
            Interlocked.Increment(ref this.RefreshCalls);
            var refresh = JsonDocument.Parse(string.IsNullOrEmpty(body) ? "{}" : body).RootElement.GetProperty("refresh_token").GetString() ?? "";
            this.RefreshTokens.Enqueue(refresh);
            if (this.TransientRefresh)
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            if (this.RejectRefresh || !this.refreshOwner.TryGetValue(refresh, out var user))
                return Results.Json(new Dictionary<string, string> { ["error"] = "invalid_grant" }, statusCode: StatusCodes.Status400BadRequest);
            var email = user == "user-b" ? "b@example.test" : "a@example.test";
            return Results.Json(this.Issue(user, email));
        }

        return Results.NotFound(http.Request.Path.Value);
    }

    private IResult Notes(HttpContext http)
    {
        Interlocked.Increment(ref this.NotesCalls);
        this.LastNotesAuthorization = http.Request.Headers.Authorization.ToString();
        var token = this.LastNotesAuthorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? this.LastNotesAuthorization["Bearer ".Length..]
            : "";
        if (!this.accessOwner.TryGetValue(token, out var user))
            return Results.Json(Array.Empty<object>());
        return Results.Json(new[]
        {
            new Dictionary<string, object?>
            {
                ["id"] = user == "user-b" ? 2 : 1,
                ["body"] = user == "user-b" ? "seed-note-b" : "seed-note-a",
            },
        });
    }

    private Dictionary<string, object?> Issue(string user, string email)
    {
        var n = Interlocked.Increment(ref this.sequence);
        var access = $"access-{user}-{n}";
        var refresh = $"refresh-{user}-{n}";
        this.accessOwner[access] = user;
        this.refreshOwner[refresh] = user;
        this.LastAccess = access;
        this.LastRefresh = refresh;
        return new Dictionary<string, object?>
        {
            ["access_token"] = access,
            ["refresh_token"] = refresh,
            ["expires_in"] = 3600,
            ["token_type"] = "bearer",
            ["user"] = new Dictionary<string, object?>
            {
                ["id"] = user,
                ["email"] = email,
            },
        };
    }
}
