namespace Supabase.CookieSession;

/// <summary>
/// What the Blazor page talks to. The access token lives in memory on the runtime that
/// owns the SDK client. The refresh token never appears on this type.
/// </summary>
public interface ICookieSession
{
    /// <summary>Safe profile for the in-memory access token, or null when signed out.</summary>
    SessionProfile? Profile { get; }

    /// <summary>True when this runtime currently holds an access token in memory.</summary>
    bool HasAccessToken { get; }

    /// <summary>
    /// <c>sdk-browser-websocket</c> on WASM (the SDK's browser <c>ClientWebSocket</c>) or
    /// <c>sdk-server-websocket</c> on Interactive Server. Neither value is a same-origin relay.
    /// </summary>
    string RealtimeTransport { get; }

    /// <summary>How long until <see cref="RotateAsync"/> should run, or null when signed out.</summary>
    TimeSpan? TimeUntilRefresh(DateTimeOffset now);

    Task SignInAsync(string email, string password, CancellationToken cancellationToken = default);

    /// <summary>Loads an access token if memory does not already hold a fresh one.</summary>
    Task EnsureAsync(CancellationToken cancellationToken = default);

    /// <summary>Redeems the refresh cookie and installs the new access token.</summary>
    Task RotateAsync(CancellationToken cancellationToken = default);

    Task SignOutAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NoteRow>> GetNotesAsync(CancellationToken cancellationToken = default);

    Task<ISdkSubscription> SubscribeAsync(string topic, Func<string, Task> onEvent, CancellationToken cancellationToken = default);

    /// <summary>Raised when another tab signals sign-out. The signal carries no token.</summary>
    event Action? SessionEnded;

    /// <summary>Listens for the browser sign-out signal. Safe to call more than once.</summary>
    Task WatchSignOutAsync();
}

/// <summary>A native SDK channel subscription. Disposing it leaves that channel.</summary>
public interface ISdkSubscription : IAsyncDisposable
{
    string Transport { get; }

    string Topic { get; }
}

/// <summary>Identity and expiry. No tokens.</summary>
public sealed class SessionProfile
{
    public string UserId { get; set; } = "";

    public string? Email { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>One row from the user's notes table, read with the user JWT.</summary>
public sealed class NoteRow
{
    public long Id { get; set; }

    public string? Body { get; set; }
}

/// <summary>Failure of the cookie session companion. The message never includes a token.</summary>
public sealed class CookieSessionException : Exception
{
    public CookieSessionException(string message, bool reauthenticate = false) : base(message) =>
        this.Reauthenticate = reauthenticate;

    /// <summary>True when the refresh cookie was rejected and the caller must sign in again.</summary>
    public bool Reauthenticate { get; }
}
