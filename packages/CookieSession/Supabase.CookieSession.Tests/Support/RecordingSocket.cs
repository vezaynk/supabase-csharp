using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Supabase.Realtime.Sockets;
using Websocket.Client;

namespace Supabase.CookieSession.Tests.Support;

internal sealed class RecordingFactory : IWebSocketFactory
{
    public RecordingSocket Socket { get; } = new();

    public Uri? Uri { get; private set; }

    public IWebsocketClient Create(Uri uri, Func<Dictionary<string, string>> headers)
    {
        this.Uri = uri;
        return this.Socket;
    }
}

/// <summary>In-memory transport for the real Realtime client. Replies to join and heartbeat so channels can open.</summary>
internal sealed class RecordingSocket : IWebsocketClient
{
    private readonly Replay messages = new();
    private readonly Replay reconnections = new();
    private readonly List<string> frames = [];

    public IReadOnlyList<string> Frames
    {
        get
        {
            lock (this.frames)
                return this.frames.ToArray();
        }
    }

    public bool IsRunning { get; set; }

    public bool IsReconnectionEnabled { get; set; } = true;

    public IObservable<ResponseMessage> MessageReceived => this.messages;

    public IObservable<ReconnectionInfo> ReconnectionHappened => this.reconnections;

    public IObservable<DisconnectionInfo> DisconnectionHappened { get; } = new Replay();

    public TimeSpan? ReconnectTimeout { get; set; }

    public TimeSpan? ErrorReconnectTimeout { get; set; }

    public Task StartOrFail()
    {
        this.IsRunning = true;
        this.reconnections.Next(ReconnectionInfo.Create(ReconnectionType.Initial));
        return Task.CompletedTask;
    }

    public bool Send(string message)
    {
        lock (this.frames)
            this.frames.Add(message);

        var node = JsonNode.Parse(message);
        var name = node?["event"]?.GetValue<string>();
        if (name is "phx_join" or "heartbeat")
        {
            var topic = node?["topic"]?.GetValue<string>() ?? "phoenix";
            var reference = node?["ref"]?.GetValue<string>() ?? "";
            this.Receive($"{{\"topic\":\"{topic}\",\"event\":\"phx_reply\",\"ref\":\"{reference}\",\"payload\":{{\"status\":\"ok\",\"response\":{{}}}}}}");
        }

        return true;
    }

    public void Receive(string json) => this.messages.Next(ResponseMessage.TextMessage(json));

    public void Recover() => this.reconnections.Next(ReconnectionInfo.Create(ReconnectionType.Lost));

    public Task<bool> Stop(WebSocketCloseStatus status, string reason)
    {
        this.IsRunning = false;
        this.IsReconnectionEnabled = false;
        return Task.FromResult(true);
    }

    public void Dispose()
    {
    }

    public Uri Url { get; set; } = new("ws://unused/socket");

    public TimeSpan ConnectTimeout { get; set; }

    public TimeSpan? LostReconnectTimeout { get; set; }

    public string? Name { get; set; }

    public bool IsStarted => this.IsRunning;

    public bool TextSenderRunning => false;

    public bool BinarySenderRunning => false;

    public bool IsInsideLock => false;

    public bool IsTextMessageConversionEnabled { get; set; }

    public bool IsStreamDisposedAutomatically { get; set; }

    public System.Text.Encoding? MessageEncoding { get; set; } = System.Text.Encoding.UTF8;

    public ClientWebSocket NativeClient => throw new NotSupportedException();

    public Task Start() => this.StartOrFail();

    public Task<bool> StopOrFail(WebSocketCloseStatus status, string reason) => this.Stop(status, reason);

    public Task Reconnect() => Task.CompletedTask;

    public Task ReconnectOrFail() => Task.CompletedTask;

    public bool Send(byte[] message) => false;

    public bool Send(ArraySegment<byte> message) => false;

    public bool Send(System.Buffers.ReadOnlySequence<byte> message) => false;

    public Task SendInstant(string message) => Task.CompletedTask;

    public Task SendInstant(byte[] message) => Task.CompletedTask;

    public bool SendAsText(byte[] message) => false;

    public bool SendAsText(ArraySegment<byte> message) => false;

    public bool SendAsText(System.Buffers.ReadOnlySequence<byte> message) => false;

    public void StreamFakeMessage(ResponseMessage message) => this.messages.Next(message);

    private sealed class Replay : IObservable<ResponseMessage>, IObservable<ReconnectionInfo>, IObservable<DisconnectionInfo>
    {
        private readonly List<IObserver<ResponseMessage>> messages = [];
        private readonly List<IObserver<ReconnectionInfo>> reconnections = [];
        private readonly List<IObserver<DisconnectionInfo>> disconnections = [];

        public void Next(ResponseMessage message) => this.Publish(this.messages, message);

        public void Next(ReconnectionInfo info) => this.Publish(this.reconnections, info);

        public IDisposable Subscribe(IObserver<ResponseMessage> observer) => this.Add(this.messages, observer);

        public IDisposable Subscribe(IObserver<ReconnectionInfo> observer) => this.Add(this.reconnections, observer);

        IDisposable IObservable<DisconnectionInfo>.Subscribe(IObserver<DisconnectionInfo> observer) => this.Add(this.disconnections, observer);

        private IDisposable Add<T>(List<IObserver<T>> observers, IObserver<T> observer)
        {
            lock (observers)
                observers.Add(observer);
            return new Removal(() =>
            {
                lock (observers)
                    observers.Remove(observer);
            });
        }

        private void Publish<T>(List<IObserver<T>> observers, T value)
        {
            IObserver<T>[] copy;
            lock (observers)
                copy = observers.ToArray();
            foreach (var observer in copy)
                observer.OnNext(value);
        }

        private sealed class Removal(Action remove) : IDisposable
        {
            public void Dispose() => remove();
        }
    }
}
