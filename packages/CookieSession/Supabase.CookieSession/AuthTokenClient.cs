using Supabase.Gotrue;
using Supabase.Gotrue.Exceptions;

namespace Supabase.CookieSession;

/// <summary>Server-side GoTrue calls. The browser never sees the refresh token these methods exchange.</summary>
public interface IAuthTokenClient
{
    Task<IssuedTokens> SignInAsync(string email, string password, CancellationToken cancellationToken);

    Task<IssuedTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken);

    Task SignOutAsync(string? accessToken, CancellationToken cancellationToken);
}

/// <summary>GoTrue rejected the refresh token. The cookie must not be rewritten with an older value.</summary>
public sealed class AuthRejectedException : Exception
{
    public AuthRejectedException() : base("The auth server rejected the refresh token.")
    {
    }
}

/// <summary>GoTrue or the network failed without proving the refresh token was spent.</summary>
public sealed class AuthTransientException : Exception
{
    public AuthTransientException() : base("The auth server could not refresh the session.")
    {
    }
}

/// <summary>Uses the shipping GoTrue <see cref="Api"/> client. Auto-refresh on a GoTrue <c>Client</c> is not started.</summary>
public sealed class GotrueAuthTokenClient : IAuthTokenClient
{
    private readonly CookieSessionOptions options;
    private readonly HttpClient? httpClient;
    private Api? api;

    public GotrueAuthTokenClient(CookieSessionOptions options, HttpClient? httpClient = null)
    {
        this.options = options;
        this.httpClient = httpClient;
    }

    public async Task<IssuedTokens> SignInAsync(string email, string password, CancellationToken cancellationToken)
    {
        try
        {
            var session = await this.Api().SignInWithEmail(email, password).WaitAsync(cancellationToken).ConfigureAwait(false);
            return ToTokens(session);
        }
        catch (GotrueException ex) when (IsRejected(ex))
        {
            throw new AuthRejectedException();
        }
        catch (GotrueException)
        {
            throw new AuthTransientException();
        }
    }

    public async Task<IssuedTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        try
        {
            // GoTrue's refresh route accepts the publishable key as the bearer when the caller
            // has no access token yet. The refresh token travels in the body, server to server.
            var session = await this.Api().RefreshAccessToken(this.options.AnonKey, refreshToken).WaitAsync(cancellationToken).ConfigureAwait(false);
            return ToTokens(session);
        }
        catch (GotrueException ex) when (IsRejected(ex))
        {
            throw new AuthRejectedException();
        }
        catch (GotrueException)
        {
            throw new AuthTransientException();
        }
    }

    public async Task SignOutAsync(string? accessToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(accessToken))
            return;
        try
        {
            await this.Api().SignOutAsync(accessToken, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (GotrueException)
        {
            // Local cookie clearing still proceeds. An access JWT is not revoked globally by a failed logout call.
        }
    }

    private Api Api()
    {
        if (this.api is not null)
            return this.api;
        if (string.IsNullOrWhiteSpace(this.options.SupabaseUrl))
            throw new CookieSessionException("CookieSession SupabaseUrl is not configured.");
        var root = this.options.SupabaseUrl.TrimEnd('/');
        this.api = new Api($"{root}/auth/v1", new Dictionary<string, string>
        {
            ["apikey"] = this.options.AnonKey,
        }, this.httpClient);
        return this.api;
    }

    private static bool IsRejected(GotrueException exception) =>
        exception.StatusCode is >= 400 and < 500 and not 429;

    private static IssuedTokens ToTokens(Session? session)
    {
        if (session is null || string.IsNullOrEmpty(session.AccessToken) || string.IsNullOrEmpty(session.RefreshToken))
            throw new AuthRejectedException();

        var expiresIn = session.ExpiresIn <= 0 ? 3600 : session.ExpiresIn;
        return new IssuedTokens
        {
            AccessToken = session.AccessToken,
            RefreshToken = session.RefreshToken,
            ExpiresIn = expiresIn,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
            UserId = session.User?.Id ?? "",
            Email = session.User?.Email,
        };
    }
}
