namespace Supabase.CookieSession;

/// <summary>Decides when the companion, not the SDK timer, should redeem the refresh cookie.</summary>
public static class AccessTokenFreshness
{
    public static bool NeedsRefresh(DateTimeOffset expiresAt, DateTimeOffset now, TimeSpan skew) =>
        expiresAt - now <= skew;

    public static TimeSpan? DelayUntilRefresh(DateTimeOffset? expiresAt, DateTimeOffset now, TimeSpan skew)
    {
        if (expiresAt is null)
            return null;

        var delay = expiresAt.Value - skew - now;
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }
}
