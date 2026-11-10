using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Supabase.CookieSession;

public enum CookieWriteResult
{
    Written,
    Idempotent,
    Rejected,
}

/// <summary>
/// Encrypts the refresh token into chunked HttpOnly cookies and refuses to let an older
/// generation, or a different token at the same generation, overwrite a newer write in this process.
/// The high-water mark is a generation and a hash, not a copy of the token. It is lost on restart;
/// the cookie is the durable copy.
/// </summary>
public sealed class CookieJar
{
    public const string ProtectorPurpose = "Supabase.CookieSession.Refresh.v1";

    private readonly IDataProtector protector;
    private readonly CookieSessionOptions options;
    private readonly object gate = new();
    private readonly Dictionary<string, GenerationMark> highWater = new(StringComparer.Ordinal);
    private readonly HashSet<string> revoked = new(StringComparer.Ordinal);

    public CookieJar(IDataProtectionProvider provider, CookieSessionOptions options)
    {
        this.protector = provider.CreateProtector(ProtectorPurpose);
        this.options = options;
    }

    public CookieWriteResult TryWrite(HttpResponse response, RefreshCookie cookie)
    {
        ArgumentNullException.ThrowIfNull(cookie);
        if (string.IsNullOrEmpty(cookie.SessionId) || string.IsNullOrEmpty(cookie.RefreshToken))
            return CookieWriteResult.Rejected;

        lock (this.gate)
        {
            if (this.revoked.Contains(cookie.SessionId))
                return CookieWriteResult.Rejected;

            var hash = Hash(cookie.RefreshToken);
            var result = CookieWriteResult.Written;
            if (this.highWater.TryGetValue(cookie.SessionId, out var mark))
            {
                if (cookie.Generation < mark.Generation)
                    return CookieWriteResult.Rejected;
                if (cookie.Generation == mark.Generation)
                {
                    if (!string.Equals(mark.Hash, hash, StringComparison.Ordinal))
                        return CookieWriteResult.Rejected;
                    result = CookieWriteResult.Idempotent;
                }
            }

            this.Append(response, cookie);
            this.highWater[cookie.SessionId] = new GenerationMark(cookie.Generation, hash);
            return result;
        }
    }

    public RefreshCookie? Read(HttpRequest request)
    {
        var countText = request.Cookies[this.CountName];
        if (!int.TryParse(countText, out var count) || count <= 0)
            return null;

        var builder = new StringBuilder();
        for (var index = 0; index < count; index++)
        {
            var chunk = request.Cookies[this.ChunkName(index)];
            if (string.IsNullOrEmpty(chunk))
                return null;
            builder.Append(chunk);
        }

        try
        {
            var json = this.protector.Unprotect(builder.ToString());
            return JsonSerializer.Deserialize<RefreshCookie>(json);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Clear(HttpResponse response, string? sessionId)
    {
        lock (this.gate)
        {
            if (!string.IsNullOrEmpty(sessionId))
                this.revoked.Add(sessionId);
        }

        this.Delete(response, this.CountName);
        for (var index = 0; index < 16; index++)
            this.Delete(response, this.ChunkName(index));
    }

    /// <summary>
    /// True when this process has not already published a newer generation for the session.
    /// A failed redeem of an older cookie must not delete that newer cookie.
    /// </summary>
    public bool StillCurrent(string sessionId, long generation)
    {
        lock (this.gate)
        {
            if (this.revoked.Contains(sessionId))
                return false;
            if (!this.highWater.TryGetValue(sessionId, out var mark))
                return true;
            return mark.Generation <= generation;
        }
    }

    public bool IsRevoked(string sessionId)
    {
        lock (this.gate)
            return this.revoked.Contains(sessionId);
    }

    private void Append(HttpResponse response, RefreshCookie cookie)
    {
        var json = JsonSerializer.Serialize(cookie);
        var protectedText = this.protector.Protect(json);
        var size = Math.Max(32, this.options.ChunkChars);
        var count = Math.Max(1, (int) Math.Ceiling(protectedText.Length / (double) size));
        for (var index = 0; index < count; index++)
        {
            var start = index * size;
            var length = Math.Min(size, protectedText.Length - start);
            this.AppendOne(response, this.ChunkName(index), protectedText.Substring(start, length));
        }

        this.AppendOne(response, this.CountName, count.ToString());
    }

    private void AppendOne(HttpResponse response, string name, string value) =>
        response.Cookies.Append(name, value, new CookieOptions
        {
            HttpOnly = true,
            Secure = this.options.SecureCookies,
            SameSite = this.options.SameSite,
            Path = "/",
            IsEssential = true,
            MaxAge = TimeSpan.FromDays(Math.Max(1, this.options.CookieDays)),
        });

    private void Delete(HttpResponse response, string name) =>
        response.Cookies.Delete(name, new CookieOptions
        {
            HttpOnly = true,
            Secure = this.options.SecureCookies,
            SameSite = this.options.SameSite,
            Path = "/",
        });

    private string CountName => this.options.CookieName + "-n";

    private string ChunkName(int index) => index == 0 ? this.options.CookieName : $"{this.options.CookieName}.{index}";

    private static string Hash(string refreshToken)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken));
        return Convert.ToHexString(bytes);
    }

    private readonly record struct GenerationMark(long Generation, string Hash);
}
