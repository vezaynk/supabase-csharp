using System.Net.WebSockets;
using Supabase.Realtime.Interfaces;

namespace Supabase.CookieSession;

/// <summary>
/// One Supabase client whose only credential is an in-memory access token.
/// SDK session persistence stays the default no-op, and <c>AutoRefreshToken</c> stays off
/// so GoTrue's timer does not redeem refresh tokens this client does not have.
/// Realtime uses <see cref="IRealtimeClient{TSocket,TChannel}.SetAuth"/>, which the SDK
/// already pushes to joined channels and reads again on rejoin.
/// </summary>
public sealed class SupabaseMemoryClient : IAsyncDisposable
{
    private readonly MemoryAccessTokenStore store;
    private readonly string anonKey;
    private readonly Dictionary<string, ISdkSubscription> subscriptions = new(StringComparer.Ordinal);
    private readonly object subscriptionGate = new();
    private int stopped;

    public SupabaseMemoryClient(SupabaseMemoryOptions options, MemoryAccessTokenStore store)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(store);
        if (string.IsNullOrWhiteSpace(options.SupabaseUrl))
            throw new CookieSessionException("CookieSession SupabaseUrl is not configured.");
        if (string.IsNullOrWhiteSpace(options.AnonKey))
            throw new CookieSessionException("CookieSession AnonKey is not configured.");

        this.store = store;
        this.anonKey = options.AnonKey;
        this.Client = new Supabase.Client(options.SupabaseUrl, options.AnonKey, new SupabaseOptions
        {
            AutoRefreshToken = false,
            AutoConnectRealtime = false,
            HttpClient = options.HttpClient,
            WebSocketFactory = options.WebSocketFactory,
        });
        if (options.HeartbeatInterval is { } heartbeat)
            this.Client.Realtime.Options.HeartbeatInterval = heartbeat;

        this.Client.Postgrest.GetHeaders = this.Headers;
        this.Client.Storage.GetHeaders = this.Headers;
        this.Client.Functions.GetHeaders = this.Headers;
        this.Client.Auth.GetHeaders = this.Headers;
        this.Client.Realtime.GetHeaders = () =>
        {
            var headers = this.Headers();
            headers.Remove("Authorization");
            return headers;
        };
        this.store.Changed += this.PushToken;
    }

    public Supabase.Client Client { get; }

    public string Transport => OperatingSystem.IsBrowser() ? "sdk-browser-websocket" : "sdk-server-websocket";

    public void PushToken()
    {
        var accessToken = this.store.AccessToken;
        if (!string.IsNullOrEmpty(accessToken))
            this.Client.Realtime.SetAuth(accessToken);
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        this.stopped = 0;
        this.PushToken();
        if (this.Client.Realtime.Socket is null)
            await this.Client.Realtime.ConnectAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        this.PushToken();
    }

    public async Task<IReadOnlyList<NoteRow>> GetNotesAsync(CancellationToken cancellationToken = default)
    {
        var response = await this.Client.From<Note>().Get(cancellationToken).ConfigureAwait(false);
        return response.Models.Select(note => new NoteRow { Id = note.Id, Body = note.Body }).ToArray();
    }

    public async Task<ISdkSubscription> SubscribeAsync(string topic, Func<string, Task> onEvent, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(topic);
        ArgumentNullException.ThrowIfNull(onEvent);
        await this.ConnectAsync(cancellationToken).ConfigureAwait(false);

        lock (this.subscriptionGate)
        {
            if (this.subscriptions.TryGetValue(topic, out var existing))
                return existing;
        }

        var channel = this.Client.Realtime.Channel(topic);
        var subscription = new SdkSubscription(this, topic, channel);
        channel.AddMessageReceivedHandler((sender, message) =>
        {
            if (Volatile.Read(ref this.stopped) == 1 || message._event == "access_token")
                return;

            _ = onEvent(message._event ?? "message");
        });
        await channel.Subscribe().WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (this.subscriptionGate)
            this.subscriptions[topic] = subscription;
        return subscription;
    }

    public void Stop()
    {
        Interlocked.Exchange(ref this.stopped, 1);
        ISdkSubscription[] open;
        lock (this.subscriptionGate)
        {
            open = this.subscriptions.Values.ToArray();
            this.subscriptions.Clear();
        }

        foreach (var subscription in open)
            subscription.DisposeAsync().AsTask().GetAwaiter().GetResult();

        try
        {
            this.Client.Realtime.Disconnect(WebSocketCloseStatus.NormalClosure, "signed out");
        }
        catch (Exception)
        {
            // The socket is already gone. Sign-out still clears memory.
        }
    }

    internal void Forget(string topic)
    {
        lock (this.subscriptionGate)
            this.subscriptions.Remove(topic);
    }

    public ValueTask DisposeAsync()
    {
        this.store.Changed -= this.PushToken;
        this.Stop();
        return ValueTask.CompletedTask;
    }

    /// <summary>True when the GoTrue client was left without a session and without its refresh timer.</summary>
    public bool SdkRefreshIsDisabled
    {
        get
        {
            var auth = (Supabase.Gotrue.Client) this.Client.Auth;
            return !auth.Options.AutoRefreshToken && auth.TokenRefresh is null && auth.CurrentSession is null;
        }
    }

    private Dictionary<string, string> Headers()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["apikey"] = this.anonKey,
        };
        var accessToken = this.store.AccessToken;
        headers["Authorization"] = string.IsNullOrEmpty(accessToken)
            ? $"Bearer {this.anonKey}"
            : $"Bearer {accessToken}";
        return headers;
    }

    private sealed class SdkSubscription : ISdkSubscription
    {
        private readonly SupabaseMemoryClient owner;
        private readonly Supabase.Realtime.RealtimeChannel channel;
        private int disposed;

        public SdkSubscription(SupabaseMemoryClient owner, string topic, Supabase.Realtime.RealtimeChannel channel)
        {
            this.owner = owner;
            this.Topic = topic;
            this.channel = channel;
        }

        public string Transport => this.owner.Transport;

        public string Topic { get; }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) == 1)
                return ValueTask.CompletedTask;

            try
            {
                this.channel.Unsubscribe();
            }
            catch (Exception)
            {
                // Leaving a closed socket is still a successful local dispose.
            }

            this.owner.Forget(this.Topic);
            return ValueTask.CompletedTask;
        }
    }
}
