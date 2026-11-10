namespace Supabase.CookieSession;

/// <summary>Rejects cross-site calls to the cookie endpoints. This is not an open proxy.</summary>
public static class OriginGuard
{
    public static bool Allows(HttpRequest request, CookieSessionOptions options)
    {
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin))
            origin = request.Headers.Referer.ToString();
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            return false;

        // An allow-list entry is an exact scheme and authority. It overrides a scheme or host
        // mismatch (HTTPS at the browser, HTTP behind a terminator) and does not allow other origins.
        var authority = uri.GetLeftPart(UriPartial.Authority);
        if (options.AllowedOrigins.Contains(authority, StringComparer.OrdinalIgnoreCase))
            return true;

        if (!uri.Host.Equals(request.Host.Host, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!uri.Scheme.Equals(request.Scheme, StringComparison.OrdinalIgnoreCase))
            return false;

        var requestPort = request.Host.Port ?? (request.Scheme == "https" ? 443 : 80);
        return uri.Port == requestPort;
    }
}
