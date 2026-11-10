using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Supabase.CookieSession;

/// <summary>
/// Redeems a refresh cookie before the Razor endpoint renders, while the response
/// has not started. Streaming render must not be enabled on these pages: a started
/// response cannot receive <c>Set-Cookie</c>, and this bootstrap then leaves the
/// request signed out instead of writing the cookie late.
/// </summary>
public sealed class PrerenderSessionBootstrap
{
    private readonly HybridCookieSession session;
    private readonly ILogger<PrerenderSessionBootstrap> logger;

    public PrerenderSessionBootstrap(HybridCookieSession session, ILogger<PrerenderSessionBootstrap> logger)
    {
        this.session = session;
        this.logger = logger;
    }

    public static bool ShouldRun(HttpRequest request)
    {
        if (request.Method is not ("GET" or "HEAD"))
            return false;

        var path = request.Path.Value ?? "";
        if (path.StartsWith("/auth", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/_", StringComparison.OrdinalIgnoreCase))
            return false;
        if (Path.HasExtension(path))
            return false;

        var accept = request.Headers.Accept.ToString();
        if (accept.Length == 0)
            return true;
        return accept.Contains("text/html", StringComparison.OrdinalIgnoreCase)
            || accept.Contains("*/*", StringComparison.OrdinalIgnoreCase);
    }

    public async Task EstablishAsync(HttpContext http, CancellationToken cancellationToken = default)
    {
        if (!ShouldRun(http.Request))
            return;

        if (http.Response.HasStarted)
        {
            this.logger.LogWarning("Skipped prerender session redeem because the response has already started.");
            await this.session.NoteResponseStartedAsync().ConfigureAwait(false);
            return;
        }

        try
        {
            await this.session.EstablishAsync(http, cancellationToken).ConfigureAwait(false);
        }
        catch (CookieSessionException exception)
        {
            this.logger.LogInformation("Prerender session was not established ({Reason}).", exception.Message);
        }
    }
}

/// <summary>Registers the prerender redeem ahead of endpoint execution.</summary>
public static class PrerenderSessionApplicationBuilderExtensions
{
    public static IApplicationBuilder UsePrerenderSession(this IApplicationBuilder app) =>
        app.Use(async (http, next) =>
        {
            if (PrerenderSessionBootstrap.ShouldRun(http.Request))
            {
                var bootstrap = http.RequestServices.GetRequiredService<PrerenderSessionBootstrap>();
                await bootstrap.EstablishAsync(http).ConfigureAwait(false);
            }

            await next().ConfigureAwait(false);
        });
}
