using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Supabase.CookieSession.Tests.Support;

/// <summary>
/// Local stand-in for the three HTTP surfaces this SDK calls.
/// It is not Supabase, GoTrue, PostgREST, or the Elixir Phoenix server.
/// It does not prove RLS, single-use refresh revocation, or realtime-js vsn 1.0.0 array frames.
/// It does accept the JSON object frames this C# SDK writes on a real WebSocket, plus CORS so a browser can call it.
/// </summary>
internal sealed class ProtocolHost : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly object gate = new();
    private readonly Dictionary<string, string> refreshUser = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> accessUser = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Connection> connections = new();
    private readonly List<ProtocolFrame> frames = [];
    private int sequence;
    private int nextConnection;

    private ProtocolHost(WebApplication app) => this.app = app;

    public string Url { get; private set; } = "";

    public static async Task<ProtocolHost> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var host = new ProtocolHost(app);
        app.UseWebSockets();
        app.Use(host.CorsAsync);
        app.MapPost("/auth/v1/token", host.Token);
        app.MapPost("/auth/v1/logout", (HttpContext _) => Results.NoContent());
        app.MapMethods("/rest/v1/notes", new[] { "GET", "HEAD" }, host.Notes);
        app.Map("/realtime/v1/websocket", host.SocketAsync);
        app.MapGet("/__protocol/trace", () => Results.Json(host.Snapshot()));
        await app.StartAsync().ConfigureAwait(false);
        host.Url = app.Urls.Single().TrimEnd('/');
        return host;
    }

    public IReadOnlyList<ProtocolFrame> Snapshot()
    {
        lock (this.gate)
            return this.frames.ToArray();
    }

    public async Task DropAsync(bool browser)
    {
        List<WebSocket> sockets;
        lock (this.gate)
            sockets = this.connections.Values.Where(connection => connection.Browser == browser).Select(connection => connection.Socket).ToArray().ToList();

        foreach (var socket in sockets)
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test drop", CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                socket.Abort();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await this.app.StopAsync().ConfigureAwait(false);
        await this.app.DisposeAsync().ConfigureAwait(false);
    }

    private Task CorsAsync(HttpContext http, RequestDelegate next)
    {
        var origin = http.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin))
        {
            http.Response.Headers.AccessControlAllowOrigin = origin;
            var requested = http.Request.Headers.AccessControlRequestHeaders.ToString();
            http.Response.Headers.AccessControlAllowHeaders = string.IsNullOrEmpty(requested)
                ? "authorization,apikey,content-type,accept,prefer"
                : requested;
            http.Response.Headers.AccessControlAllowMethods = "GET,POST,PUT,PATCH,DELETE,OPTIONS,HEAD";
            http.Response.Headers.AccessControlMaxAge = "600";
            http.Response.Headers.Vary = "Origin";
        }

        if (HttpMethods.IsOptions(http.Request.Method))
        {
            http.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        }

        return next(http);
    }

    private IResult Token(HttpContext http)
    {
        var body = new StreamReader(http.Request.Body).ReadToEndAsync().GetAwaiter().GetResult();
        var document = JsonDocument.Parse(string.IsNullOrEmpty(body) ? "{}" : body);
        var grant = http.Request.Query["grant_type"].ToString();
        if (grant == "password")
        {
            var email = document.RootElement.TryGetProperty("email", out var value) ? value.GetString() ?? "" : "";
            var user = email.StartsWith('b') ? "user-b" : "user-a";
            return Results.Json(this.Issue(user, email));
        }

        if (grant == "refresh_token")
        {
            var refresh = document.RootElement.TryGetProperty("refresh_token", out var value) ? value.GetString() ?? "" : "";
            string? user;
            lock (this.gate)
                this.refreshUser.TryGetValue(refresh, out user);
            if (user is null)
                return Results.Json(new { error = "invalid_grant" }, statusCode: StatusCodes.Status400BadRequest);
            var email = user == "user-b" ? "b@example.test" : "a@example.test";
            return Results.Json(this.Issue(user, email));
        }

        return Results.NotFound();
    }

    private IResult Notes(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : "";
        string? user;
        lock (this.gate)
            this.accessUser.TryGetValue(token, out user);
        this.Add(new ProtocolFrame { Kind = "notes", AccessToken = token, Topic = user });
        if (user == "user-b")
            return Results.Json(new[] { new { id = 2, body = "seed-note-b" } });
        if (user == "user-a")
            return Results.Json(new[] { new { id = 1, body = "seed-note-a" } });
        return Results.Json(Array.Empty<object>());
    }

    private async Task SocketAsync(HttpContext http)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var browser = !string.IsNullOrEmpty(http.Request.Headers.Origin.ToString());
        var socket = await http.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var id = Interlocked.Increment(ref this.nextConnection);
        lock (this.gate)
            this.connections[id] = new Connection(socket, browser);
        this.Add(new ProtocolFrame { Kind = "open", Connection = id, Browser = browser });
        var buffer = new byte[64 * 1024];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var text = await this.ReadAsync(socket, buffer).ConfigureAwait(false);
                if (text is null)
                    break;
                await this.OnMessageAsync(id, browser, socket, text).ConfigureAwait(false);
            }
        }
        catch (WebSocketException)
        {
            // The client went away. The close frame below is the record.
        }
        finally
        {
            lock (this.gate)
                this.connections.Remove(id);
            this.Add(new ProtocolFrame { Kind = "close", Connection = id, Browser = browser });
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    socket.Abort();
                }
            }
        }
    }

    private async Task OnMessageAsync(int id, bool browser, WebSocket socket, string text)
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        var topic = root.TryGetProperty("topic", out var topicValue) ? topicValue.GetString() : null;
        var name = root.TryGetProperty("event", out var eventValue) ? eventValue.GetString() : null;
        var reference = root.TryGetProperty("ref", out var refValue) ? refValue.GetString() : null;
        var token = root.TryGetProperty("payload", out var payload) ? TokenOf(payload) : null;
        if (name is "phx_join" or "access_token")
            this.Add(new ProtocolFrame { Kind = name, Connection = id, Browser = browser, Topic = topic, AccessToken = token });

        if (name is "phx_join" or "heartbeat" or "access_token")
        {
            var reply = "{\"topic\":" + JsonSerializer.Serialize(topic)
                + ",\"event\":\"phx_reply\",\"ref\":" + JsonSerializer.Serialize(reference)
                + ",\"payload\":{\"status\":\"ok\",\"response\":{}}}";
            var bytes = Encoding.UTF8.GetBytes(reply);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
        }

        if (name == "phx_join" && string.Equals(topic, "realtime:notes", StringComparison.Ordinal))
        {
            var push = "{\"topic\":\"realtime:notes\",\"event\":\"note-created\",\"payload\":{},\"ref\":null}";
            var bytes = Encoding.UTF8.GetBytes(push);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<string?> ReadAsync(WebSocket socket, byte[] buffer)
    {
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    /// <summary>
    /// Every refresh token this process has issued stays valid. That is wider than GoTrue's
    /// reuse interval, so a passing test here does not prove single-use revocation.
    /// </summary>
    private object Issue(string user, string email)
    {
        var number = Interlocked.Increment(ref this.sequence);
        var access = $"access-{user}-{number}";
        var refresh = $"refresh-{user}-{number}";
        lock (this.gate)
        {
            this.refreshUser[refresh] = user;
            this.accessUser[access] = user;
        }

        return new
        {
            access_token = access,
            refresh_token = refresh,
            expires_in = 3600,
            token_type = "bearer",
            user = new { id = user, email },
        };
    }

    private static string? TokenOf(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("access_token", out var token))
            return null;
        return token.GetString();
    }

    private void Add(ProtocolFrame frame)
    {
        lock (this.gate)
        {
            this.frames.Add(frame);
            if (this.frames.Count > 400)
                this.frames.RemoveRange(0, this.frames.Count - 400);
        }
    }

    private sealed class Connection
    {
        public Connection(WebSocket socket, bool browser)
        {
            this.Socket = socket;
            this.Browser = browser;
        }

        public WebSocket Socket { get; }

        public bool Browser { get; }
    }
}

internal sealed class ProtocolFrame
{
    public string Kind { get; init; } = "";

    public int Connection { get; init; }

    public bool Browser { get; init; }

    public string? Topic { get; init; }

    public string? AccessToken { get; init; }
}
