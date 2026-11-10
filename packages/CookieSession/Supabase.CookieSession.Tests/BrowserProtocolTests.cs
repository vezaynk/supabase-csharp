using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Supabase.CookieSession.Tests.Support;

namespace Supabase.CookieSession.Tests;

/// <summary>
/// Headless Chrome against the sample and <see cref="ProtocolHost"/>.
/// The host is a local GoTrue/PostgREST/WebSocket stand-in, not a live Supabase project and not Elixir Phoenix.
/// </summary>
[TestClass]
[TestCategory("Browser")]
[DoNotParallelize]
public class BrowserProtocolTests
{
    [TestMethod]
    [Timeout(240000)]
    public async Task Auto_ShouldStaySignedInAcrossServerWasmSocketsRotationAndASecondUser()
    {
        await using var protocol = await ProtocolHost.StartAsync().ConfigureAwait(false);
        await using var sample = await SampleProcess.StartAsync(protocol.Url).ConfigureAwait(false);
        await using var chrome = await HeadlessChrome.LaunchAsync().ConfigureAwait(false);
        var detail = new StringBuilder();
        try
        {
            var origin = sample.Origin;
            var cookies = await this.SignInAsync(sample.OriginUri, "a@example.test").ConfigureAwait(false);
            var auto = await chrome.OpenAsync().ConfigureAwait(false);
            foreach (var cookie in cookies)
                await auto.SetCookieAsync(cookie.Name, cookie.Value, origin + "/").ConfigureAwait(false);

            await auto.NavigateAsync(origin + "/static").ConfigureAwait(false);
            var staticText = await WaitTextAsync(auto, text => text.Contains("a@example.test", StringComparison.Ordinal) && text.Contains("seed-note-a", StringComparison.Ordinal), TimeSpan.FromSeconds(20), "static SSR did not render the signed-in profile", detail, protocol).ConfigureAwait(false);
            await auto.CaptureDocumentAsync().ConfigureAwait(false);
            var document = auto.DocumentBody ?? "";
            document.Should().Contain("a@example.test");
            document.Should().Contain("seed-note-a");
            document.Should().NotContain("seed-note-b");
            staticText.Should().Contain("not started until interactive");

            await auto.NavigateAsync(origin + "/").ConfigureAwait(false);
            var first = await WaitTextAsync(auto, text => text.Contains("sdk-server-websocket", StringComparison.Ordinal) && text.Contains("seed-note-a", StringComparison.Ordinal), TimeSpan.FromSeconds(30), "Auto first visit did not become Interactive Server with data", detail, protocol).ConfigureAwait(false);
            first.Should().NotContain("token request failed");
            first.Should().NotContain("sdk-browser-websocket");
            await WaitAsync(() => auto.StorageAsync("blazor-resource-hash:BlazorAuto.Client"), value => !string.IsNullOrEmpty(value), TimeSpan.FromSeconds(45), "WASM resources were not cached").ConfigureAwait(false);

            await auto.NavigateAsync(origin + "/").ConfigureAwait(false);
            var wasm = await WaitTextAsync(auto, text => text.Contains("sdk-browser-websocket", StringComparison.Ordinal) && text.Contains("a@example.test", StringComparison.Ordinal) && text.Contains("seed-note-a", StringComparison.Ordinal), TimeSpan.FromSeconds(40), "cached Auto load did not select WASM with data", detail, protocol).ConfigureAwait(false);
            wasm.Should().NotContain("token request failed");
            wasm.Should().NotContain("Sign in");
            auto.TokenStatuses.Should().NotBeEmpty("WASM must redeem the cookie");
            auto.TokenStatuses.Should().OnlyContain(status => status == 200);

            await auto.ClickAsync("Subscribe").ConfigureAwait(false);
            await WaitTextAsync(auto, text => text.Contains("note-created", StringComparison.Ordinal) && text.Contains("joined notes via sdk-browser-websocket", StringComparison.Ordinal), TimeSpan.FromSeconds(20), "browser socket did not join and deliver a message", detail, protocol).ConfigureAwait(false);
            auto.SocketUrls.Should().Contain(url => url.StartsWith(protocol.Url.Replace("http://", "ws://", StringComparison.Ordinal) + "/realtime/v1/websocket", StringComparison.Ordinal));
            var browserJoin = await WaitFrameAsync(protocol, frame => frame.Kind == "phx_join" && frame.Browser && frame.Topic == "realtime:notes" && !string.IsNullOrEmpty(frame.AccessToken), TimeSpan.FromSeconds(10), "protocol host did not see the browser join").ConfigureAwait(false);
            var firstToken = browserJoin.AccessToken!;

            await auto.ClickAsync("Rotate").ConfigureAwait(false);
            var rotated = await WaitFrameAsync(protocol, frame => frame.Kind == "access_token" && frame.Browser && frame.Connection == browserJoin.Connection && frame.AccessToken != firstToken, TimeSpan.FromSeconds(15), "browser socket did not receive the rotated access token").ConfigureAwait(false);
            await protocol.DropAsync(browser: true).ConfigureAwait(false);
            var rejoined = await WaitFrameAsync(protocol, frame => frame.Kind == "phx_join" && frame.Browser && frame.Topic == "realtime:notes" && frame.Connection != browserJoin.Connection && frame.AccessToken == rotated.AccessToken, TimeSpan.FromSeconds(20), "browser reconnect did not join with the latest access token").ConfigureAwait(false);
            auto.SentFrames.Should().Contain(frame => frame.Contains(rejoined.AccessToken!, StringComparison.Ordinal), "the browser DevTools trace must show the SDK sending the latest access token");
            auto.SentFrames.Should().Contain(frame => frame.Contains("phx_join", StringComparison.Ordinal));

            var server = await chrome.OpenAsync().ConfigureAwait(false);
            await server.NavigateAsync(origin + "/server").ConfigureAwait(false);
            var serverText = await WaitTextAsync(server, text => text.Contains("sdk-server-websocket", StringComparison.Ordinal) && text.Contains("seed-note-a", StringComparison.Ordinal), TimeSpan.FromSeconds(30), "Interactive Server tab did not load data", detail, protocol).ConfigureAwait(false);
            serverText.Should().NotContain("seed-note-b");
            server.TokenStatuses.Should().NotBeEmpty();
            server.TokenStatuses.Should().OnlyContain(status => status == 200);
            await server.ClickAsync("Subscribe").ConfigureAwait(false);
            await WaitTextAsync(server, text => text.Contains("joined notes via sdk-server-websocket", StringComparison.Ordinal), TimeSpan.FromSeconds(20), "server circuit did not report its SDK socket", detail, protocol).ConfigureAwait(false);
            var serverJoin = await WaitFrameAsync(protocol, frame => frame.Kind == "phx_join" && !frame.Browser && frame.Topic == "realtime:notes" && !string.IsNullOrEmpty(frame.AccessToken), TimeSpan.FromSeconds(15), "protocol host did not see the server ClientWebSocket join").ConfigureAwait(false);
            await server.ClickAsync("Rotate").ConfigureAwait(false);
            var serverRotated = await WaitFrameAsync(protocol, frame => frame.Kind == "access_token" && !frame.Browser && frame.Connection == serverJoin.Connection && frame.AccessToken != serverJoin.AccessToken, TimeSpan.FromSeconds(15), "server socket did not receive the rotated access token").ConfigureAwait(false);
            await protocol.DropAsync(browser: false).ConfigureAwait(false);
            await WaitFrameAsync(protocol, frame => frame.Kind == "phx_join" && !frame.Browser && frame.Topic == "realtime:notes" && frame.Connection != serverJoin.Connection && frame.AccessToken == serverRotated.AccessToken, TimeSpan.FromSeconds(20), "server reconnect did not join with the latest access token").ConfigureAwait(false);

            await using var otherBrowser = await HeadlessChrome.LaunchAsync().ConfigureAwait(false);
            var otherCookies = await this.SignInAsync(sample.OriginUri, "b@example.test").ConfigureAwait(false);
            var other = await otherBrowser.OpenAsync().ConfigureAwait(false);
            foreach (var cookie in otherCookies)
                await other.SetCookieAsync(cookie.Name, cookie.Value, origin + "/").ConfigureAwait(false);
            await other.NavigateAsync(origin + "/static").ConfigureAwait(false);
            var otherText = await WaitTextAsync(other, text => text.Contains("b@example.test", StringComparison.Ordinal) && text.Contains("seed-note-b", StringComparison.Ordinal), TimeSpan.FromSeconds(20), "second user did not get their own static data", detail, protocol).ConfigureAwait(false);
            otherText.Should().NotContain("seed-note-a");
            otherText.Should().NotContain("a@example.test");
            (await auto.TextAsync().ConfigureAwait(false)).Should().Contain("seed-note-a").And.NotContain("seed-note-b");

            await Task.WhenAll(auto.ClickAsync("Rotate"), server.ClickAsync("Rotate")).ConfigureAwait(false);
            var afterA = await WaitTextAsync(auto, text => text.Contains("a@example.test", StringComparison.Ordinal) && text.Contains("seed-note-a", StringComparison.Ordinal) && !text.Contains("Sign in", StringComparison.Ordinal), TimeSpan.FromSeconds(20), "WASM tab signed out during a concurrent refresh", detail, protocol).ConfigureAwait(false);
            var afterServer = await WaitTextAsync(server, text => text.Contains("a@example.test", StringComparison.Ordinal) && text.Contains("seed-note-a", StringComparison.Ordinal) && !text.Contains("Sign in", StringComparison.Ordinal), TimeSpan.FromSeconds(20), "server tab signed out during a concurrent refresh", detail, protocol).ConfigureAwait(false);
            afterA.Should().NotContain("token request failed").And.NotContain("seed-note-b");
            afterServer.Should().NotContain("token request failed").And.NotContain("seed-note-b");

            await auto.ClickAsync("Sign out").ConfigureAwait(false);
            await auto.NavigateAsync(origin + "/").ConfigureAwait(false);
            var signedOut = await WaitTextAsync(auto, text => text.Contains("Sign in", StringComparison.Ordinal), TimeSpan.FromSeconds(20), "sign-out did not return the form", detail, protocol).ConfigureAwait(false);
            signedOut.Should().NotContain("a@example.test");
            await other.NavigateAsync(origin + "/static").ConfigureAwait(false);
            var stillB = await WaitTextAsync(other, text => text.Contains("b@example.test", StringComparison.Ordinal), TimeSpan.FromSeconds(20), "signing out user A cleared user B", detail, protocol).ConfigureAwait(false);
            stillB.Should().Contain("seed-note-b").And.NotContain("seed-note-a");
        }
        catch (Exception exception)
        {
            detail.AppendLine(exception.Message);
            detail.AppendLine(this.Trace(protocol));
            throw new AssertFailedException(detail.ToString(), exception);
        }
    }

    [TestMethod]
    [Timeout(180000)]
    public async Task Handoff_ShouldKeepPrerenderedNotesOnScreenWithoutASecondFetch()
    {
        await using var protocol = await ProtocolHost.StartAsync().ConfigureAwait(false);
        await using var sample = await SampleProcess.StartAsync(protocol.Url).ConfigureAwait(false);
        await using var chrome = await HeadlessChrome.LaunchAsync().ConfigureAwait(false);
        var origin = sample.Origin;
        var cookies = await this.SignInAsync(sample.OriginUri, "a@example.test").ConfigureAwait(false);
        var page = await chrome.OpenAsync().ConfigureAwait(false);
        foreach (var cookie in cookies)
            await page.SetCookieAsync(cookie.Name, cookie.Value, origin + "/").ConfigureAwait(false);
        await page.InstallScriptAsync(HandoffObserver).ConfigureAwait(false);
        Directory.CreateDirectory("/opt/cursor/artifacts");

        await this.AssertQuietHandoffAsync(protocol, page, origin + "/static", "Static", 0, "/opt/cursor/artifacts/handoff-static.png").ConfigureAwait(false);

        await this.AssertQuietHandoffAsync(protocol, page, origin + "/", "Server", 1, "/opt/cursor/artifacts/handoff-auto-server.png").ConfigureAwait(false);
        await WaitAsync(() => page.StorageAsync("blazor-resource-hash:BlazorAuto.Client"), value => !string.IsNullOrEmpty(value), TimeSpan.FromSeconds(45), "WASM resources were not cached").ConfigureAwait(false);
        await this.AssertQuietHandoffAsync(protocol, page, origin + "/", "WebAssembly", 1, "/opt/cursor/artifacts/handoff-auto-wasm.png").ConfigureAwait(false);

        await this.AssertQuietHandoffAsync(protocol, page, origin + "/server", "Server", 1, "/opt/cursor/artifacts/handoff-server.png").ConfigureAwait(false);
        var notes = protocol.Snapshot().Count(frame => frame.Kind == "notes");
        var tokens = page.TokenStatuses.Count;
        await page.ClickAsync("Rotate").ConfigureAwait(false);
        await WaitAsync(() => Task.FromResult<string?>(page.TokenStatuses.Count.ToString()), value => int.Parse(value ?? "0", System.Globalization.CultureInfo.InvariantCulture) > tokens, TimeSpan.FromSeconds(15), "rotation did not call the token endpoint").ConfigureAwait(false);
        await Task.Delay(1200).ConfigureAwait(false);
        var after = await this.ReadHandoffAsync(page).ConfigureAwait(false);
        after.ProfileLost.Should().BeFalse("rotation replaced the profile: " + after.Summary);
        after.NoteLost.Should().BeFalse("rotation replaced the notes: " + after.Summary);
        after.SignInSeen.Should().BeFalse("rotation showed the sign-in form: " + after.Summary);
        protocol.Snapshot().Count(frame => frame.Kind == "notes").Should().Be(notes, "rotation must not reload notes");
    }

    private async Task AssertQuietHandoffAsync(ProtocolHost protocol, ChromePage page, string url, string runtime, int tokenCalls, string screenshot)
    {
        var notesBefore = protocol.Snapshot().Count(frame => frame.Kind == "notes");
        await page.NavigateAsync(url).ConfigureAwait(false);
        await WaitTextAsync(page, text => text.Contains(runtime, StringComparison.Ordinal) && text.Contains("seed-note-a", StringComparison.Ordinal) && (tokenCalls == 0 || text.Contains("not started until interactive", StringComparison.Ordinal) || text.Contains("sdk-", StringComparison.Ordinal)), TimeSpan.FromSeconds(30), url + " did not settle on " + runtime, new StringBuilder(), protocol).ConfigureAwait(false);
        if (tokenCalls > 0)
            await WaitTextAsync(page, text => text.Contains("sdk-", StringComparison.Ordinal), TimeSpan.FromSeconds(20), url + " did not open realtime", new StringBuilder(), protocol).ConfigureAwait(false);
        await Task.Delay(1500).ConfigureAwait(false);
        await page.CaptureDocumentAsync().ConfigureAwait(false);
        var document = page.DocumentBody ?? "";
        document.Should().Contain("seed-note-a");
        document.Should().Contain("data-profile");
        document.Should().NotContain("access_token");
        document.Should().NotContain("refresh_token");
        var handoff = await this.ReadHandoffAsync(page).ConfigureAwait(false);
        handoff.ProfileSeen.Should().BeTrue("the prerendered profile never appeared at " + url + " " + handoff.Summary);
        handoff.NoteSeen.Should().BeTrue(handoff.Summary);
        handoff.ProfileLost.Should().BeFalse("profile disappeared during handoff at " + url + " " + handoff.Summary);
        handoff.NoteLost.Should().BeFalse("notes disappeared during handoff at " + url + " " + handoff.Summary);
        handoff.SignInSeen.Should().BeFalse("sign-in form replaced the session at " + url + " " + handoff.Summary);
        handoff.LoadingSeen.Should().BeFalse(handoff.Summary);
        page.TokenStatuses.Should().HaveCount(tokenCalls, "token endpoint calls at " + url + " were " + string.Join(",", page.TokenStatuses));
        if (tokenCalls > 0)
            page.TokenStatuses.Should().OnlyContain(status => status == 200);
        page.NotesStatuses.Should().BeEmpty("the browser refetched notes at " + url);
        (protocol.Snapshot().Count(frame => frame.Kind == "notes") - notesBefore).Should().Be(1, "prerender loads notes once and interactive startup must not load them again");
        await page.ScreenshotAsync(screenshot).ConfigureAwait(false);
    }

    private async Task<HandoffReport> ReadHandoffAsync(ChromePage page)
    {
        var json = await page.EvalAsync("JSON.stringify(window.__handoff || {missing:true})").ConfigureAwait(false);
        return JsonSerializer.Deserialize<HandoffReport>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new HandoffReport();
    }

    private const string HandoffObserver = """
        (() => {
          const state = { profileSeen:false, noteSeen:false, profileLost:false, noteLost:false, signInSeen:false, loadingSeen:false, events:[] };
          window.__handoff = state;
          const sample = () => {
            const profile = !!document.querySelector('[data-profile]');
            const note = !!document.querySelector('[data-note]');
            const signIn = !!document.querySelector('[data-signin]');
            const loading = !!document.querySelector('[data-loading]');
            if (profile) state.profileSeen = true;
            if (note) state.noteSeen = true;
            if (state.profileSeen && !profile) state.profileLost = true;
            if (state.noteSeen && !note) state.noteLost = true;
            if (state.profileSeen && signIn) state.signInSeen = true;
            if (loading) state.loadingSeen = true;
            const runtime = document.querySelector('[data-runtime]')?.getAttribute('data-runtime') || '';
            const last = state.events[state.events.length - 1];
            if (!last || last.profile !== profile || last.note !== note || last.signIn !== signIn || last.loading !== loading || last.runtime !== runtime)
              state.events.push({ profile, note, signIn, loading, runtime });
          };
          const start = () => {
            sample();
            new MutationObserver(sample).observe(document.documentElement, { childList:true, subtree:true, characterData:true, attributes:true });
          };
          if (document.documentElement) start();
          else addEventListener('DOMContentLoaded', start);
        })();
        """;

    private sealed class HandoffReport
    {
        public bool ProfileSeen { get; set; }

        public bool NoteSeen { get; set; }

        public bool ProfileLost { get; set; }

        public bool NoteLost { get; set; }

        public bool SignInSeen { get; set; }

        public bool LoadingSeen { get; set; }

        public List<HandoffEvent> Events { get; set; } = [];

        public string Summary => string.Join(" | ", this.Events.Select(item => $"{item.Runtime}:{item.Profile}/{item.Note}/sign={item.SignIn}"));
    }

    private sealed class HandoffEvent
    {
        public bool Profile { get; set; }

        public bool Note { get; set; }

        public bool SignIn { get; set; }

        public bool Loading { get; set; }

        public string Runtime { get; set; } = "";
    }

    private async Task<List<Cookie>> SignInAsync(Uri origin, string email)
    {
        var jar = new CookieContainer();
        using var handler = new HttpClientHandler { CookieContainer = jar, UseCookies = true };
        using var http = new HttpClient(handler) { BaseAddress = origin };
        var authority = origin.GetLeftPart(UriPartial.Authority);
        var csrfRequest = new HttpRequestMessage(HttpMethod.Get, "/auth/session/csrf");
        csrfRequest.Headers.TryAddWithoutValidation("Origin", authority);
        var csrfResponse = await http.SendAsync(csrfRequest).ConfigureAwait(false);
        csrfResponse.EnsureSuccessStatusCode();
        var token = (await csrfResponse.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false)).GetProperty("token").GetString();
        var signIn = new HttpRequestMessage(HttpMethod.Post, "/auth/session/signin");
        signIn.Headers.TryAddWithoutValidation("Origin", authority);
        signIn.Headers.TryAddWithoutValidation("RequestVerificationToken", token);
        signIn.Content = JsonContent.Create(new { email, password = "pw" });
        var response = await http.SendAsync(signIn).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        response.IsSuccessStatusCode.Should().BeTrue(body);
        return jar.GetCookies(origin).Cast<Cookie>().ToList();
    }

    private string Trace(ProtocolHost protocol) =>
        string.Join(Environment.NewLine, protocol.Snapshot().Select(frame => $"{frame.Kind} conn={frame.Connection} browser={frame.Browser} topic={frame.Topic} token={frame.AccessToken}"));

    private static async Task<string> WaitTextAsync(ChromePage page, Func<string, bool> match, TimeSpan timeout, string why, StringBuilder detail, ProtocolHost protocol)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var last = "";
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await page.TextAsync().ConfigureAwait(false);
            if (match(last))
                return last;
            await Task.Delay(250).ConfigureAwait(false);
        }

        detail.AppendLine(why);
        detail.AppendLine(last);
        detail.AppendLine(string.Join(Environment.NewLine, protocol.Snapshot().Select(frame => frame.Kind + " " + frame.Topic)));
        throw new AssertFailedException(why + "\n" + last);
    }

    private static async Task<ProtocolFrame> WaitFrameAsync(ProtocolHost protocol, Func<ProtocolFrame, bool> match, TimeSpan timeout, string why)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var found = protocol.Snapshot().LastOrDefault(match);
            if (found is not null)
                return found;
            await Task.Delay(200).ConfigureAwait(false);
        }

        var trace = string.Join(Environment.NewLine, protocol.Snapshot().Select(frame => $"{frame.Kind} conn={frame.Connection} browser={frame.Browser} topic={frame.Topic} token={frame.AccessToken}"));
        throw new AssertFailedException(why + "\n" + trace);
    }

    private static async Task WaitAsync(Func<Task<string?>> read, Func<string?, bool> match, TimeSpan timeout, string why)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (match(await read().ConfigureAwait(false)))
                return;
            await Task.Delay(250).ConfigureAwait(false);
        }

        throw new AssertFailedException(why);
    }

    private sealed class SampleProcess : IAsyncDisposable
    {
        private readonly Process process;

        private SampleProcess(Process process, Uri origin)
        {
            this.process = process;
            this.OriginUri = origin;
        }

        public Uri OriginUri { get; }

        public string Origin => this.OriginUri.GetLeftPart(UriPartial.Authority);

        public static async Task<SampleProcess> StartAsync(string protocolUrl)
        {
            var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                ? "Release"
                : "Debug";
            var dll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "sample", "BlazorAuto", "bin", configuration, "net10.0", "BlazorAuto.dll"));
            if (!File.Exists(dll))
                throw new FileNotFoundException("Sample app was not built.", dll);

            var port = FreePort();
            var error = new StringBuilder();
            var process = new Process
            {
                StartInfo = new ProcessStartInfo("dotnet", $"\"{dll}\" --urls http://127.0.0.1:{port}")
                {
                    WorkingDirectory = Path.GetDirectoryName(dll),
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                },
            };
            process.StartInfo.Environment["SUPABASE_URL"] = protocolUrl;
            process.StartInfo.Environment["SUPABASE_ANON_KEY"] = "sb_publishable_testkey";
            process.StartInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                    error.AppendLine(args.Data);
            };
            process.Start();
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            using var http = new HttpClient();
            for (var attempt = 0; attempt < 80; attempt++)
            {
                if (process.HasExited)
                    throw new InvalidOperationException("Sample exited.\n" + error);
                try
                {
                    var response = await http.GetAsync(new Uri($"http://127.0.0.1:{port}/")).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                        return new SampleProcess(process, new Uri($"http://127.0.0.1:{port}/"));
                }
                catch (HttpRequestException)
                {
                    // Still binding.
                }

                await Task.Delay(250).ConfigureAwait(false);
            }

            throw new InvalidOperationException("Sample did not start.\n" + error);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!this.process.HasExited)
                    this.process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // Already stopped.
            }

            await this.process.WaitForExitAsync().ConfigureAwait(false);
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
}
