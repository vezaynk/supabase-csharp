namespace Supabase.CookieSession;

/// <summary>
/// Server host session. A still-writable HTTP response (static SSR, before flush) redeems the
/// refresh cookie directly. Interactive Server cannot Set-Cookie on the circuit, so it asks the
/// browser to call the token endpoint and then keeps only the returned access token in circuit memory.
/// </summary>
public sealed class HybridCookieSession : ICookieSession, IAsyncDisposable
{
    private readonly BrowserCookieSession browser;
    private readonly RefreshAuthority authority;
    private readonly CookieJar jar;
    private readonly IHttpContextAccessor http;

    public HybridCookieSession(BrowserCookieSession browser, RefreshAuthority authority, CookieJar jar, IHttpContextAccessor http)
    {
        this.browser = browser;
        this.authority = authority;
        this.jar = jar;
        this.http = http;
    }

    public SupabaseMemoryClient? Sdk => this.browser.Sdk;

    public SessionProfile? Profile => this.browser.Profile;

    public bool HasAccessToken => this.browser.HasAccessToken;

    public string RealtimeTransport => this.browser.RealtimeTransport;

    public TimeSpan? TimeUntilRefresh(DateTimeOffset now) => this.browser.TimeUntilRefresh(now);

    public Task SignInAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        if (this.Writable(out var http))
            return this.SignInOnRequestAsync(http, email, password, cancellationToken);
        return this.browser.SignInAsync(email, password, cancellationToken);
    }

    public Task EnsureAsync(CancellationToken cancellationToken = default)
    {
        if (this.browser.HasAccessToken && this.browser.TimeUntilRefresh(DateTimeOffset.UtcNow) > TimeSpan.Zero)
            return Task.CompletedTask;
        if (this.Writable(out var http))
            return this.RedeemOnRequestAsync(http, cancellationToken);
        return this.browser.EnsureAsync(cancellationToken);
    }

    public Task RotateAsync(CancellationToken cancellationToken = default)
    {
        if (this.Writable(out var http))
            return this.RedeemOnRequestAsync(http, cancellationToken);
        return this.browser.RotateAsync(cancellationToken);
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        if (this.Writable(out var http))
        {
            var cookie = this.jar.Read(http.Request);
            this.jar.Clear(http.Response, cookie?.SessionId);
            if (!string.IsNullOrEmpty(cookie?.SessionId))
                this.authority.Forget(cookie.SessionId);
        }

        await this.browser.SignOutAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<NoteRow>> GetNotesAsync(CancellationToken cancellationToken = default) =>
        this.browser.GetNotesAsync(cancellationToken);

    public Task<ISdkSubscription> SubscribeAsync(string topic, Func<string, Task> onEvent, CancellationToken cancellationToken = default) =>
        this.browser.SubscribeAsync(topic, onEvent, cancellationToken);

    public ValueTask DisposeAsync() => this.browser.DisposeAsync();

    private bool Writable(out HttpContext http)
    {
        var current = this.http.HttpContext;
        if (current is { Response.HasStarted: false })
        {
            http = current;
            return true;
        }

        http = null!;
        return false;
    }

    private async Task SignInOnRequestAsync(HttpContext http, string email, string password, CancellationToken cancellationToken)
    {
        var grant = await this.authority.SignInAsync(email, password, (cookie, _) => Task.FromResult(this.jar.TryWrite(http.Response, cookie)), cancellationToken).ConfigureAwait(false);
        await this.InstallAsync(grant, cancellationToken).ConfigureAwait(false);
    }

    private async Task RedeemOnRequestAsync(HttpContext http, CancellationToken cancellationToken)
    {
        var cookie = this.jar.Read(http.Request);
        if (cookie is null)
            throw new CookieSessionException("No refresh cookie is present.", reauthenticate: true);
        var grant = await this.authority.RedeemAsync(cookie, (next, _) => Task.FromResult(this.jar.TryWrite(http.Response, next)), cancellationToken).ConfigureAwait(false);
        await this.InstallAsync(grant, cancellationToken).ConfigureAwait(false);
    }

    private Task InstallAsync(AccessGrant grant, CancellationToken cancellationToken) =>
        this.browser.InstallAsync(grant.AccessToken, new SessionProfile
        {
            UserId = grant.User.Id,
            Email = grant.User.Email,
            ExpiresAt = grant.ExpiresAt,
        }, cancellationToken);
}
