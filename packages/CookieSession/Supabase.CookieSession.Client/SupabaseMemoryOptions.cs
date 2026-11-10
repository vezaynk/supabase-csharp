using Supabase.Realtime.Sockets;

namespace Supabase.CookieSession;

/// <summary>Public project settings and test seams for the in-memory SDK client.</summary>
public sealed class SupabaseMemoryOptions
{
    public string SupabaseUrl { get; set; } = "";

    /// <summary>Publishable or anon key. Never a service-role key.</summary>
    public string AnonKey { get; set; } = "";

    public TimeSpan RefreshSkew { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>When set, replaces the SDK socket. Tests use this. Production leaves it null.</summary>
    public IWebSocketFactory? WebSocketFactory { get; set; }

    public HttpClient? HttpClient { get; set; }

    public TimeSpan? HeartbeatInterval { get; set; }
}
