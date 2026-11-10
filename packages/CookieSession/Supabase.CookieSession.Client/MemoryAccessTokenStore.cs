namespace Supabase.CookieSession;

/// <summary>
/// Process (or WASM) memory for one circuit or one browser runtime. Not localStorage,
/// and not shared across users. The refresh token is never stored here.
/// </summary>
public sealed class MemoryAccessTokenStore
{
    private readonly object gate = new();
    private string? accessToken;
    private SessionProfile? profile;

    public string? AccessToken
    {
        get
        {
            lock (this.gate)
                return this.accessToken;
        }
    }

    public SessionProfile? Profile
    {
        get
        {
            lock (this.gate)
                return this.profile;
        }
    }

    public event Action? Changed;

    public void Set(string accessToken, SessionProfile profile)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessToken);
        ArgumentNullException.ThrowIfNull(profile);
        lock (this.gate)
        {
            this.accessToken = accessToken;
            this.profile = profile;
        }

        this.Changed?.Invoke();
    }

    public void Clear()
    {
        lock (this.gate)
        {
            this.accessToken = null;
            this.profile = null;
        }

        this.Changed?.Invoke();
    }
}
