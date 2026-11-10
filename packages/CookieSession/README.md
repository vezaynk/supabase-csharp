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

The interactive circuit is a new scope. It does not see the prerender access token. Prerender persists only the profile and the notes (`PersistentComponentState`, key `cookie-session-view-auto` or `cookie-session-view-server`). That payload has no refresh token and no access token, so neither is written into the HTML. The first interactive render, Server or WASM, paints that same profile and those same notes. It does not fetch notes again and it does not show the sign-in form.

The interactive runtime still needs an access token to open a socket. After the first paint it calls `POST /auth/session/token` in the background, keeps the notes on screen, and then subscribes. That is one extra refresh for the new runtime. Putting the prerender access token in the persisted state would avoid it, and would also embed that JWT in the page. This sample does not do that. Static `/static` never starts that second redeem.

## Rotation, and the races that remain

One browser tab-set uses `navigator.locks.request("sb-cookie-session")` around sign-in, redeem, and sign-out. If Web Locks are missing, only that tab is queued.

Inside one process, `RefreshAuthority` holds a gate per session id. Waiters for the same generation share one GoTrue call and write the same next cookie. The jar refuses an older generation, and refuses a different refresh token at the same generation. A rejected redeem deletes the cookie only when this process has not already published a newer generation.

While that single-flight entry is still alive, a request that presents an older generation of the same session does not call GoTrue and does not sign the user out. The response is the newer access token, and `Set-Cookie` repeats the newer refresh cookie. Two tabs that overlap a rotation therefore keep the session.

What this does not prevent:

- Two servers can both redeem the cookie they were given. Supabase's refresh-token reuse window may return the same new pair to the second call. Outside that window one call gets a rejection. The generation guard is per process, so the other server can still `Set-Cookie` a conflicting token. This is not a distributed lock.
- After the 10 second single-flight memory expires, a request that still presents the old cookie calls GoTrue again. If that call rotates the token and this process has already moved on, the write is rejected and the user must sign in again.
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

This run: **39 passed, 0 failed** (Release, net10.0), including both headless Chrome tests below. No test called a live Supabase project. `LiveSupabase_IsOptInAndWasNotRun` returns immediately unless `COOKIE_SESSION_LIVE_URL` is set, and even then it does not dial out. Docker and the Supabase CLI are not installed here, so nothing ran `supabase start`.

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
- A failed `POST /auth/session/token` keeps its own status. The sample's status-code page does not re-execute `/auth`, so a missing `Origin` stays HTTP 403 and the body is not the Razor sentence `The request has an incorrect Content-type.`
- An older cookie generation inside the single-flight TTL is answered with the newer grant and does not call GoTrue again.

Before this handoff change, the interactive scope started empty. `OnInitializedAsync` redeemed the cookie again and called notes again before it could paint, so a document load produced two notes calls. The flicker test fails if that returns: a `MutationObserver` installed before the document records whether `[data-profile]` or `[data-note]` disappears, whether `[data-signin]` appears, and whether a `[data-loading]` placeholder appears. It also requires exactly one notes call per document and, for an interactive page, exactly one background `POST /auth/session/token`. Static requires zero token calls. Rotation on `/server` must add a token call and no notes call, without removing the profile.

`BrowserProtocolTests` starts `ProtocolHost` (see the limits below), the sample, and headless Chrome. It is part of the command above. What that run checked:

- Signed-in `/static` HTML, from the document response, contains `a@example.test` and `seed-note-a`.
- First Auto visit is Interactive Server (`sdk-server-websocket`) and shows that note.
- After Blazor has cached its resource hash, the next load is WASM (`sdk-browser-websocket`). Every `POST /auth/session/token` in that load is HTTP 200. The page shows the profile and the note, not `token request failed`.
- `/server` does the same with the server circuit, and its token calls are HTTP 200.
- Subscribe opens a real WebSocket to the protocol host at `/realtime/v1/websocket` (not to the sample). DevTools shows the browser SDK sending `phx_join`. The host records a separate join with no `Origin` header, which is the server `ClientWebSocket`.
- The host pushes `note-created`. The WASM page shows it.
- Rotate sends an `access_token` frame with a new synthetic JWT on each socket. Closing that socket makes the SDK join again with that same latest token, for both the browser socket and the server socket.
- A second Chrome profile signed in as `b@example.test` sees `seed-note-b` and not user A's note, while A's tabs still show `seed-note-a`.
- Clicking Rotate on the WASM tab and the server tab together leaves both on user A. Sign-out returns A's form and leaves B signed in.

`ProtocolHost` is not Supabase. It speaks the three shapes this SDK uses: GoTrue's password and refresh JSON, a PostgREST `GET /rest/v1/notes` with CORS, and the JSON object frames this Realtime client writes (`topic`, `event`, `payload`, `ref`). It does not prove Elixir Phoenix array frames, realtime-js, RLS, or GoTrue single-use revocation. Every refresh token the host has issued stays valid, which is wider than GoTrue's reuse interval. Access tokens are synthetic strings such as `access-user-a-3`, not JWTs signed by GoTrue.

The WASM failure this replaced was two stacked bugs, both visible in headless Chrome against the earlier fake:

- `UseStatusCodePagesWithReExecute("/not-found")` kept the original POST and `application/json` body. The Razor endpoint answered `400` with `The request has an incorrect Content-type.` `cookie-session.js` reports any non-401 as `token request failed`, so a real 401 or 403 from `/auth/session/token` was hidden. `/auth` is now outside that re-execute.
- `OnInitializedAsync` loaded notes before the profile render was committed. A browser notes call that failed (the old fake sent no CORS headers, so WASM's PostgREST `fetch` threw) aborted the component and left the sign-in form on screen even after the token POST had returned 200. A notes failure now stays an error under the signed-in profile.

Not run:

- Two OS processes redeeming one cookie.
- A real local `supabase start` stack. Docker and the Supabase CLI are not installed, and no project credentials were used.
