using System.Text.Json.Serialization;

namespace Supabase.CookieSession;

/// <summary>
/// JSON the browser receives from the token endpoint and hands to .NET.
/// There is intentionally no refresh-token property: extra JSON fields are ignored.
/// </summary>
public sealed class AcquireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("expires_at")]
    public DateTimeOffset ExpiresAt { get; set; }

    [JsonPropertyName("expires_in")]
    public long ExpiresIn { get; set; }

    [JsonPropertyName("user")]
    public PublicUser? User { get; set; }
}

/// <summary>Safe auth metadata returned next to an access token.</summary>
public sealed class PublicUser
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("email")]
    public string? Email { get; set; }
}

/// <summary>The publishable project coordinates embedded in the host page.</summary>
public sealed class PublicProject
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("anonKey")]
    public string AnonKey { get; set; } = "";
}
