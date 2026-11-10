# Cookie session prototype

Opt-in Blazor companion for this fork. It is not part of the shipping SDK packages, and nothing here is published.

The SDK already had the hooks this needs. No GoTrue or Realtime type was changed:

- `SupabaseOptions.AutoRefreshToken = false` leaves GoTrue's refresh timer off. The default `IGotrueSessionPersistence` is a no-op, so the SDK does not persist a session.
- `Postgrest` / `Functions` / `Storage` `GetHeaders` are replaced so every request sends the in-memory user access token. The publishable key stays on `apikey`.
- `Realtime.SetAuth(jwt)` stores that access token and pushes `{ "event": "access_token" }` to channels that have already joined. Join and rejoin call `RetrieveAccessToken()`, which reads the same field, so a reconnect uses the latest token.
- On WebAssembly the SDK's default socket factory already builds a `ClientWebSocket` and skips headers the browser forbids (`packages/Realtime/Realtime/Sockets/DefaultWebSocketFactory.cs`). .NET maps that socket onto the browser WebSocket. The sample does not open a same-origin relay.

## What is durable, and what is not

| State | Where it lives |
| --- | --- |
| Refresh token | Encrypted `HttpOnly` cookie (`sb-refresh`, chunked). `Secure` when configured, `SameSite=Lax`. |
| Access token | Memory only: a JS module variable, the WASM or circuit `MemoryAccessTokenStore`, and the SDK client. Not `localStorage`, `sessionStorage`, or IndexedDB. |
| Single-flight result | This server process, for `SingleFlightTtl` (default 10 seconds), keyed by session id and cookie generation. It exists so overlapping requests write one new cookie. It is not a database, not shared across instances, and a crash drops it. |
| Data Protection keys | The sample stores them in `.data-protection-keys/`. Those are keys, not tokens. |

A refresh is successful only after `Set-Cookie` is written. If GoTrue rotates the refresh token and the process dies before that write, the old cookie is dead and the user must sign in again.

## Render modes

| Mode | Access token | Refresh cookie | Database | Realtime |
| --- | --- | --- | --- | --- |
| Static SSR, response not flushed | Redeemed on that request and kept for the request | Rotated on that response | SDK Postgrest from the server, user JWT | Not started |
| Prerender of an interactive page | Not redeemed (no JS, and the circuit cannot `Set-Cookie`) | Untouched | Waits until interactive | Not started |
| Interactive Server | Browser calls `POST /auth/session/token`, then JS passes the access token into the circuit | Stays in the browser. The circuit never receives it | SDK Postgrest from the server | Native SDK `ClientWebSocket` from the server to Supabase (`sdk-server-websocket`) |
| WASM, including Auto after the handoff | Same endpoint. The access token stays in JS and WASM memory | HttpOnly, so JS cannot read it | SDK Postgrest from the browser | Native SDK `ClientWebSocket` from the browser to Supabase (`sdk-browser-websocket`) |

Interactive Auto starts on the server circuit and then moves to WASM. The server socket is disposed with the circuit. The browser opens its own SDK socket. That is two native connections over the lifetime of the page, not a relay.

## Rotation, and the races that remain

One browser tab-set uses `navigator.locks.request("sb-cookie-session")` around sign-in, redeem, and sign-out. If Web Locks are missing, only that tab is queued.

Inside one process, `RefreshAuthority` holds a gate per session id. Waiters for the same generation share one GoTrue call and write the same next cookie. The jar refuses an older generation, and refuses a different refresh token at the same generation. A rejected redeem deletes the cookie only when this process has not already published a newer generation.

What this does not prevent:

- Two servers can both redeem the cookie they were given. Supabase's refresh-token reuse window may return the same new pair to the second call. Outside that window one call gets a rejection. The generation guard is per process, so the other server can still `Set-Cookie` a conflicting token. This is not a distributed lock.
- After the 10 second single-flight memory expires, a request that still presents the old cookie calls GoTrue again.
- Logout clears the cookie and stops local sockets. GoTrue logout is best-effort. An access JWT that was already issued keeps working until it expires.
- XSS can call the token endpoint. The browser will attach the HttpOnly cookie, and the response contains a fresh access token. HttpOnly stops script from reading the refresh token. It does not stop script from acting as the signed-in user.

User data calls use the user access token and the publishable key. There is no service-role path.

## Run the sample

From the repo root, with the .NET SDK in `global.json`:

```sh
export SUPABASE_URL=http://127.0.0.1:54321
export SUPABASE_ANON_KEY=sb_publishable_or_anon_key
dotnet run --project packages/CookieSession/sample/BlazorAuto
```

Open `http://127.0.0.1:5288`. `SecureCookies` is false in the sample `appsettings.json` because that URL is HTTP. Set it true behind HTTPS.

The anon key is public and is embedded in the host page so WASM can construct the SDK client. The refresh token is not embedded.

No live project was configured in this run. Sign-in against a real GoTrue was not exercised here.

## API

Server registration:

```csharp
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keyRing))
    .SetApplicationName("Supabase.CookieSession.Sample");

builder.Services.AddCookieSession(options =>
{
    options.SupabaseUrl = "http://127.0.0.1:54321";
    options.AnonKey = "sb_publishable_or_anon_key";
    options.SecureCookies = false; // true on HTTPS
});
app.MapCookieSessionEndpoints();
```

WASM registration leaves the URL empty and reads it from the host page:

```csharp
builder.Services.AddSupabaseBrowserSession();
```

A component, any render mode:

```csharp
@inject ICookieSession Session

await Session.SignInAsync(email, password);
await Session.EnsureAsync();
var notes = await Session.GetNotesAsync();
var subscription = await Session.SubscribeAsync("notes", message =>
{
    // message is the Realtime event name. access_token frames are not forwarded.
    return Task.CompletedTask;
});
// subscription.Transport is sdk-server-websocket or sdk-browser-websocket
await Session.RotateAsync();
await Session.SignOutAsync();
```

`Session.Sdk` is the `SupabaseMemoryClient`. Its GoTrue `CurrentSession` stays null, and `TokenRefresh` stays null. `Realtime.SetAuth` is how a new access token reaches an open channel.

The browser helper is `wwwroot/cookie-session.js`. `cookieSession.acquire(force)` is the only redeem path used by interactive code. It returns `{ access_token, expires_at, expires_in, user }` and refuses a body that contains `refresh_token`.

## Tests

Command:

```sh
dotnet test packages/CookieSession/Supabase.CookieSession.Tests/Supabase.CookieSession.Tests.csproj
```

This run: **24 passed, 0 failed, 0 skipped** (Release, net10.0). The sample project builds. With no Supabase URL configured, `GET /` returned 200 and the prerendered sign-in form, a CSRF meta tag, and a public config script with empty `url` and `anonKey`. That HTML contained no access or refresh token. No test called a live Supabase project. `LiveSupabase_IsOptInAndWasNotRun` returns immediately unless `COOKIE_SESSION_LIVE_URL` is set, and even then it does not dial out.

Covered with fakes and the real SDK client:

- Cookie flags (`HttpOnly`, `Secure`, `SameSite=Lax`), chunk round-trip, ciphertext does not contain the refresh token.
- Older generation and a different token at the same generation cannot overwrite the cookie.
- Two waiters on one generation cause one GoTrue refresh. Two session ids do not share results. After the single-flight TTL, a second redeem calls GoTrue again.
- A rejected write or a rejected refresh does not claim success. The endpoint returns 401 and clears the cookie only when no newer generation was stored.
- Origin and antiforgery failures. Responses are `Cache-Control: no-store` and do not contain the refresh token. Logs from the endpoint do not contain the access token, refresh token, or password.
- The real `Supabase.Gotrue.Api` posts the refresh token to `/auth/v1/token?grant_type=refresh_token`. A 400 becomes `AuthRejectedException` whose message does not include the response body.
- The real Realtime client, on an in-memory socket: join carries the access token, `SetAuth` pushes `access_token` to the joined channel, reconnect rejoin carries the new token and not the old one, the socket URL is the configured Supabase host (not this app), a second client does not see the first user's token, `access_token` frames are not delivered to the page callback, sign-out stops the socket, duplicate `Subscribe` keeps one channel.
- Postgrest `Get` sends `Authorization: Bearer <user access token>` and `apikey` set to the publishable key.
- The JS file uses Web Locks and does not reference `localStorage`, `sessionStorage`, or `indexedDB`.

Not run:

- A browser tab actually speaking Phoenix to Supabase Realtime.
- Two OS processes redeeming one cookie.
- A real local `supabase start` stack. Docker was not required for the prototype, and no credentials were available.
