namespace Supabase.CookieSession;

/// <summary>
/// Access token for the current HTTP request only. The prerender middleware fills it.
/// A circuit or WASM runtime gets a new scope, so this does not survive into interactivity.
/// </summary>
public sealed class RequestSession
{
    public bool Attempted { get; private set; }

    public string? AccessToken { get; private set; }

    public SessionProfile? Profile { get; private set; }

    public void Adopt(string accessToken, SessionProfile profile)
    {
        this.Attempted = true;
        this.AccessToken = accessToken;
        this.Profile = profile;
    }

    public void MarkAnonymous()
    {
        this.Attempted = true;
        this.AccessToken = null;
        this.Profile = null;
    }
}
