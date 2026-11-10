using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Supabase.CookieSession.Tests.Support;

internal sealed class FakeAuth : IAuthTokenClient
{
    public int SignInCalls { get; private set; }

    public int RefreshCalls { get; set; }

    public int SignOutCalls { get; private set; }

    public string? LastRefreshToken { get; private set; }

    public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Hold { get; set; }

    public bool Reject { get; set; }

    public IssuedTokens Next { get; set; } = new()
    {
        AccessToken = "access-secret-value",
        RefreshToken = "refresh-secret-value",
        ExpiresIn = 3600,
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        UserId = "user-a",
        Email = "a@example.test",
    };

    public async Task<IssuedTokens> SignInAsync(string email, string password, CancellationToken cancellationToken)
    {
        this.SignInCalls++;
        if (this.Reject)
            throw new AuthRejectedException();
        return await this.MaybeHold().ConfigureAwait(false);
    }

    public async Task<IssuedTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        this.RefreshCalls++;
        this.LastRefreshToken = refreshToken;
        if (this.Reject)
            throw new AuthRejectedException();
        return await this.MaybeHold().ConfigureAwait(false);
    }

    public Task SignOutAsync(string? accessToken, CancellationToken cancellationToken)
    {
        this.SignOutCalls++;
        return Task.CompletedTask;
    }

    private async Task<IssuedTokens> MaybeHold()
    {
        if (this.Hold && this.RefreshCalls > 0)
            await this.Gate.Task.ConfigureAwait(false);
        return this.Next;
    }
}

internal sealed class RecordingHandler : HttpMessageHandler
{
    private readonly string body;

    public RecordingHandler(string body) => this.body = body;

    public string? LastAuthorization { get; private set; }

    public string? LastApiKey { get; private set; }

    public Uri? LastUri { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        this.LastUri = request.RequestUri;
        this.LastAuthorization = request.Headers.Authorization?.ToString();
        if (request.Headers.TryGetValues("apikey", out var keys))
            this.LastApiKey = keys.Single();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(this.body, Encoding.UTF8, "application/json"),
            RequestMessage = request,
        };
        return Task.FromResult(response);
    }
}

internal sealed class ListLoggerProvider : ILoggerProvider
{
    public ListLogger Logger { get; } = new();

    public ILogger CreateLogger(string categoryName) => this.Logger;

    public void Dispose()
    {
    }
}

internal sealed class ListLogger : ILogger
{
    public List<string> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (this.Lines)
            this.Lines.Add(formatter(state, exception));
    }
}

internal static class Waits
{
    public static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("condition was not met");
            await Task.Delay(20);
        }
    }
}
