using Microsoft.JSInterop;

namespace Supabase.CookieSession;

/// <summary>
/// Interactive Server and WASM session. Both ask the browser to redeem the HttpOnly
/// refresh cookie. JavaScript returns the access token and never the refresh token.
/// Interactive Server then keeps that access token in circuit memory and opens the
/// SDK socket on the server. WASM keeps it in WASM memory and opens the SDK socket
/// in the browser (ClientWebSocket over the browser WebSocket).
/// </summary>
public sealed class BrowserCookieSession : ICookieSession, IAsyncDisposable
{
    private readonly IJSRuntime js;
    private readonly SupabaseMemoryOptions configured;
    private readonly MemoryAccessTokenStore store;
    private readonly SemaphoreSlim gate = new(1, 1);
    private SupabaseMemoryClient? sdk;

    public BrowserCookieSession(IJSRuntime js, SupabaseMemoryOptions configured, MemoryAccessTokenStore store)
    {
        this.js = js;
        this.configured = configured;
        this.store = store;
    }

    public SupabaseMemoryClient? Sdk => this.sdk;

    public SessionProfile? Profile => this.store.Profile;

    public bool HasAccessToken => !string.IsNullOrEmpty(this.store.AccessToken);

    public string RealtimeTransport => OperatingSystem.IsBrowser() ? "sdk-browser-websocket" : "sdk-server-websocket";

    public TimeSpan? TimeUntilRefresh(DateTimeOffset now) =>
        AccessTokenFreshness.DelayUntilRefresh(this.store.Profile?.ExpiresAt, now, this.configured.RefreshSkew);

    public Task SignInAsync(string email, string password, CancellationToken cancellationToken = default) =>
        this.ExchangeAsync("cookieSession.signIn", cancellationToken, email, password);

    public Task EnsureAsync(CancellationToken cancellationToken = default)
    {
        if (this.HasFreshToken())
            return Task.CompletedTask;
        return this.ExchangeAsync("cookieSession.acquire", cancellationToken, false);
    }

    public Task RotateAsync(CancellationToken cancellationToken = default) =>
        this.ExchangeAsync("cookieSession.acquire", cancellationToken, true);

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        var accessToken = this.store.AccessToken;
        this.store.Clear();
        this.sdk?.Stop();
        try
        {
            await this.js.InvokeAsync<AcquireResult>("cookieSession.signOut", cancellationToken, accessToken).ConfigureAwait(false);
        }
        catch (JSException exception)
        {
            throw new CookieSessionException($"The browser could not clear the session ({exception.Message}).");
        }
    }

    public async Task<IReadOnlyList<NoteRow>> GetNotesAsync(CancellationToken cancellationToken = default)
    {
        await this.EnsureAsync(cancellationToken).ConfigureAwait(false);
        var client = await this.ClientAsync(cancellationToken).ConfigureAwait(false);
        return await client.GetNotesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ISdkSubscription> SubscribeAsync(string topic, Func<string, Task> onEvent, CancellationToken cancellationToken = default)
    {
        await this.EnsureAsync(cancellationToken).ConfigureAwait(false);
        var client = await this.ClientAsync(cancellationToken).ConfigureAwait(false);
        return await client.SubscribeAsync(topic, onEvent, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Installs an access token that was redeemed on this HTTP request. Used by static SSR.</summary>
    public Task InstallAsync(string accessToken, SessionProfile profile, CancellationToken cancellationToken = default)
    {
        this.store.Set(accessToken, profile);
        this.sdk?.PushToken();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (this.sdk is not null)
            return this.sdk.DisposeAsync();
        return ValueTask.CompletedTask;
    }

    private bool HasFreshToken()
    {
        var profile = this.store.Profile;
        return this.HasAccessToken && profile is not null &&
               !AccessTokenFreshness.NeedsRefresh(profile.ExpiresAt, DateTimeOffset.UtcNow, this.configured.RefreshSkew);
    }

    private async Task ExchangeAsync(string function, CancellationToken cancellationToken, params object?[] args)
    {
        AcquireResult result;
        try
        {
            result = await this.js.InvokeAsync<AcquireResult>(function, cancellationToken, args).ConfigureAwait(false);
        }
        catch (JSException exception)
        {
            throw new CookieSessionException($"The browser token call failed ({exception.Message}).");
        }
        catch (InvalidOperationException)
        {
            throw new CookieSessionException("JavaScript interop is unavailable (static prerender). Wait until the page is interactive.", reauthenticate: false);
        }

        if (!result.Ok || string.IsNullOrEmpty(result.AccessToken) || result.User is null)
        {
            if (result.Status == 401)
            {
                this.store.Clear();
                this.sdk?.Stop();
                throw new CookieSessionException("The refresh cookie was rejected. Sign in again.", reauthenticate: true);
            }

            throw new CookieSessionException(result.Error ?? "The token endpoint did not return an access token.");
        }

        await this.ClientAsync(cancellationToken).ConfigureAwait(false);
        this.store.Set(result.AccessToken, new SessionProfile
        {
            UserId = result.User.Id,
            Email = result.User.Email,
            ExpiresAt = result.ExpiresAt,
        });
    }

    private async Task<SupabaseMemoryClient> ClientAsync(CancellationToken cancellationToken)
    {
        if (this.sdk is not null)
            return this.sdk;

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.sdk is not null)
                return this.sdk;

            var options = await this.ResolveOptionsAsync(cancellationToken).ConfigureAwait(false);
            this.sdk = new SupabaseMemoryClient(options, this.store);
            this.sdk.PushToken();
            return this.sdk;
        }
        finally
        {
            this.gate.Release();
        }
    }

    private async Task<SupabaseMemoryOptions> ResolveOptionsAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(this.configured.SupabaseUrl) && !string.IsNullOrWhiteSpace(this.configured.AnonKey))
            return this.configured;

        var project = await this.js.InvokeAsync<PublicProject>("cookieSession.publicConfig", cancellationToken).ConfigureAwait(false);
        return new SupabaseMemoryOptions
        {
            SupabaseUrl = project.Url,
            AnonKey = project.AnonKey,
            RefreshSkew = this.configured.RefreshSkew,
            WebSocketFactory = this.configured.WebSocketFactory,
            HttpClient = this.configured.HttpClient,
            HeartbeatInterval = this.configured.HeartbeatInterval,
        };
    }
}
