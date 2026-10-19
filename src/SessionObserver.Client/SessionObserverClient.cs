using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Threading.Channels;
using log4net;
using SessionObserver.Protocol;
using SessionObserver.Protocol.Framing;
using SessionObserver.Protocol.Messages;
using SessionObserver.Protocol.Models;

namespace SessionObserver.Client;

/// <summary>
/// Talks to the Windows Sessions Observer service over its named pipe.
/// <para>
/// Use <see cref="ConnectAsync"/> for a single connection attempt (errors are thrown), or <see cref="Start"/>
/// to connect in the background and keep reconnecting. Subscriptions created with <see cref="SubscribeAsync"/>
/// survive reconnects: events missed while disconnected are fetched and delivered in order, without duplicates.
/// </para>
/// </summary>
public sealed class SessionObserverClient : IAsyncDisposable
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(SessionObserverClient));
    private const int GapFillPageSize = 1000;

    private readonly SessionObserverClientOptions _options;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly List<Subscriber> _subscribers = [];

    private Connection? _connection;
    private Task? _reconnectTask;
    private bool _reconnectRunning;
    private bool _reconnectEnabled;
    private bool _everConnected;
    private bool _disposed;
    private long _nextRequestId;
    private ConnectionState _state = ConnectionState.Disconnected;
    private SessionObserverException? _lastReportedError;
    private TaskCompletionSource _connectedSignal = NewSignal();

    public SessionObserverClient(SessionObserverClientOptions? options = null)
    {
        _options = options ?? new SessionObserverClientOptions();
    }

    public SessionObserverClientOptions Options => _options;

    public ConnectionState State
    {
        get { lock (_sync) { return _state; } }
    }

    /// <summary>The most recent connection failure, cleared on a successful connection.</summary>
    public SessionObserverException? LastError { get; private set; }

    /// <summary>Information the service reported during the last successful handshake.</summary>
    public PongResponse? ServerInfo { get; private set; }

    /// <summary>Raised on a thread-pool thread whenever the connection state changes.</summary>
    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    /// <summary>Makes one connection attempt and performs the handshake.</summary>
    /// <exception cref="SessionObserverException">The service is unavailable, access is denied, or versions differ.</exception>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        try
        {
            await ConnectCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SessionObserverException ex)
        {
            LastError = ex;
            SetState(ConnectionState.Disconnected, ex);
            throw;
        }
    }

    /// <summary>Connects in the background and reconnects whenever the connection is lost, until disposed.</summary>
    public void Start()
    {
        ThrowIfDisposed();
        lock (_sync)
        {
            _reconnectEnabled = true;
        }
        EnsureReconnectLoop();
    }

    /// <summary>Completes when the client is connected.</summary>
    public Task WaitUntilConnectedAsync(CancellationToken cancellationToken = default)
    {
        Task signal;
        lock (_sync)
        {
            signal = _connectedSignal.Task;
        }
        return signal.WaitAsync(cancellationToken);
    }

    public Task<PongResponse> PingAsync(CancellationToken cancellationToken = default) =>
        RequestAsync<PongResponse>(RequireConnection(), new PingRequest(), cancellationToken);

    /// <summary>Returns the sessions that currently exist on the machine.</summary>
    public async Task<IReadOnlyList<SessionInfo>> GetSessionsAsync(CancellationToken cancellationToken = default)
    {
        SessionsResult result = await RequestAsync<SessionsResult>(
            RequireConnection(), new GetSessionsRequest(), cancellationToken).ConfigureAwait(false);
        return result.Sessions;
    }

    /// <summary>
    /// Returns recorded events in ascending order: the newest <paramref name="max"/> events, or, when
    /// <paramref name="sinceSequence"/> is given, the oldest <paramref name="max"/> events after it.
    /// </summary>
    public Task<EventsResult> GetEventsAsync(
        long? sinceSequence = null, int max = 500, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(max);
        return RequestAsync<EventsResult>(
            RequireConnection(),
            new GetEventsRequest { SinceSequence = sinceSequence, Max = max },
            cancellationToken);
    }

    /// <summary>
    /// Streams session events. With <paramref name="afterSequence"/>, every event after that sequence is delivered
    /// (missed ones are fetched first); without it, delivery starts with events recorded from now on.
    /// Reconnects are transparent when the client reconnects automatically; otherwise the stream ends with a
    /// <see cref="SessionObserverException"/> when the connection is lost.
    /// </summary>
    public async IAsyncEnumerable<SessionEvent> SubscribeAsync(
        long? afterSequence = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var subscriber = new Subscriber(afterSequence);
        Connection? connection;
        bool reconnectEnabled;
        lock (_sync)
        {
            _subscribers.Add(subscriber);
            connection = _connection;
            reconnectEnabled = _reconnectEnabled;
        }

        try
        {
            if (connection is not null)
            {
                try
                {
                    await EnsureServerSubscriptionAsync(connection, cancellationToken).ConfigureAwait(false);
                    subscriber.RequestResync();
                }
                catch (SessionObserverException ex) when (reconnectEnabled && IsTransient(ex))
                {
                    Log.Debug("Subscription request failed; it will be renewed after reconnecting.", ex);
                }
            }
            else if (!reconnectEnabled)
            {
                throw new SessionObserverException(
                    SessionObserverErrorKind.ConnectionLost, "Not connected to the service.");
            }

            while (await subscriber.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (subscriber.Reader.TryRead(out SessionEvent? item))
                {
                    if (item is null)
                    {
                        List<SessionEvent> missed = await FillGapAsync(subscriber, cancellationToken).ConfigureAwait(false);
                        foreach (SessionEvent e in missed)
                        {
                            yield return e;
                        }
                    }
                    else if (subscriber.LastSequence is null || item.Sequence > subscriber.LastSequence)
                    {
                        subscriber.LastSequence = item.Sequence;
                        yield return item;
                    }
                }
            }

            if (subscriber.Error is not null)
            {
                throw subscriber.Error;
            }
        }
        finally
        {
            lock (_sync)
            {
                _subscribers.Remove(subscriber);
            }
            subscriber.Complete(null);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Connection? connection;
        Task? reconnectTask;
        Subscriber[] subscribers;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            connection = _connection;
            _connection = null;
            reconnectTask = _reconnectTask;
            subscribers = [.. _subscribers];
        }

        _disposeCts.Cancel();
        connection?.Close(new SessionObserverException(SessionObserverErrorKind.ConnectionLost, "The client was disposed."));
        foreach (Subscriber subscriber in subscribers)
        {
            subscriber.Complete(null);
        }

        await IgnoreFailures(reconnectTask).ConfigureAwait(false);
        await IgnoreFailures(connection?.ReadLoop).ConfigureAwait(false);

        SetState(ConnectionState.Disconnected, null);
        _disposeCts.Dispose();
        _connectGate.Dispose();
    }

    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_connection is not null)
                {
                    return;
                }
            }

            SetState(_everConnected ? ConnectionState.Reconnecting : ConnectionState.Connecting, LastError);
            Log.Debug($@"Connecting to \\{_options.ServerName}\pipe\{_options.PipeName}.");

            var pipe = new NamedPipeClientStream(
                _options.ServerName,
                _options.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous,
                TokenImpersonationLevel.Identification);
            try
            {
                await pipe.ConnectAsync((int)_options.ConnectTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw MapConnectException(ex);
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            var connection = new Connection(pipe);
            connection.ReadLoop = Task.Run(() => ReadLoopAsync(connection), CancellationToken.None);

            PongResponse pong;
            try
            {
                pong = await RequestAsync<PongResponse>(connection, new PingRequest(), cancellationToken).ConfigureAwait(false);
                if (pong.ServerProtocolVersion != ProtocolVersion.Current)
                {
                    throw new SessionObserverException(
                        SessionObserverErrorKind.ProtocolMismatch,
                        $"The service speaks protocol version {pong.ServerProtocolVersion}, " +
                        $"this client speaks version {ProtocolVersion.Current}.");
                }
            }
            catch (Exception ex)
            {
                connection.Close(ex as SessionObserverException
                    ?? new SessionObserverException(SessionObserverErrorKind.ConnectionLost, ex.Message, ex));
                if (ex is SessionObserverException { Kind: SessionObserverErrorKind.ServerError } server &&
                    server.Message.StartsWith(ErrorCodes.VersionMismatch, StringComparison.Ordinal))
                {
                    throw new SessionObserverException(SessionObserverErrorKind.ProtocolMismatch, server.Message, server);
                }
                throw;
            }

            Subscriber[] subscribers;
            lock (_sync)
            {
                if (connection.IsClosed || _disposed)
                {
                    throw new SessionObserverException(
                        SessionObserverErrorKind.ConnectionLost, "The connection was lost during the handshake.");
                }
                _connection = connection;
                _everConnected = true;
                if (_options.AutoReconnect)
                {
                    _reconnectEnabled = true;
                }
                subscribers = [.. _subscribers];
            }

            ServerInfo = pong;
            LastError = null;
            Log.Info($"Connected to the service on '{pong.MachineName}' " +
                     $"(service version {pong.ServerVersion}, protocol {pong.ServerProtocolVersion}).");

            if (subscribers.Length > 0)
            {
                try
                {
                    await EnsureServerSubscriptionAsync(connection, cancellationToken).ConfigureAwait(false);
                }
                catch (SessionObserverException ex) when (IsTransient(ex))
                {
                    Log.Warn("Renewing the event subscription failed.", ex);
                }
            }

            SetState(ConnectionState.Connected, null);
            foreach (Subscriber subscriber in subscribers)
            {
                subscriber.RequestResync();
            }
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task EnsureServerSubscriptionAsync(Connection connection, CancellationToken cancellationToken)
    {
        Task<SubscribedResponse> task;
        lock (_sync)
        {
            task = connection.SubscribeTask ??=
                RequestAsync<SubscribedResponse>(connection, new SubscribeRequest(), CancellationToken.None);
        }

        try
        {
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch when (task.IsFaulted)
        {
            lock (_sync)
            {
                if (ReferenceEquals(connection.SubscribeTask, task))
                {
                    connection.SubscribeTask = null;
                }
            }
            throw;
        }
    }

    private async Task<List<SessionEvent>> FillGapAsync(Subscriber subscriber, CancellationToken cancellationToken)
    {
        var missed = new List<SessionEvent>();
        try
        {
            Connection connection = RequireConnection();
            if (subscriber.LastSequence is null)
            {
                EventsResult latest = await RequestAsync<EventsResult>(
                    connection, new GetEventsRequest { Max = 0 }, cancellationToken).ConfigureAwait(false);
                subscriber.LastSequence = latest.LatestSequence;
                return missed;
            }

            while (true)
            {
                EventsResult page = await RequestAsync<EventsResult>(
                    connection,
                    new GetEventsRequest { SinceSequence = subscriber.LastSequence, Max = GapFillPageSize },
                    cancellationToken).ConfigureAwait(false);

                foreach (SessionEvent e in page.Events)
                {
                    if (e.Sequence > subscriber.LastSequence)
                    {
                        missed.Add(e);
                        subscriber.LastSequence = e.Sequence;
                    }
                }

                if (page.Events.Count < GapFillPageSize)
                {
                    break;
                }
            }

            if (missed.Count > 0)
            {
                Log.Info($"Recovered {missed.Count} event(s) recorded while not subscribed.");
            }
        }
        catch (SessionObserverException ex) when (IsTransient(ex))
        {
            Log.Debug("Gap fill interrupted; it will be retried after reconnecting.", ex);
        }
        return missed;
    }

    private async Task<TResponse> RequestAsync<TResponse>(
        Connection connection, ProtocolMessage request, CancellationToken cancellationToken)
        where TResponse : ProtocolMessage
    {
        string id = Interlocked.Increment(ref _nextRequestId).ToString(CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<ProtocolMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!connection.TryAddPending(id, completion))
        {
            throw new SessionObserverException(SessionObserverErrorKind.ConnectionLost, "Not connected to the service.");
        }

        try
        {
            try
            {
                await connection.Writer.WriteAsync(request with { RequestId = id }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                throw new SessionObserverException(
                    SessionObserverErrorKind.ConnectionLost, $"Sending to the service failed: {ex.Message}", ex);
            }

            ProtocolMessage response;
            try
            {
                response = await completion.Task.WaitAsync(_options.RequestTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                throw new SessionObserverException(
                    SessionObserverErrorKind.Timeout,
                    $"The service did not answer within {_options.RequestTimeout.TotalSeconds:0} seconds.", ex);
            }

            return response switch
            {
                TResponse typed => typed,
                ErrorResponse error => throw new SessionObserverException(
                    SessionObserverErrorKind.ServerError, $"{error.Code}: {error.Message}"),
                _ => throw new SessionObserverException(
                    SessionObserverErrorKind.ProtocolError,
                    $"Expected {typeof(TResponse).Name} but received {response.GetType().Name}."),
            };
        }
        finally
        {
            connection.RemovePending(id);
        }
    }

    private async Task ReadLoopAsync(Connection connection)
    {
        Exception? error = null;
        try
        {
            while (true)
            {
                ProtocolMessage? message = await connection.Reader.ReadAsync(connection.Cancellation).ConfigureAwait(false);
                if (message is null)
                {
                    break;
                }
                Dispatch(connection, message);
            }
        }
        catch (OperationCanceledException) when (connection.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            error = ex;
        }

        OnConnectionLost(connection, error);
    }

    private void Dispatch(Connection connection, ProtocolMessage message)
    {
        if (message is EventPush push)
        {
            Subscriber[] subscribers;
            lock (_sync)
            {
                subscribers = [.. _subscribers];
            }
            foreach (Subscriber subscriber in subscribers)
            {
                subscriber.Post(push.Event);
            }
            return;
        }

        if (message.RequestId is { } id && connection.Complete(id, message))
        {
            return;
        }

        Log.Debug($"Ignoring unexpected {message.GetType().Name} (request id '{message.RequestId}').");
    }

    private void OnConnectionLost(Connection connection, Exception? error)
    {
        var reason = new SessionObserverException(
            SessionObserverErrorKind.ConnectionLost,
            error is null
                ? "The service closed the connection."
                : $"The connection to the service was lost: {error.Message}",
            error);

        bool wasCurrent;
        bool reconnect;
        Subscriber[] subscribersToFail = [];
        lock (_sync)
        {
            connection.Close(reason);
            wasCurrent = ReferenceEquals(_connection, connection);
            if (!wasCurrent)
            {
                return;
            }
            _connection = null;
            _connectedSignal = NewSignal();
            reconnect = _reconnectEnabled && !_disposed;
            if (!reconnect)
            {
                subscribersToFail = [.. _subscribers];
            }
        }

        if (error is null)
        {
            Log.Info(reason.Message);
        }
        else
        {
            Log.Warn(reason.Message, error);
        }

        LastError = reason;
        if (reconnect)
        {
            SetState(ConnectionState.Reconnecting, reason);
            EnsureReconnectLoop();
        }
        else
        {
            SetState(ConnectionState.Disconnected, reason);
            foreach (Subscriber subscriber in subscribersToFail)
            {
                subscriber.Complete(reason);
            }
        }
    }

    private void EnsureReconnectLoop()
    {
        lock (_sync)
        {
            if (_disposed || _reconnectRunning)
            {
                return;
            }
            _reconnectRunning = true;
            _reconnectTask = Task.Run(ReconnectLoopAsync);
        }
    }

    private async Task ReconnectLoopAsync()
    {
        CancellationToken token = _disposeCts.Token;
        TimeSpan delay = _options.ReconnectMinDelay;
        bool failureLogged = false;

        while (true)
        {
            lock (_sync)
            {
                if (_connection is not null || _disposed || token.IsCancellationRequested)
                {
                    _reconnectRunning = false;
                    return;
                }
            }

            try
            {
                await ConnectCoreAsync(token).ConfigureAwait(false);
                delay = _options.ReconnectMinDelay;
                failureLogged = false;
                continue;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                continue;
            }
            catch (ObjectDisposedException)
            {
                continue;
            }
            catch (Exception ex)
            {
                SessionObserverException failure = ex as SessionObserverException
                    ?? new SessionObserverException(SessionObserverErrorKind.ConnectionLost, ex.Message, ex);
                LastError = failure;

                // Log the first failure of a streak as a warning; repeats only at debug level.
                if (!failureLogged)
                {
                    Log.Warn($"Cannot connect to the service ({failure.Kind}): {failure.Message} Retrying in the background.");
                    failureLogged = true;
                }
                else
                {
                    Log.Debug($"Reconnect attempt failed ({failure.Kind}): {failure.Message}");
                }

                SetState(_everConnected ? ConnectionState.Reconnecting : ConnectionState.Connecting, failure);
            }

            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                continue;
            }
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.ReconnectMaxDelay.Ticks));
        }
    }

    private void SetState(ConnectionState state, SessionObserverException? error)
    {
        lock (_sync)
        {
            if (_state == state && ReferenceEquals(_lastReportedError, error))
            {
                return;
            }
            _state = state;
            _lastReportedError = error;
            if (state == ConnectionState.Connected)
            {
                _connectedSignal.TrySetResult();
            }
        }

        try
        {
            ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(state, error));
        }
        catch (Exception ex)
        {
            Log.Error("A ConnectionStateChanged handler threw.", ex);
        }
    }

    private Connection RequireConnection()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _connection ?? throw new SessionObserverException(
                SessionObserverErrorKind.ConnectionLost, "Not connected to the service.");
        }
    }

    private void ThrowIfDisposed()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }

    private SessionObserverException MapConnectException(Exception ex) => ex switch
    {
        TimeoutException => new SessionObserverException(
            SessionObserverErrorKind.ServiceUnavailable,
            $"The Windows Sessions Observer service is not running (pipe '{_options.PipeName}' did not answer).", ex),
        UnauthorizedAccessException => new SessionObserverException(
            SessionObserverErrorKind.AccessDenied,
            "Access to the Windows Sessions Observer service was denied. Run as an administrator (elevated).", ex),
        IOException => new SessionObserverException(
            SessionObserverErrorKind.ServiceUnavailable, $"Cannot connect to the service: {ex.Message}", ex),
        _ => new SessionObserverException(
            SessionObserverErrorKind.ConnectionLost, $"Cannot connect to the service: {ex.Message}", ex),
    };

    private static bool IsTransient(SessionObserverException ex) =>
        ex.Kind is SessionObserverErrorKind.ConnectionLost or SessionObserverErrorKind.Timeout;

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task IgnoreFailures(Task? task)
    {
        if (task is null)
        {
            return;
        }
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // Shutting down: failures of background loops are irrelevant here.
        }
    }

    /// <summary>One pipe connection with its reader, writer and outstanding requests.</summary>
    private sealed class Connection
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<ProtocolMessage>> _pending = new();
        private readonly CancellationTokenSource _cts = new();
        private int _closed;

        public Connection(NamedPipeClientStream pipe)
        {
            Pipe = pipe;
            Reader = new MessageReader(pipe);
            Writer = new MessageWriter(pipe);
        }

        public NamedPipeClientStream Pipe { get; }
        public MessageReader Reader { get; }
        public MessageWriter Writer { get; }
        public Task? ReadLoop { get; set; }

        /// <summary>Server-side subscription for this connection; guarded by the client's lock.</summary>
        public Task<SubscribedResponse>? SubscribeTask { get; set; }

        public CancellationToken Cancellation => _cts.Token;
        public bool IsClosed => Volatile.Read(ref _closed) != 0;

        public bool TryAddPending(string id, TaskCompletionSource<ProtocolMessage> completion)
        {
            if (IsClosed)
            {
                return false;
            }
            _pending[id] = completion;
            if (IsClosed)
            {
                _pending.TryRemove(id, out _);
                return false;
            }
            return true;
        }

        public void RemovePending(string id) => _pending.TryRemove(id, out _);

        public bool Complete(string id, ProtocolMessage message) =>
            _pending.TryRemove(id, out TaskCompletionSource<ProtocolMessage>? completion) &&
            completion.TrySetResult(message);

        public void Close(Exception reason)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
            {
                return;
            }
            foreach (KeyValuePair<string, TaskCompletionSource<ProtocolMessage>> pending in _pending)
            {
                pending.Value.TrySetException(reason);
            }
            _pending.Clear();
            _cts.Cancel();
            Pipe.Dispose();
        }
    }

    /// <summary>
    /// Local consumer of pushed events. A null item in the channel requests a resync (gap fill).
    /// </summary>
    private sealed class Subscriber
    {
        private readonly Channel<SessionEvent?> _channel =
            Channel.CreateUnbounded<SessionEvent?>(new UnboundedChannelOptions { SingleReader = true });

        public Subscriber(long? afterSequence)
        {
            LastSequence = afterSequence;
        }

        public long? LastSequence { get; set; }
        public SessionObserverException? Error { get; private set; }
        public ChannelReader<SessionEvent?> Reader => _channel.Reader;

        public void Post(SessionEvent e) => _channel.Writer.TryWrite(e);
        public void RequestResync() => _channel.Writer.TryWrite(null);

        public void Complete(SessionObserverException? error)
        {
            if (error is not null)
            {
                Error ??= error;
            }
            _channel.Writer.TryComplete();
        }
    }
}
