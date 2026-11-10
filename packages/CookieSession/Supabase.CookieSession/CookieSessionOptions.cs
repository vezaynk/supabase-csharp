namespace Supabase.CookieSession;

/// <summary>Host settings for the refresh-cookie companion. Access tokens are not configured here.</summary>
public sealed class CookieSessionOptions
{
    public string SupabaseUrl { get; set; } = "";

    /// <summary>Publishable or anon key sent as <c>apikey</c>. Never a service-role key.</summary>
    public string AnonKey { get; set; } = "";

    public bool SecureCookies { get; set; } = true;

    public SameSiteMode SameSite { get; set; } = SameSiteMode.Lax;

    public int CookieDays { get; set; } = 14;

    /// <summary>Ciphertext is split so a large refresh token still fits in cookie limits.</summary>
    public int ChunkChars { get; set; } = 3500;

    public string CookieName { get; set; } = "sb-refresh";

    /// <summary>
    /// How long this process remembers a just-completed redemption of one cookie generation
    /// so overlapping requests write the same new cookie. This is not durable storage:
    /// it dies with the process and is invisible to other instances.
    /// </summary>
    public TimeSpan SingleFlightTtl { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan RefreshSkew { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Extra origins allowed to call the token endpoints, besides the request host.</summary>
    public List<string> AllowedOrigins { get; set; } = [];
}
