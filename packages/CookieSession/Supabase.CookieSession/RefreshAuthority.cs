namespace Supabase.CookieSession;

/// <summary>
/// One in-process refresh authority per browser session id.
/// Concurrent redeemers of the same cookie generation share a single GoTrue call and then
/// all write that same new cookie. The result is remembered only for <see cref="CookieSessionOptions.SingleFlightTtl"/>.
/// That memory holds the new refresh token so a waiter can Set-Cookie it. It is not a database,
/// it is not shared across servers, and a crash drops it. If GoTrue rotated the token and the
/// process dies before Set-Cookie, the old cookie is dead and the user must sign in again.
/// Two instances can still both redeem. Supabase's refresh-token reuse window may make the
/// second call return the same pair; if it does not, the loser gets a rejection and must not
/// write an older cookie. This class does not make that distributed race disappear.
/// </summary>
public sealed class RefreshAuthority
{
    private readonly IAuthTokenClient tokens;
    private readonly CookieSessionOptions options;
    private readonly object registryGate = new();
    private readonly Dictionary<string, SemaphoreSlim> gates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Flight> flights = new(StringComparer.Ordinal);

    public RefreshAuthority(IAuthTokenClient tokens, CookieSessionOptions options)
    {
        this.tokens = tokens;
        this.options = options;
    }

    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    public async Task<AccessGrant> SignInAsync(string email, string password, Func<RefreshCookie, CancellationToken, Task<CookieWriteResult>> write, CancellationToken cancellationToken)
    {
        var issued = await this.tokens.SignInAsync(email, password, cancellationToken).ConfigureAwait(false);
        var cookie = RefreshCookie.Issue(issued);
        var wrote = await write(cookie, cancellationToken).ConfigureAwait(false);
        if (wrote == CookieWriteResult.Rejected)
            throw new CookieSessionException("The new refresh cookie was not stored.");
        return issued.ToGrant();
    }

    public async Task<AccessGrant> RedeemAsync(RefreshCookie presented, Func<RefreshCookie, CancellationToken, Task<CookieWriteResult>> write, CancellationToken cancellationToken)
    {
        var gate = this.Gate(presented.SessionId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            this.Sweep();
            var key = FlightKey(presented);
            Flight? recent;
            lock (this.registryGate)
                recent = this.flights.TryGetValue(key, out var found) ? found : null;
            if (recent is not null)
            {
                var wrote = await write(recent.Cookie, cancellationToken).ConfigureAwait(false);
                if (wrote == CookieWriteResult.Rejected)
                    throw new CookieSessionException("A newer refresh cookie is already stored.", reauthenticate: false);
                return recent.Grant;
            }

            IssuedTokens issued;
            try
            {
                issued = await this.tokens.RefreshAsync(presented.RefreshToken, cancellationToken).ConfigureAwait(false);
            }
            catch (AuthRejectedException)
            {
                throw new CookieSessionException("The refresh cookie was rejected.", reauthenticate: true);
            }
            catch (AuthTransientException)
            {
                throw new CookieSessionException("The auth server could not refresh the session.");
            }

            var next = presented.Next(issued);
            var grant = issued.ToGrant();
            var result = await write(next, cancellationToken).ConfigureAwait(false);
            if (result == CookieWriteResult.Rejected)
                throw new CookieSessionException("The rotated refresh cookie was not stored. Sign in again.", reauthenticate: true);

            lock (this.registryGate)
                this.flights[key] = new Flight(next, grant, this.Clock().Add(this.options.SingleFlightTtl));
            return grant;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Forget(string sessionId)
    {
        lock (this.registryGate)
        {
            var prefix = sessionId + ":";
            foreach (var key in this.flights.Keys.Where(candidate => candidate.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
                this.flights.Remove(key);
        }
    }

    private SemaphoreSlim Gate(string sessionId)
    {
        lock (this.registryGate)
        {
            if (!this.gates.TryGetValue(sessionId, out var gate))
            {
                gate = new SemaphoreSlim(1, 1);
                this.gates[sessionId] = gate;
            }

            return gate;
        }
    }

    private void Sweep()
    {
        var now = this.Clock();
        lock (this.registryGate)
        {
            foreach (var key in this.flights.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
                this.flights.Remove(key);
        }
    }

    private static string FlightKey(RefreshCookie cookie) => cookie.SessionId + ":" + cookie.Generation;

    private sealed record Flight(RefreshCookie Cookie, AccessGrant Grant, DateTimeOffset ExpiresAt);
}
