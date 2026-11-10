namespace Supabase.CookieSession;

/// <summary>The only durable session state: an encrypted refresh token plus a generation counter.</summary>
public sealed class RefreshCookie
{
    public string SessionId { get; set; } = "";

    public long Generation { get; set; }

    public string RefreshToken { get; set; } = "";

    public string UserId { get; set; } = "";

    public string? Email { get; set; }

    public RefreshCookie Next(IssuedTokens tokens) => new()
    {
        SessionId = this.SessionId,
        Generation = this.Generation + 1,
        RefreshToken = tokens.RefreshToken,
        UserId = string.IsNullOrEmpty(tokens.UserId) ? this.UserId : tokens.UserId,
        Email = tokens.Email ?? this.Email,
    };

    public static RefreshCookie Issue(IssuedTokens tokens) => new()
    {
        SessionId = Guid.NewGuid().ToString("N"),
        Generation = 1,
        RefreshToken = tokens.RefreshToken,
        UserId = tokens.UserId,
        Email = tokens.Email,
    };
}

/// <summary>Tokens returned by GoTrue. The refresh token stays on the server; the access token is the response.</summary>
public sealed class IssuedTokens
{
    public string AccessToken { get; set; } = "";

    public string RefreshToken { get; set; } = "";

    public DateTimeOffset ExpiresAt { get; set; }

    public long ExpiresIn { get; set; }

    public string UserId { get; set; } = "";

    public string? Email { get; set; }

    public AccessGrant ToGrant() => new()
    {
        AccessToken = this.AccessToken,
        ExpiresAt = this.ExpiresAt,
        ExpiresIn = this.ExpiresIn,
        User = new PublicUser { Id = this.UserId, Email = this.Email },
    };
}

/// <summary>Browser-visible token response. Serializing it cannot emit a refresh token.</summary>
public sealed class AccessGrant
{
    [System.Text.Json.Serialization.JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("expires_at")]
    public DateTimeOffset ExpiresAt { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("expires_in")]
    public long ExpiresIn { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("token_type")]
    public string TokenType { get; set; } = "bearer";

    [System.Text.Json.Serialization.JsonPropertyName("user")]
    public PublicUser User { get; set; } = new();
}
