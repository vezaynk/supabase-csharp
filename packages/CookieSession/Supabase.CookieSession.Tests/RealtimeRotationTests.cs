using System.Text.Json.Nodes;
using FluentAssertions;
using Supabase.CookieSession.Tests.Support;

namespace Supabase.CookieSession.Tests;

/// <summary>
/// These tests drive the shipping Realtime client. Interactive Server and WASM both construct
/// <see cref="SupabaseMemoryClient"/>; the socket factory here stands in for ClientWebSocket.
/// WASM's production factory is the SDK default, which uses the browser WebSocket. There is no relay.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class RealtimeRotationTests
{
    [TestMethod]
    public async Task Subscribe_ShouldPushTheRotatedTokenAndRejoinWithIt_GivenTheSdkSocket()
    {
        var factory = new RecordingFactory();
        var (client, store) = this.Client(factory);
        store.Set("access-1", this.Profile());

        await client.SubscribeAsync("notes", _ => Task.CompletedTask);

        factory.Uri.Should().NotBeNull();
        factory.Uri!.Host.Should().Be("project.test");
        factory.Uri.Scheme.Should().Be("ws");
        factory.Uri.AbsolutePath.Should().Contain("/realtime/v1/websocket");
        factory.Uri.Query.Should().Contain("apikey=sb_publishable_testkey");
        factory.Uri.Query.Should().NotContain("access-1");
        this.Tokens(factory, "phx_join").Should().Contain("access-1");

        var beforeRotation = factory.Socket.Frames.Count;
        store.Set("access-2", this.Profile());
        await Waits.Until(() => factory.Socket.Frames.Skip(beforeRotation).Any(frame => frame.Contains("access-2", StringComparison.Ordinal)));
        factory.Socket.Frames.Skip(beforeRotation).Should().Contain(frame =>
            frame.Contains("\"event\":\"access_token\"", StringComparison.Ordinal) && frame.Contains("access-2", StringComparison.Ordinal));

        var beforeReconnect = factory.Socket.Frames.Count;
        factory.Socket.Recover();
        await Waits.Until(() => factory.Socket.Frames.Skip(beforeReconnect).Any(frame => frame.Contains("phx_join", StringComparison.Ordinal)));
        var rejoins = factory.Socket.Frames.Skip(beforeReconnect).Where(frame => frame.Contains("phx_join", StringComparison.Ordinal)).ToArray();
        this.Tokens(rejoins, "phx_join").Should().Contain("access-2").And.NotContain("access-1");
        client.SdkRefreshIsDisabled.Should().BeTrue();
    }

    [TestMethod]
    public async Task Subscribe_ShouldNotDeliverAccessTokenFramesOrCrossUsers_GivenTwoClients()
    {
        var firstFactory = new RecordingFactory();
        var secondFactory = new RecordingFactory();
        var (first, firstStore) = this.Client(firstFactory);
        var (second, secondStore) = this.Client(secondFactory, "http://other.test");
        var seen = new List<string>();
        firstStore.Set("access-user-a", this.Profile());
        secondStore.Set("access-user-b", this.Profile());

        await first.SubscribeAsync("notes", message =>
        {
            lock (seen)
                seen.Add(message);
            return Task.CompletedTask;
        });
        await second.SubscribeAsync("notes", _ => Task.CompletedTask);
        await first.SubscribeAsync("notes", _ => Task.CompletedTask);

        first.Client.Realtime.Subscriptions.Count.Should().Be(1, "the SDK returns the existing channel for a topic");
        string.Join("\n", secondFactory.Socket.Frames).Should().NotContain("access-user-a");
        string.Join("\n", firstFactory.Socket.Frames).Should().NotContain("access-user-b");

        firstFactory.Socket.Receive("""{"topic":"realtime:notes","event":"access_token","payload":{"access_token":"access-user-a"},"ref":"1"}""");
        firstFactory.Socket.Receive("""{"topic":"realtime:notes","event":"broadcast","payload":{"event":"ping"},"ref":"2"}""");
        await Waits.Until(() => seen.Contains("broadcast"));
        string.Join("\n", seen).Should().NotContain("access-user-a");

        first.Stop();
        firstFactory.Socket.IsRunning.Should().BeFalse();
        var afterStop = seen.Count;
        firstFactory.Socket.Receive("""{"topic":"realtime:notes","event":"broadcast","payload":{},"ref":"3"}""");
        await Task.Delay(50);
        seen.Count.Should().Be(afterStop);
    }

    [TestMethod]
    public async Task GetNotes_ShouldSendTheUserAccessToken_GivenAPostgrestCall()
    {
        var handler = new RecordingHandler("""[{"id":7,"body":"hello"}]""");
        var http = new HttpClient(handler);
        var (client, store) = this.Client(new RecordingFactory(), httpClient: http);
        store.Set("access-user-a", this.Profile());

        var notes = await client.GetNotesAsync();

        notes.Should().ContainSingle(note => note.Id == 7 && note.Body == "hello");
        handler.LastAuthorization.Should().Be("Bearer access-user-a");
        handler.LastApiKey.Should().Be("sb_publishable_testkey");
        handler.LastUri!.Host.Should().Be("project.test");
        handler.LastUri.AbsolutePath.Should().Contain("/notes");
    }

    private (SupabaseMemoryClient Client, MemoryAccessTokenStore Store) Client(RecordingFactory factory, string? url = null, HttpClient? httpClient = null)
    {
        var store = new MemoryAccessTokenStore();
        var client = new SupabaseMemoryClient(new SupabaseMemoryOptions
        {
            SupabaseUrl = url ?? "http://project.test",
            AnonKey = "sb_publishable_testkey",
            WebSocketFactory = factory,
            HttpClient = httpClient,
            HeartbeatInterval = TimeSpan.FromHours(1),
        }, store);
        return (client, store);
    }

    private SessionProfile Profile() => new()
    {
        UserId = "user-a",
        Email = "a@example.test",
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(2),
    };

    private IEnumerable<string> Tokens(RecordingFactory factory, string eventName) => this.Tokens(factory.Socket.Frames, eventName);

    private IEnumerable<string> Tokens(IEnumerable<string> frames, string eventName)
    {
        foreach (var frame in frames)
        {
            var node = JsonNode.Parse(frame);
            if (node?["event"]?.GetValue<string>() != eventName)
                continue;
            var token = node["payload"]?["access_token"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(token))
                yield return token;
        }
    }
}
