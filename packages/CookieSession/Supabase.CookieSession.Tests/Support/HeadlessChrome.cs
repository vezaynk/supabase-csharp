using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Supabase.CookieSession.Tests.Support;

/// <summary>Drives the system Chrome over the DevTools protocol. No Selenium package.</summary>
internal sealed class HeadlessChrome : IAsyncDisposable
{
    private readonly Process process;
    private readonly string profile;
    private readonly ClientWebSocket browser = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<string>> pending = new();
    private readonly ConcurrentDictionary<string, ChromePage> pages = new();
    private readonly CancellationTokenSource stop = new();
    private int sequence;

    private HeadlessChrome(Process process, string profile)
    {
        this.process = process;
        this.profile = profile;
    }

    public static async Task<HeadlessChrome> LaunchAsync()
    {
        var binary = new[] { "/usr/bin/google-chrome", "/usr/bin/google-chrome-stable", "/usr/bin/chromium" }
            .FirstOrDefault(File.Exists);
        if (binary is null)
            throw new InvalidOperationException("Headless Chrome is not installed.");

        var profile = Directory.CreateTempSubdirectory("cookie-chrome-").FullName;
        var port = FreePort();
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(binary, $"--headless=new --disable-gpu --no-sandbox --disable-dev-shm-usage --remote-debugging-port={port} --user-data-dir={profile} about:blank")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var chrome = new HeadlessChrome(process, profile);
        using var http = new HttpClient();
        string? debuggerUrl = null;
        for (var attempt = 0; attempt < 50 && debuggerUrl is null; attempt++)
        {
            try
            {
                var version = await http.GetStringAsync(new Uri($"http://127.0.0.1:{port}/json/version")).ConfigureAwait(false);
                debuggerUrl = JsonDocument.Parse(version).RootElement.GetProperty("webSocketDebuggerUrl").GetString();
            }
            catch (Exception)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }
        }

        if (debuggerUrl is null)
            throw new InvalidOperationException("Chrome did not open a DevTools port.");

        await chrome.browser.ConnectAsync(new Uri(debuggerUrl), CancellationToken.None).ConfigureAwait(false);
        _ = chrome.ReceiveLoop();
        return chrome;
    }

    public async Task<ChromePage> OpenAsync()
    {
        var created = await this.SendAsync(null, "Target.createTarget", new { url = "about:blank" }).ConfigureAwait(false);
        var targetId = JsonDocument.Parse(created).RootElement.GetProperty("targetId").GetString()!;
        var attached = await this.SendAsync(null, "Target.attachToTarget", new { targetId, flatten = true }).ConfigureAwait(false);
        var sessionId = JsonDocument.Parse(attached).RootElement.GetProperty("sessionId").GetString()!;
        var page = new ChromePage(this, sessionId);
        this.pages[sessionId] = page;
        await page.EnableAsync().ConfigureAwait(false);
        return page;
    }

    public async ValueTask DisposeAsync()
    {
        this.stop.Cancel();
        try
        {
            if (this.browser.State == WebSocketState.Open)
                await this.browser.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The browser is about to be killed.
        }

        try
        {
            if (!this.process.HasExited)
                this.process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Already gone.
        }

        try
        {
            Directory.Delete(this.profile, recursive: true);
        }
        catch (Exception)
        {
            // Chrome may still be releasing the profile directory.
        }
    }

    internal async Task<string> SendAsync(string? sessionId, string method, object parameters)
    {
        var id = Interlocked.Increment(ref this.sequence);
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.pending[id] = completion;
        var sessionJson = sessionId is null ? "" : ",\"sessionId\":" + JsonSerializer.Serialize(sessionId);
        var payload = "{\"id\":" + id
            + ",\"method\":" + JsonSerializer.Serialize(method)
            + ",\"params\":" + JsonSerializer.Serialize(parameters, parameters.GetType())
            + sessionJson + "}";
        var bytes = Encoding.UTF8.GetBytes(payload);
        await this.browser.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
        var finished = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(20))).ConfigureAwait(false);
        if (finished != completion.Task)
            throw new TimeoutException($"Chrome did not answer {method}.");
        return await completion.Task.ConfigureAwait(false);
    }

    private async Task ReceiveLoop()
    {
        var buffer = new byte[1024 * 256];
        try
        {
            while (!this.stop.IsCancellationRequested && this.browser.State == WebSocketState.Open)
            {
                using var stream = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await this.browser.ReceiveAsync(buffer, this.stop.Token).ConfigureAwait(false);
                    stream.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                using var document = JsonDocument.Parse(Encoding.UTF8.GetString(stream.ToArray()));
                var root = document.RootElement;
                if (root.TryGetProperty("id", out var idProperty) && idProperty.TryGetInt32(out var id) && this.pending.TryRemove(id, out var completion))
                {
                    if (root.TryGetProperty("error", out var error))
                        completion.SetException(new InvalidOperationException(error.GetRawText()));
                    else
                        completion.SetResult(root.TryGetProperty("result", out var value) ? value.GetRawText() : "{}");
                    continue;
                }

                if (!root.TryGetProperty("method", out var methodProperty) || !root.TryGetProperty("params", out var eventParameters))
                    continue;
                var method = methodProperty.GetString() ?? "";
                var session = root.TryGetProperty("sessionId", out var sessionProperty) ? sessionProperty.GetString() : null;
                if (session is not null && this.pages.TryGetValue(session, out var page))
                    page.OnEvent(method, eventParameters);
            }
        }
        catch (Exception)
        {
            foreach (var completion in this.pending.Values)
                completion.TrySetCanceled();
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint) listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

internal sealed class ChromePage
{
    private readonly HeadlessChrome chrome;
    private readonly string sessionId;
    private readonly HashSet<string> realtimeSockets = new(StringComparer.Ordinal);
    private readonly List<string> sentFrames = [];
    private readonly List<string> socketUrls = [];
    private readonly List<int> tokenStatuses = [];
    private string? documentRequestId;

    public ChromePage(HeadlessChrome chrome, string sessionId)
    {
        this.chrome = chrome;
        this.sessionId = sessionId;
    }

    public string? DocumentBody { get; private set; }

    public IReadOnlyList<int> TokenStatuses
    {
        get
        {
            lock (this.tokenStatuses)
                return this.tokenStatuses.ToArray();
        }
    }

    public IReadOnlyList<string> SocketUrls
    {
        get
        {
            lock (this.socketUrls)
                return this.socketUrls.ToArray();
        }
    }

    public IReadOnlyList<string> SentFrames
    {
        get
        {
            lock (this.sentFrames)
                return this.sentFrames.ToArray();
        }
    }

    public async Task EnableAsync()
    {
        await this.SendAsync("Network.enable", new { }).ConfigureAwait(false);
        await this.SendAsync("Network.setCacheDisabled", new { cacheDisabled = true }).ConfigureAwait(false);
        await this.SendAsync("Page.enable", new { }).ConfigureAwait(false);
        await this.SendAsync("Runtime.enable", new { }).ConfigureAwait(false);
    }

    public void ClearNetwork()
    {
        lock (this.tokenStatuses)
            this.tokenStatuses.Clear();
        lock (this.sentFrames)
            this.sentFrames.Clear();
        lock (this.socketUrls)
            this.socketUrls.Clear();
        this.DocumentBody = null;
        this.documentRequestId = null;
    }

    public Task SetCookieAsync(string name, string value, string url) =>
        this.SendAsync("Network.setCookie", new { name, value, url, httpOnly = true, path = "/" });

    public async Task NavigateAsync(string url)
    {
        this.ClearNetwork();
        await this.SendAsync("Page.navigate", new { url }).ConfigureAwait(false);
    }

    public async Task<string> TextAsync() =>
        await this.EvalAsync("document.body ? document.body.innerText : ''").ConfigureAwait(false);

    public async Task<string?> StorageAsync(string key)
    {
        var json = await this.EvalAsync($"localStorage.getItem({JsonSerializer.Serialize(key)})").ConfigureAwait(false);
        return json == "null" ? null : json;
    }

    public async Task ClickAsync(string label)
    {
        var script = "(() => { const button = [...document.querySelectorAll('button')].find(item => item.textContent.trim() === " + JsonSerializer.Serialize(label) + "); if (!button) return 'missing'; button.click(); return 'clicked'; })()";
        var result = await this.EvalAsync(script).ConfigureAwait(false);
        if (result != "clicked")
            throw new InvalidOperationException($"Could not click {label}.");
    }

    public async Task CaptureDocumentAsync()
    {
        if (this.documentRequestId is null)
            return;
        var json = await this.SendAsync("Network.getResponseBody", new { requestId = this.documentRequestId }).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var body = document.RootElement.GetProperty("body").GetString() ?? "";
        var encoded = document.RootElement.TryGetProperty("base64Encoded", out var flag) && flag.GetBoolean();
        this.DocumentBody = encoded ? Encoding.UTF8.GetString(Convert.FromBase64String(body)) : body;
    }

    public async Task<string> EvalAsync(string expression)
    {
        var json = await this.SendAsync("Runtime.evaluate", new { expression, awaitPromise = true, returnByValue = true }).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("exceptionDetails", out var details))
            throw new InvalidOperationException(details.GetRawText());
        var inner = document.RootElement.GetProperty("result");
        if (inner.TryGetProperty("subtype", out var subtype) && subtype.GetString() == "error")
            throw new InvalidOperationException(inner.GetRawText());
        if (!inner.TryGetProperty("value", out var value) || value.ValueKind == JsonValueKind.Null)
            return "";
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();
    }

    internal void OnEvent(string method, JsonElement parameters)
    {
        if (method == "Network.responseReceived")
        {
            var response = parameters.GetProperty("response");
            var url = response.GetProperty("url").GetString() ?? "";
            if (url.Contains("/auth/session/token", StringComparison.Ordinal))
            {
                lock (this.tokenStatuses)
                    this.tokenStatuses.Add(response.GetProperty("status").GetInt32());
            }

            if (this.documentRequestId is null && parameters.GetProperty("type").GetString() == "Document")
                this.documentRequestId = parameters.GetProperty("requestId").GetString();
            return;
        }

        if (method == "Network.webSocketCreated")
        {
            var url = parameters.GetProperty("url").GetString() ?? "";
            if (url.Contains("/realtime/v1/websocket", StringComparison.Ordinal))
            {
                this.realtimeSockets.Add(parameters.GetProperty("requestId").GetString() ?? "");
                lock (this.socketUrls)
                    this.socketUrls.Add(url);
            }
            return;
        }

        if (method == "Network.webSocketFrameSent")
        {
            var requestId = parameters.GetProperty("requestId").GetString() ?? "";
            if (!this.realtimeSockets.Contains(requestId))
                return;
            var data = parameters.GetProperty("response").GetProperty("payloadData").GetString() ?? "";
            lock (this.sentFrames)
                this.sentFrames.Add(data);
        }
    }

    private Task<string> SendAsync(string method, object parameters) =>
        this.chrome.SendAsync(this.sessionId, method, parameters);
}
