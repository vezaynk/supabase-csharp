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

        var host = request.Host.Host;
        if (!uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase))
            return options.AllowedOrigins.Contains(uri.GetLeftPart(UriPartial.Authority), StringComparer.OrdinalIgnoreCase);

        if (uri.Scheme != request.Scheme)
            return false;

        var requestPort = request.Host.Port ?? (request.Scheme == "https" ? 443 : 80);
        return uri.Port == requestPort;
    }
}
