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
| Static SSR (`/static`) | Redeemed in middleware for that request, then discarded with the request | Rotated on that response, before the endpoint writes the body | SDK Postgrest from the server, user JWT. Profile and notes are in the HTML | Not started |
| Prerender of InteractiveAuto (`/`) and InteractiveServer (`/server`) | Same request-scoped redeem. No JS. The circuit does not share this instance | Rotated on that document response. A started response refuses `Set-Cookie` and renders signed out | Same: profile and notes are in the first HTML | Not started. The refresh loop and socket start only after `RendererInfo.IsInteractive` |
| Interactive Server, after the circuit starts | Browser calls `POST /auth/session/token` because the prerender scope is gone, then JS passes the access token into the new circuit | The cookie from the document response. This is a second redeem, not a second copy of the prerender memory | SDK Postgrest from the server | Native SDK `ClientWebSocket` from the server to Supabase (`sdk-server-websocket`) |
| WASM, including Auto after the handoff | Same endpoint. The access token stays in JS and WASM memory | HttpOnly, so JS cannot read it | SDK Postgrest from the browser | Native SDK `ClientWebSocket` from the browser to Supabase (`sdk-browser-websocket`) |

Interactive Auto starts on the server circuit and then moves to WASM. The server socket is disposed with the circuit. The browser opens its own SDK socket. That is two native connections over the lifetime of the page, not a relay.

Document requests (`/`, `/server`, `/static`) run `UsePrerenderSession` before the Razor endpoint. The refresh cookie is redeemed there, `Set-Cookie` is appended, and only then does the page render. The access token stays in that HTTP request's scope. These pages do not enable streaming rendering. If a response has already started, the middleware does not write a cookie and the HTML stays signed out.

The interactive circuit is a new scope. It does not see the prerender access token. Its first `EnsureAsync` asks the browser to call `/auth/session/token`, which redeems the cookie the document response just stored. That is a second refresh of the same visit. WASM on the same page can then reuse the access token held in the JS module if it is still inside the skew, so the handoff does not have to redeem a third time.

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

Sign-in in the tests and the browser smoke used a local fake GoTrue, not a hosted Supabase project.

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
app.UsePrerenderSession();
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

This run: **35 passed, 0 failed** (Release, net10.0). No test called a live Supabase project. `LiveSupabase_IsOptInAndWasNotRun` returns immediately unless `COOKIE_SESSION_LIVE_URL` is set, and even then it does not dial out.

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
- Sample prerender against an in-process fake GoTrue and PostgREST: `GET /` (InteractiveAuto), `GET /server` (InteractiveServer), and `GET /static` with a real encrypted refresh cookie return the user email and `seed-note-a`, one refresh call, one notes call with that user's access token, and an HttpOnly `Set-Cookie` that does not contain the refresh token. Anonymous HTML is the sign-in form and does not call GoTrue. A corrupt cookie and a rejected refresh render the sign-in form and clear the cookie. A transient refresh failure leaves the cookie in place. A later `POST /auth/session/token` redeems the rotated cookie. Two browsers do not see each other's notes. A response that has already started does not rotate.

Headless Chrome, same fake, not a live Supabase project: after sign-in, loading `/` and `/server` showed `a@example.test` and `seed-note-a` once the Blazor circuit was interactive (`sdk-server-websocket`, WebSocket to `/_blazor`). A clean Auto load stayed on that server circuit for 12 seconds; `/auth/session/token` during that load returned 200. A later load reached `sdk-browser-websocket` and then showed `token request failed`. That WASM handoff was not a clean success. Native Supabase Realtime from the browser was not opened.

Not run:

- A browser tab actually speaking Phoenix to Supabase Realtime.
- Two OS processes redeeming one cookie.
- A real local `supabase start` stack. Docker was not required for the prototype, and no credentials were available.
