using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Supabase.CookieSession;

/// <summary>
/// Browser-facing token endpoints. They read the refresh cookie, redeem it server-side,
/// Set-Cookie the rotated refresh token, and return only an access token plus safe metadata.
/// </summary>
public static class CookieSessionEndpoints
{
    public static IEndpointRouteBuilder MapCookieSessionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/auth/session");
        group.MapGet("/csrf", Csrf);
        group.MapGet("/config", Config);
        group.MapPost("/signin", SignIn);
        group.MapPost("/token", Token);
        group.MapPost("/signout", SignOut);
        return endpoints;
    }

    private static IResult Csrf(HttpContext http, IAntiforgery antiforgery, CookieSessionOptions options)
    {
        if (!OriginGuard.Allows(http.Request, options))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        var tokens = antiforgery.GetAndStoreTokens(http);
        NoStore(http);
        return Results.Json(new { token = tokens.RequestToken });
    }

    private static IResult Config(HttpContext http, CookieSessionOptions options)
    {
        if (!OriginGuard.Allows(http.Request, options))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        NoStore(http);
        return Results.Json(new PublicProject { Url = options.SupabaseUrl, AnonKey = options.AnonKey });
    }

    private static async Task<IResult> SignIn(HttpContext http, SignInRequest body, IAntiforgery antiforgery, RefreshAuthority authority, CookieJar jar, CookieSessionOptions options, ILoggerFactory logger, CancellationToken cancellationToken)
    {
        if (!await AllowedAsync(http, antiforgery, options).ConfigureAwait(false))
            return Results.StatusCode(http.Response.StatusCode == 0 ? StatusCodes.Status403Forbidden : http.Response.StatusCode);
        if (string.IsNullOrWhiteSpace(body.Email) || string.IsNullOrWhiteSpace(body.Password))
            return Results.BadRequest();

        try
        {
            var grant = await authority.SignInAsync(body.Email, body.Password, (cookie, token) => Task.FromResult(jar.TryWrite(http.Response, cookie)), cancellationToken).ConfigureAwait(false);
            logger.CreateLogger("Supabase.CookieSession").LogInformation("Signed in a browser session.");
            return Grant(http, grant);
        }
        catch (AuthRejectedException)
        {
            return Results.Json(new { error = "sign-in failed" }, statusCode: StatusCodes.Status401Unauthorized);
        }
        catch (CookieSessionException)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> Token(HttpContext http, IAntiforgery antiforgery, RefreshAuthority authority, CookieJar jar, CookieSessionOptions options, ILoggerFactory logger, CancellationToken cancellationToken)
    {
        if (!await AllowedAsync(http, antiforgery, options).ConfigureAwait(false))
            return Results.StatusCode(http.Response.StatusCode == 0 ? StatusCodes.Status403Forbidden : http.Response.StatusCode);

        var cookie = jar.Read(http.Request);
        if (cookie is null || jar.IsRevoked(cookie.SessionId))
            return Results.Unauthorized();

        // A newer cookie is already in the browser. Do not call GoTrue with the stale refresh
        // token, and do not tell this tab to sign out. 409 asks it to retry with the current cookie.
        if (!jar.StillCurrent(cookie.SessionId, cookie.Generation))
        {
            NoStore(http);
            return Results.StatusCode(StatusCodes.Status409Conflict);
        }

        try
        {
            var grant = await authority.RedeemAsync(cookie, (next, _) => Task.FromResult(jar.TryWrite(http.Response, next)), cancellationToken).ConfigureAwait(false);
            logger.CreateLogger("Supabase.CookieSession").LogInformation("Redeemed session {SessionId} at generation {Generation}.", cookie.SessionId, cookie.Generation + 1);
            return Grant(http, grant);
        }
        catch (CookieSessionException ex) when (ex.Reauthenticate)
        {
            // A newer generation already stored in this process must keep its cookie.
            if (jar.StillCurrent(cookie.SessionId, cookie.Generation))
            {
                jar.Clear(http.Response, cookie.SessionId);
                authority.Forget(cookie.SessionId);
            }

            NoStore(http);
            return Results.Unauthorized();
        }
        catch (CookieSessionException)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> SignOut(HttpContext http, IAntiforgery antiforgery, IAuthTokenClient auth, RefreshAuthority authority, CookieJar jar, CookieSessionOptions options, CancellationToken cancellationToken)
    {
        if (!await AllowedAsync(http, antiforgery, options).ConfigureAwait(false))
            return Results.StatusCode(http.Response.StatusCode == 0 ? StatusCodes.Status403Forbidden : http.Response.StatusCode);

        var cookie = jar.Read(http.Request);
        var sessionId = cookie?.SessionId;
        jar.Clear(http.Response, sessionId);
        if (!string.IsNullOrEmpty(sessionId))
            authority.Forget(sessionId);

        var accessToken = Bearer(http.Request);
        await auth.SignOutAsync(accessToken, cancellationToken).ConfigureAwait(false);
        NoStore(http);
        if (http.Request.HasFormContentType)
            return Results.LocalRedirect(LocalReturn(http.Request.Form["returnUrl"].ToString()));
        return Results.NoContent();
    }

    private static string LocalReturn(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 200)
            return "/";
        if (!value.StartsWith('/') || value.StartsWith("//", StringComparison.Ordinal) || value.Contains('\\') || value.Contains("://", StringComparison.Ordinal))
            return "/";
        return value;
    }

    private static async Task<bool> AllowedAsync(HttpContext http, IAntiforgery antiforgery, CookieSessionOptions options)
    {
        if (!OriginGuard.Allows(http.Request, options))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return false;
        }

        try
        {
            await antiforgery.ValidateRequestAsync(http).ConfigureAwait(false);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return false;
        }
    }

    private static IResult Grant(HttpContext http, AccessGrant grant)
    {
        NoStore(http);
        return Results.Json(grant);
    }

    private static void NoStore(HttpContext http)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";
    }

    private static string? Bearer(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return header[prefix.Length..].Trim();
        return null;
    }

}

/// <summary>Email and password posted by the browser. The response never echoes them.</summary>
public sealed class SignInRequest
{
    public string? Email { get; set; }

    public string? Password { get; set; }
}
