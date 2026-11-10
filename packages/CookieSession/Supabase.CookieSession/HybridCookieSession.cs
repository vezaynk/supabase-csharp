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
    private readonly RequestSession request;
    private readonly IHttpContextAccessor http;

    public HybridCookieSession(BrowserCookieSession browser, RefreshAuthority authority, CookieJar jar, RequestSession request, IHttpContextAccessor http)
    {
        this.browser = browser;
        this.authority = authority;
        this.jar = jar;
        this.request = request;
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
        if (this.request.Attempted)
            return this.ApplyPreparedAsync(cancellationToken);
        if (this.browser.HasAccessToken && this.browser.TimeUntilRefresh(DateTimeOffset.UtcNow) > TimeSpan.Zero)
            return Task.CompletedTask;
        if (this.Writable(out var http))
            return this.RedeemOnRequestAsync(http, cancellationToken);
        return this.browser.EnsureAsync(cancellationToken);
    }

    /// <summary>
    /// Redeems the refresh cookie for this document request and writes <c>Set-Cookie</c>
    /// before the caller renders. A second call in the same request does not redeem again.
    /// </summary>
    public async Task EstablishAsync(HttpContext http, CancellationToken cancellationToken = default)
    {
        if (this.request.Attempted)
            return;
        if (http.Response.HasStarted)
        {
            this.request.MarkAnonymous();
            return;
        }

        if (!this.jar.HasCookie(http.Request))
        {
            this.request.MarkAnonymous();
            return;
        }

        var cookie = this.jar.Read(http.Request);
        if (cookie is null || this.jar.IsRevoked(cookie.SessionId))
        {
            this.jar.Clear(http.Response, cookie?.SessionId);
            this.request.MarkAnonymous();
            return;
        }

        try
        {
            var grant = await this.authority.RedeemAsync(cookie, (next, _) =>
            {
                if (http.Response.HasStarted)
                    return Task.FromResult(CookieWriteResult.Rejected);
                return Task.FromResult(this.jar.TryWrite(http.Response, next));
            }, cancellationToken).ConfigureAwait(false);
            await this.AdoptAsync(grant, cancellationToken).ConfigureAwait(false);
        }
        catch (CookieSessionException exception) when (exception.Reauthenticate)
        {
            if (!http.Response.HasStarted && this.jar.StillCurrent(cookie.SessionId, cookie.Generation))
            {
                this.jar.Clear(http.Response, cookie.SessionId);
                this.authority.Forget(cookie.SessionId);
            }

            this.request.MarkAnonymous();
        }
        catch (CookieSessionException)
        {
            this.request.MarkAnonymous();
        }
    }

    /// <summary>The response already started, so this request stays signed out and does not rotate.</summary>
    public Task NoteResponseStartedAsync()
    {
        if (!this.request.Attempted)
            this.request.MarkAnonymous();
        return Task.CompletedTask;
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

    public async Task<IReadOnlyList<NoteRow>> GetNotesAsync(CancellationToken cancellationToken = default)
    {
        await this.EnsureAsync(cancellationToken).ConfigureAwait(false);
        if (!this.HasAccessToken)
            return Array.Empty<NoteRow>();
        return await this.browser.GetNotesAsync(cancellationToken).ConfigureAwait(false);
    }

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

    private Task ApplyPreparedAsync(CancellationToken cancellationToken)
    {
        if (this.request.AccessToken is null || this.request.Profile is null || this.browser.HasAccessToken)
            return Task.CompletedTask;
        return this.browser.InstallAsync(this.request.AccessToken, this.request.Profile, cancellationToken);
    }

    private Task AdoptAsync(AccessGrant grant, CancellationToken cancellationToken)
    {
        var profile = new SessionProfile
        {
            UserId = grant.User.Id,
            Email = grant.User.Email,
            ExpiresAt = grant.ExpiresAt,
        };
        this.request.Adopt(grant.AccessToken, profile);
        return this.browser.InstallAsync(grant.AccessToken, profile, cancellationToken);
    }

    private Task InstallAsync(AccessGrant grant, CancellationToken cancellationToken) =>
        this.AdoptAsync(grant, cancellationToken);
}
