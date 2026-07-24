#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Phoenix
{
    public sealed class Socket : IDisposable
    {
        private volatile bool _disposed;
        public delegate void OnClosedDelegate(ushort code, string message);

        public delegate void OnErrorDelegate(string message);

        public delegate void OnMessageDelegate(Message message);


        public delegate void OnOpenDelegate();

        private sealed class PendingConnectWaiter
        {
            private readonly Action<Exception> _finishWithException;
            private readonly Func<bool> _tryClaim;

            public PendingConnectWaiter(
                Func<bool> tryClaim,
                Action<Exception> finishWithException
            )
            {
                _tryClaim = tryClaim;
                _finishWithException = finishWithException;
            }

            public bool TryClaim()
            {
                return _tryClaim();
            }

            public void FinishWithException(Exception exception)
            {
                _finishWithException(exception);
            }
        }


        /**
         * In PhoenixJS, listening to socket events is done by passing a callback and
         * holding the returned reference string in order to unsubscribe later.
         *
         * In C#, delegates are much more convenient and fit the paradigm better. Hence,
         * we simple use delegate +=, -= to subscribe and unsubscribe.
         */
        // private readonly Dictionary<Event, List<Subscription>> stateChangeCallbacks = new();
        private readonly List<Channel> _channels = new List<Channel>();
        private readonly object _channelsLock = new object();
        private readonly HashSet<PendingConnectWaiter> _pendingConnectWaiters =
            new HashSet<PendingConnectWaiter>();
        private readonly object _pendingConnectWaitersLock = new object();

        // TODO: binaryType?

        // private uint connectClock = 1;
        private readonly string _endPoint;
        private readonly Dictionary<string, string>? _params;
        private readonly Scheduler? _reconnectTimer;

        private readonly IWebsocketFactory _websocketFactory;
        internal readonly Options Opts;

        internal readonly List<Func<bool>> SendBuffer = new List<Func<bool>>();
        private readonly object _sendBufferLock = new object();
        private bool _isFlushingSendBuffer;
        private bool _sendBufferFlushRequested;

        private readonly object _heartbeatStateLock = new object();
        private bool _closeWasClean;
        private IWebsocket? _conn;
        private long _heartbeatGeneration;
        private IDelayedExecution? _heartbeatTimer;
        private string? _pendingHeartbeatRef;
        private long _ref;

        public OnClosedDelegate? OnClose;

        public OnErrorDelegate? OnError;

        public OnMessageDelegate? OnMessage;

        public OnOpenDelegate? OnOpen;

        public Socket(
            string endPoint,
            Dictionary<string, string>? @params,
            IWebsocketFactory websocketFactory,
            Options opts
        )
        {
            if (endPoint == null)
                throw new ArgumentNullException(nameof(endPoint));
            if (string.IsNullOrWhiteSpace(endPoint))
                throw new ArgumentException("Endpoint URL cannot be empty or whitespace.", nameof(endPoint));
            if (websocketFactory == null)
                throw new ArgumentNullException(nameof(websocketFactory));
            if (opts == null)
                throw new ArgumentNullException(nameof(opts));

            _endPoint = endPoint;
            _params = @params == null
                ? null
                : new Dictionary<string, string>(@params, @params.Comparer);
            _websocketFactory = websocketFactory;
            Opts = opts;

            if (Opts.ReconnectAfter != null)
            {
                _reconnectTimer = new Scheduler(
                    () => Teardown(Connect),
                    Opts.ReconnectAfter,
                    Opts.DelayedExecutor
                );
            }
        }

        public IWebsocket? Conn => Volatile.Read(ref _conn);

        // convenience
        public WebsocketState? State => Conn?.State;

        // NOTE: ReplaceTransport functionality not support in this library

        // NOTE: Protocol inference not support in C# client

        private Uri EndPointUrl()
        {
            var @params = _params == null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(_params, _params.Comparer);
            @params["vsn"] = Opts.Vsn;

            var stringParams = @params
                .Select(pair =>
                    $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value ?? string.Empty)}")
                .ToArray();

            var builder = new UriBuilder($"{_endPoint}/websocket")
            {
                Query = string.Join("&", stringParams)
            };

            return builder.Uri;
        }

        public void Disconnect(Action? callback = null, ushort? code = null, string? reason = null)
        {
            // connectClock++;
            List<PendingConnectWaiter> connectWaiters;
            lock (_pendingConnectWaitersLock)
            {
                Volatile.Write(ref _closeWasClean, true);
                connectWaiters = ClaimPendingConnectWaitersLocked();
            }

            foreach (var connectWaiter in connectWaiters)
            {
                connectWaiter.FinishWithException(
                    new Exception("Connection failed: socket is disconnecting")
                );
            }

            _reconnectTimer?.Reset();
            Teardown(callback, code, reason);
        }

        public void Connect()
        {
            ConnectAndGetException();
        }

        private Exception? ConnectAndGetException()
        {
            if (_disposed)
            {
                return new ObjectDisposedException(nameof(Socket), "Cannot connect disposed socket");
            }

            // connectClock++;
            var connection = Conn;
            if (connection != null)
            {
                if (connection.State != WebsocketState.Closed)
                {
                    return null;
                }

                if (!TryClearConnection(connection))
                {
                    return null;
                }
            }

            Volatile.Write(ref _closeWasClean, false);
            connection = null;

            try
            {
                var callbacksEnabled = 0;
                var config = new WebsocketConfiguration
                {
                    uri = EndPointUrl(),
                    onOpenCallback = websocket =>
                    {
                        if (Volatile.Read(ref callbacksEnabled) != 0)
                        {
                            OnConnOpen(websocket);
                        }
                    },
                    onCloseCallback = (websocket, code, reason) =>
                    {
                        if (Volatile.Read(ref callbacksEnabled) != 0)
                        {
                            OnConnClose(websocket, code, reason);
                        }
                    },
                    onErrorCallback = (websocket, error) =>
                    {
                        if (Volatile.Read(ref callbacksEnabled) != 0)
                        {
                            OnConnError(websocket, error);
                        }
                    },
                    onMessageCallback = (websocket, message) =>
                    {
                        if (Volatile.Read(ref callbacksEnabled) != 0)
                        {
                            OnConnMessage(websocket, message);
                        }
                    }
                };

                connection = _websocketFactory.Build(config);
                var existingConnection = Interlocked.CompareExchange(
                    ref _conn,
                    connection,
                    null
                );
                if (existingConnection != null)
                {
                    if (!ReferenceEquals(existingConnection, connection))
                    {
                        CloseUnclaimedConnection(connection);
                    }

                    return null;
                }

                Volatile.Write(ref callbacksEnabled, 1);
                connection.Connect();
                return null;
            }
            catch (Exception ex)
            {
                if (HasLogger())
                {
                    Log(LogLevel.Error, "transport", $"WebSocket connect failed: {ex.Message}");
                }

                if (connection != null)
                {
                    TryClearConnection(connection);
                }
                _reconnectTimer?.ScheduleTimeout();
                return ex;
            }
        }

        /// <summary>
        /// Connects to the Phoenix server asynchronously.
        /// </summary>
        /// <remarks>
        /// The task completes immediately when the socket is already open and faults
        /// immediately for terminal failures such as disposal or explicit disconnection.
        /// When reconnection is configured, the task remains pending while retries
        /// continue and may never complete if the retry chain never succeeds or ends.
        /// Use <paramref name="cancellationToken"/> to stop waiting independently.
        /// </remarks>
        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (_disposed)
            {
                return Task.FromException(
                    new ObjectDisposedException(nameof(Socket), "Cannot connect disposed socket")
                );
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completionClaimed = 0;
            CancellationTokenRegistration cancellationRegistration = default;
            PendingConnectWaiter? pendingConnectWaiter = null;

            bool TryClaimCompletion()
            {
                return Interlocked.CompareExchange(ref completionClaimed, 1, 0) == 0;
            }

            void Cleanup()
            {
                if (pendingConnectWaiter != null)
                {
                    lock (_pendingConnectWaitersLock)
                    {
                        _pendingConnectWaiters.Remove(pendingConnectWaiter);
                    }
                }

                OnOpen -= OnOpenHandler;
                OnError -= OnErrorHandler;
                OnClose -= OnCloseHandler;
                cancellationRegistration.Dispose();
            }

            void FinishSuccessfully()
            {
                Cleanup();
                tcs.SetResult(true);
            }

            void FinishWithException(Exception exception)
            {
                Cleanup();
                tcs.SetException(exception);
            }

            void FinishWithCancellation()
            {
                Cleanup();
                tcs.SetCanceled();
            }

            void CompleteSuccessfully()
            {
                if (!TryClaimCompletion())
                {
                    return;
                }

                FinishSuccessfully();
            }

            void CompleteWithException(Exception exception)
            {
                if (!TryClaimCompletion())
                {
                    return;
                }

                FinishWithException(exception);
            }

            void CompleteWithCancellation()
            {
                if (!TryClaimCompletion())
                {
                    return;
                }

                FinishWithCancellation();
            }

            void OnOpenHandler()
            {
                CompleteSuccessfully();
            }

            void OnErrorHandler(string error)
            {
                CompleteWithException(new Exception($"Connection failed: {error}"));
            }

            void OnCloseHandler(ushort code, string reason)
            {
                if (ShouldReconnectAfterClose(code))
                {
                    return;
                }

                CompleteWithException(
                    new Exception($"Connection failed: connection closed before opening ({code} {reason})")
                );
            }

            pendingConnectWaiter = new PendingConnectWaiter(
                TryClaimCompletion,
                FinishWithException
            );
            var registered = false;
            lock (_pendingConnectWaitersLock)
            {
                if (!_disposed)
                {
                    OnOpen += OnOpenHandler;
                    OnError += OnErrorHandler;
                    OnClose += OnCloseHandler;
                    _pendingConnectWaiters.Add(pendingConnectWaiter);
                    registered = true;
                }
            }

            if (!registered)
            {
                if (pendingConnectWaiter.TryClaim())
                {
                    pendingConnectWaiter.FinishWithException(
                        new ObjectDisposedException(nameof(Socket), "Cannot connect disposed socket")
                    );
                }

                return tcs.Task;
            }

            try
            {
                cancellationRegistration = cancellationToken.Register(CompleteWithCancellation);
            }
            catch (Exception ex)
            {
                CompleteWithException(
                    new Exception($"Connection failed: {ex.Message}", ex)
                );
                return tcs.Task;
            }

            if (Volatile.Read(ref completionClaimed) != 0)
            {
                Cleanup();
                return tcs.Task;
            }

            try
            {
                var state = State;
                var disconnecting =
                    Conn != null && Volatile.Read(ref _closeWasClean);
                if (state == WebsocketState.Open && !disconnecting)
                {
                    CompleteSuccessfully();
                }
                else if (disconnecting)
                {
                    CompleteWithException(
                        new Exception("Connection failed: socket is disconnecting")
                    );
                }
                else
                {
                    var connectException = ConnectAndGetException();
                    if (_disposed)
                    {
                        CompleteWithException(
                            new ObjectDisposedException(nameof(Socket), "Cannot connect disposed socket")
                        );
                    }
                    else if (connectException != null && _reconnectTimer == null)
                    {
                        CompleteWithException(
                            new Exception($"Connection failed: {connectException.Message}", connectException)
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                if (_disposed)
                {
                    CompleteWithException(
                        new ObjectDisposedException(nameof(Socket), "Cannot connect disposed socket")
                    );
                }
                else
                {
                    CompleteWithException(
                        new Exception($"Connection failed: {ex.Message}", ex)
                    );
                }
            }

            return tcs.Task;
        }

        /// <summary>
        /// Disconnects from the Phoenix server asynchronously.
        /// </summary>
        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completionClaimed = 0;
            CancellationTokenRegistration cancellationRegistration = default;

            void CompleteSuccessfully()
            {
                if (Interlocked.CompareExchange(ref completionClaimed, 1, 0) != 0)
                {
                    return;
                }

                cancellationRegistration.Dispose();
                tcs.SetResult(true);
            }

            void CompleteWithException(Exception exception)
            {
                if (Interlocked.CompareExchange(ref completionClaimed, 1, 0) != 0)
                {
                    return;
                }

                cancellationRegistration.Dispose();
                tcs.SetException(exception);
            }

            void CompleteWithCancellation()
            {
                if (Interlocked.CompareExchange(ref completionClaimed, 1, 0) != 0)
                {
                    return;
                }

                cancellationRegistration.Dispose();
                tcs.SetCanceled();
            }

            try
            {
                cancellationRegistration = cancellationToken.Register(CompleteWithCancellation);
            }
            catch (Exception ex)
            {
                CompleteWithException(ex);
                return tcs.Task;
            }

            if (Volatile.Read(ref completionClaimed) != 0)
            {
                cancellationRegistration.Dispose();
            }

            try
            {
                Disconnect(CompleteSuccessfully);
            }
            catch (Exception ex)
            {
                CompleteWithException(ex);
            }

            return tcs.Task;
        }

        internal void Log(LogLevel level, string source, string message)
        {
            Opts.Logger!.Log(level, source, message);
        }

        internal bool HasLogger()
        {
            return Opts.Logger != null;
        }

        // PhoenixJS: we use C# delegates instead of callbacks
        //
        // public Subscription OnOpen(Action callback)
        // public Subscription OnClose(Action callback)
        // public Subscription OnError(Action callback)
        // public Subscription OnMessage(Action callback)

        private void OnConnOpen(IWebsocket websocket)
        {
            if (HasLogger())
            {
                Log(LogLevel.Debug, "transport", $"Connected to {EndPointUrl()}");
            }

            Volatile.Write(ref _closeWasClean, false);
            // establishedConnections++;
            FlushSendBuffer();
            _reconnectTimer?.Reset();
            ResetHeartbeat();

            try
            {
                OnOpen?.Invoke();
            }
            catch (Exception ex)
            {
                if (HasLogger())
                {
                    Log(LogLevel.Error, "socket", $"OnOpen callback threw exception: {ex.Message}");
                }
            }
        }

        private void HeartbeatTimeout(long generation)
        {
            lock (_heartbeatStateLock)
            {
                if (generation != _heartbeatGeneration || _pendingHeartbeatRef == null)
                {
                    return;
                }

                _heartbeatGeneration++;
                _pendingHeartbeatRef = null;
                _heartbeatTimer = null;
            }

            if (HasLogger())
            {
                Log(LogLevel.Debug, "transport", "heartbeat timeout. Attempting to re-establish connection");
            }

            // Match PhoenixJS: trigger channel errors, then teardown with a callback
            // that explicitly schedules reconnection. This bypasses OnConnClose's
            // code != 1000 check, which would otherwise block reconnection.
            TriggerChanError();
            Volatile.Write(ref _closeWasClean, false);
            Teardown(() => _reconnectTimer?.ScheduleTimeout(), 1_000, "heartbeat timeout");
        }

        private void ResetHeartbeat()
        {
            // we don't check skipHeartbeat on conn since we always use websocket transport
            // however, we do check if heartbeatInterval is set
            var heartbeatInterval = Opts.HeartbeatInterval;
            if (!heartbeatInterval.HasValue)
            {
                return;
            }

            IDelayedExecution? previousExecution;
            long generation;
            lock (_heartbeatStateLock)
            {
                generation = ++_heartbeatGeneration;
                _pendingHeartbeatRef = null;
                previousExecution = _heartbeatTimer;
                _heartbeatTimer = null;
            }

            previousExecution?.Cancel();
            ScheduleHeartbeatSend(generation, heartbeatInterval.Value);
        }

        private void AcknowledgeHeartbeat(string? messageRef)
        {
            var heartbeatInterval = Opts.HeartbeatInterval;
            if (messageRef == null || !heartbeatInterval.HasValue)
            {
                return;
            }

            IDelayedExecution? timeoutExecution;
            long generation;
            lock (_heartbeatStateLock)
            {
                if (messageRef != _pendingHeartbeatRef)
                {
                    return;
                }

                generation = ++_heartbeatGeneration;
                _pendingHeartbeatRef = null;
                timeoutExecution = _heartbeatTimer;
                _heartbeatTimer = null;
            }

            timeoutExecution?.Cancel();
            ScheduleHeartbeatSend(generation, heartbeatInterval.Value);
        }

        private void ScheduleHeartbeatSend(long generation, TimeSpan delay)
        {
            ScheduleHeartbeatTimer(
                generation,
                () => SendHeartbeat(generation),
                delay
            );
        }

        private void ScheduleHeartbeatTimeout(long generation, TimeSpan delay)
        {
            ScheduleHeartbeatTimer(
                generation,
                () => HeartbeatTimeout(generation),
                delay
            );
        }

        private void ScheduleHeartbeatTimer(long generation, Action action, TimeSpan delay)
        {
            var execution = Opts.DelayedExecutor.Execute(action, delay);
            bool keepExecution;
            lock (_heartbeatStateLock)
            {
                keepExecution = generation == _heartbeatGeneration;
                if (keepExecution)
                {
                    _heartbeatTimer = execution;
                }
            }

            if (!keepExecution)
            {
                execution.Cancel();
            }
        }

        private void StopHeartbeat()
        {
            IDelayedExecution? execution;
            lock (_heartbeatStateLock)
            {
                _heartbeatGeneration++;
                _pendingHeartbeatRef = null;
                execution = _heartbeatTimer;
                _heartbeatTimer = null;
            }

            execution?.Cancel();
        }

        private void Teardown(Action? callback = null, ushort? code = null, string? reason = null)
        {
            var connection = Conn;
            if (connection == null)
            {
                callback?.Invoke();
                return;
            }

            if (connection.State == WebsocketState.Closed)
            {
                TryClearConnection(connection);
                callback?.Invoke();
                return;
            }

            // See: comment on the method itself
            // WaitForBufferDone(() => {

            // if (conn != null) {
            if (code.HasValue)
            {
                connection.Close(code.Value, reason);
            }
            else
            {
                connection.Close();
            }
            // }

            WaitForSocketClosed(connection, () =>
            {
                // TODO: not sure if this is important at all?
                // this.conn.onclose = function (){ } // noop
                TryClearConnection(connection);

                callback?.Invoke();
            });

            // });
        }

        // PhoenixJS: not sure how to check for bufferedAmount in C#
        //
        // private void WaitForBufferDone(Action callback, uint tries = 1) {
        //     if (tries == 5 || conn == null || conn.bufferedAmount == 0) {
        //         callback();
        //         return;
        //     }

        //     opts.delayedExecutor.Execute(
        //         () => WaitForBufferDone(callback, tries + 1),
        //         TimeSpan.FromMilliseconds(150 * tries)
        //     );
        // }

        private void WaitForSocketClosed(
            IWebsocket connection,
            Action callback,
            uint tries = 1
        )
        {
            if (tries == 5 || connection.State == WebsocketState.Closed)
            {
                callback();
                return;
            }

            Opts.DelayedExecutor.Execute(
                () => WaitForSocketClosed(connection, callback, tries + 1),
                TimeSpan.FromMilliseconds(150 * tries)
            );
        }

        private bool ShouldReconnectAfterClose(ushort code)
        {
            return !Volatile.Read(ref _closeWasClean)
                && code != 1_000
                && _reconnectTimer != null;
        }

        private List<PendingConnectWaiter> ClaimPendingConnectWaitersLocked()
        {
            var claimedWaiters = new List<PendingConnectWaiter>();
            foreach (var pendingConnectWaiter in _pendingConnectWaiters)
            {
                if (pendingConnectWaiter.TryClaim())
                {
                    claimedWaiters.Add(pendingConnectWaiter);
                }
            }

            _pendingConnectWaiters.Clear();
            return claimedWaiters;
        }

        private void CloseUnclaimedConnection(IWebsocket connection)
        {
            try
            {
                connection.Close(1_000, "Connection attempt superseded");
            }
            catch (Exception ex)
            {
                if (HasLogger())
                {
                    Log(
                        LogLevel.Error,
                        "transport",
                        $"Superseded WebSocket close failed: {ex.Message}"
                    );
                }
            }
        }

        private bool TryClearConnection(IWebsocket connection)
        {
            return ReferenceEquals(
                Interlocked.CompareExchange(ref _conn, null, connection),
                connection
            );
        }

        private void OnConnClose(IWebsocket websocket, ushort code, string reason)
        {
            StopHeartbeat();

            if (HasLogger())
            {
                Log(LogLevel.Debug, "transport", $"Close {code} {reason}");
            }

            TriggerChanError();

            if (ShouldReconnectAfterClose(code))
            {
                _reconnectTimer?.ScheduleTimeout();
            }

            try
            {
                OnClose?.Invoke(code, reason);
            }
            catch (Exception ex)
            {
                if (HasLogger())
                {
                    Log(LogLevel.Error, "socket", $"OnClose callback threw exception: {ex.Message}");
                }
            }
        }

        private void OnConnError(IWebsocket websocket, string error)
        {
            if (HasLogger())
            {
                Log(LogLevel.Debug, "transport", $"Error {error}");
            }

            try
            {
                OnError?.Invoke(error);
            }
            catch (Exception ex)
            {
                if (HasLogger())
                {
                    Log(LogLevel.Error, "socket", $"OnError callback threw exception: {ex.Message}");
                }
            }

            TriggerChanError();
        }

        private void TriggerChanError()
        {
            List<Channel> channelsCopy;
            lock (_channelsLock)
            {
                channelsCopy = new List<Channel>(_channels);
            }

            channelsCopy.ForEach(channel =>
            {
                if (!(channel.IsErrored() || channel.IsLeaving() || channel.IsClosed()))
                {
                    channel.Trigger(Message.InBoundEvent.Error);
                }
            });
        }

        internal bool IsConnected()
        {
            return State == WebsocketState.Open;
        }

        internal void Remove(Channel channel)
        {
            // PhoenixJS: see the note above regarding stateChangeCallbacks
            // this.off(channel.stateChangeRefs)
            lock (_channelsLock)
            {
                _channels.Remove(channel);
            }
        }

        // private void Off(List<string> refs)

        public Channel Channel(string topic, Dictionary<string, object>? chanParams = null)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(Socket), "Cannot create channel on disposed socket");
            }
            if (topic == null)
                throw new ArgumentNullException(nameof(topic));
            if (string.IsNullOrWhiteSpace(topic))
                throw new ArgumentException("Topic cannot be empty or whitespace.", nameof(topic));

            var chan = new Channel(topic, chanParams, this);
            bool disposed;
            lock (_channelsLock)
            {
                disposed = _disposed;
                if (!disposed)
                {
                    _channels.Add(chan);
                }
            }

            if (disposed)
            {
                ((IChannelCleanup)chan).Cleanup();
                throw new ObjectDisposedException(nameof(Socket), "Cannot create channel on disposed socket");
            }

            return chan;
        }

        internal void Push(Message message)
        {
            if (HasLogger()) // let {topic, event, payload, ref, join_ref} = data
            {
                Log(LogLevel.Debug, "push", $"Pushing {message}");
            }

            bool EncodeThenSend()
            {
                var conn = Conn;
                if (conn == null)
                {
                    return false;
                }

                conn.Send(Opts.MessageSerializer.Serialize(message));
                return true;
            }

            if (IsConnected())
            {
                if (!EncodeThenSend())
                {
                    BufferSend(EncodeThenSend);
                }
            }
            else
            {
                BufferSend(EncodeThenSend);
            }
        }

        private void BufferSend(Func<bool> callback)
        {
            bool flushAfterBuffering;
            lock (_sendBufferLock)
            {
                SendBuffer.Add(callback);
                if (_isFlushingSendBuffer)
                {
                    _sendBufferFlushRequested = true;
                    return;
                }

                flushAfterBuffering = IsConnected();
            }

            if (flushAfterBuffering)
            {
                FlushSendBuffer();
            }
        }

        internal string MakeRef()
        {
            // Must remain lock-free: Push calls this while holding Push._stateLock.
            // A long counter makes wraparound impractical; Interlocked keeps concurrent refs unique.
            return Interlocked.Increment(ref _ref).ToString();
        }

        private void SendHeartbeat(long generation)
        {
            long sendGeneration;
            lock (_heartbeatStateLock)
            {
                if (generation != _heartbeatGeneration)
                {
                    return;
                }

                _heartbeatTimer = null;
                sendGeneration = ++_heartbeatGeneration;
            }

            var heartbeatInterval = Opts.HeartbeatInterval;
            if (_disposed || !heartbeatInterval.HasValue || !IsConnected())
            {
                return;
            }

            var heartbeatRef = MakeRef();
            lock (_heartbeatStateLock)
            {
                if (sendGeneration != _heartbeatGeneration)
                {
                    return;
                }

                _pendingHeartbeatRef = heartbeatRef;
            }

            if (!IsConnected())
            {
                lock (_heartbeatStateLock)
                {
                    if (sendGeneration == _heartbeatGeneration
                        && _pendingHeartbeatRef == heartbeatRef)
                    {
                        _heartbeatGeneration++;
                        _pendingHeartbeatRef = null;
                    }
                }

                return;
            }

            Push(new Message(
                "phoenix",
                "heartbeat",
                @ref: heartbeatRef
            ));

            long timeoutGeneration;
            lock (_heartbeatStateLock)
            {
                if (sendGeneration != _heartbeatGeneration
                    || _pendingHeartbeatRef != heartbeatRef)
                {
                    return;
                }

                timeoutGeneration = ++_heartbeatGeneration;
            }

            ScheduleHeartbeatTimeout(timeoutGeneration, heartbeatInterval.Value);
        }

        internal void AbnormalClose(string reason)
        {
            Volatile.Write(ref _closeWasClean, false);
            if (IsConnected())
            {
                Conn!.Close(1_000, reason);
            }
        }

        internal void FlushSendBuffer()
        {
            List<Func<bool>> bufferCopy;
            lock (_sendBufferLock)
            {
                if (_isFlushingSendBuffer)
                {
                    _sendBufferFlushRequested = true;
                    return;
                }

                if (!IsConnected() || SendBuffer.Count <= 0)
                {
                    return;
                }

                _isFlushingSendBuffer = true;
                bufferCopy = new List<Func<bool>>(SendBuffer);
                SendBuffer.Clear();
            }

            bool flushAgain;
            try
            {
                for (var index = 0; index < bufferCopy.Count; index++)
                {
                    if (bufferCopy[index]())
                    {
                        continue;
                    }

                    lock (_sendBufferLock)
                    {
                        SendBuffer.InsertRange(
                            0,
                            bufferCopy.GetRange(index, bufferCopy.Count - index)
                        );
                    }
                    break;
                }
            }
            finally
            {
                lock (_sendBufferLock)
                {
                    _isFlushingSendBuffer = false;
                    flushAgain = _sendBufferFlushRequested;
                    _sendBufferFlushRequested = false;
                }
            }

            if (flushAgain)
            {
                FlushSendBuffer();
            }
        }

        private void OnConnMessage(IWebsocket websocket, string rawMessage)
        {
            Message? message;
            try
            {
                message = Opts.MessageSerializer.Deserialize<Message>(rawMessage);
            }
            catch (Exception ex)
            {
                if (HasLogger())
                {
                    Log(LogLevel.Error, "socket", $"Failed to deserialize message: {ex.Message}");
                }

                return;
            }

            if (message == null)
            {
                if (HasLogger())
                {
                    Log(LogLevel.Error, "socket", "Deserialized message was null");
                }

                return;
            }

            AcknowledgeHeartbeat(message.Ref);

            if (HasLogger())
            {
                Log(LogLevel.Debug, "receive", $"Received {message}");
            }

            // copy channels before triggering callbacks, since they might modify the channels list
            List<Channel> channelsCopy;
            lock (_channelsLock)
            {
                channelsCopy = new List<Channel>(_channels);
            }

            channelsCopy.ForEach(channel =>
            {
                // violates tell don't ask, but that's how Phoenix JS is implemented
                if (channel.IsMember(message))
                {
                    channel.Trigger(message);
                }
            });

            try
            {
                OnMessage?.Invoke(message);
            }
            catch (Exception ex)
            {
                if (HasLogger())
                {
                    Log(LogLevel.Error, "socket", $"OnMessage callback threw exception: {ex.Message}");
                }
            }
        }

        internal void LeaveOpenTopic(string topic)
        {
            Channel? dupChannel;
            lock (_channelsLock)
            {
                dupChannel = _channels.Find(channel =>
                    channel.Topic == topic && (channel.IsJoined() || channel.IsJoining()));
            }

            if (dupChannel == null)
            {
                return;
            }

            if (HasLogger())
            {
                Log(LogLevel.Debug, "transport", $"Leaving duplicate channel topic {topic}");
            }

            dupChannel.Leave();
        }

        /// <summary>
        /// Disposes the socket and all associated resources.
        /// Cleans up all channels, cancels timers, and closes the connection.
        /// </summary>
        public void Dispose()
        {
            List<PendingConnectWaiter> connectWaiters;
            lock (_pendingConnectWaitersLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                connectWaiters = ClaimPendingConnectWaitersLocked();
            }

            foreach (var connectWaiter in connectWaiters)
            {
                connectWaiter.FinishWithException(
                    new ObjectDisposedException(nameof(Socket), "Cannot connect disposed socket")
                );
            }

            // Cancel all timers
            StopHeartbeat();
            _reconnectTimer?.Reset();

            List<Channel> channelsCopy;
            lock (_channelsLock)
            {
                channelsCopy = _channels.ToList();
                _channels.Clear();
            }

            // Cleanup calls timer/executor code, so run it outside the socket lock.
            foreach (var channel in channelsCopy)
            {
                ((IChannelCleanup)channel).Cleanup();
            }

            lock (_sendBufferLock)
            {
                SendBuffer.Clear();
            }

            // Clear delegates to prevent any lingering references
            OnOpen = null;
            OnClose = null;
            OnError = null;
            OnMessage = null;

            // Close the connection if open
            var connection = Interlocked.Exchange(ref _conn, null);
            if (connection != null && connection.State != WebsocketState.Closed)
            {
                try
                {
                    connection.Close(1000, "Socket disposed");
                }
                catch
                {
                    // Ignore errors during disposal
                }
            }
        }


        public sealed class Options
        {
            private static readonly uint[] ConnectIntervals =
            {
                10, 50, 100, 150, 200, 250, 500, 1_000, 2_000
            };

            private static readonly uint[] JoinIntervals =
            {
                1_000, 2_000, 5_000
            };

            // Message serializer to allow different serialization methods
            public readonly IMessageSerializer MessageSerializer;

            // The object responsible for performing delayed executions
            public IDelayedExecutor DelayedExecutor = new TaskDelayedExecutor();

            // The interval to send a heartbeat message. Null means disable
            public TimeSpan? HeartbeatInterval = TimeSpan.FromSeconds(30);

            // The optional function for specialized logging
            public ILogger? Logger = null;

            // The interval for reconnecting in the event of a connection error. Null means none.
            public Func<int, TimeSpan>? ReconnectAfter = tries =>
                tries > ConnectIntervals.Length
                    ? TimeSpan.FromSeconds(5)
                    : TimeSpan.FromMilliseconds(ConnectIntervals[tries - 1]);

            // The interval for rejoining an errored channel. Null means none.
            public Func<int, TimeSpan>? RejoinAfter = tries =>
                tries > JoinIntervals.Length
                    ? TimeSpan.FromSeconds(10)
                    : TimeSpan.FromMilliseconds(JoinIntervals[tries - 1]);

            // The default timeout to trigger push timeouts.
            public TimeSpan Timeout = TimeSpan.FromSeconds(10);

            // The serializer's protocol version to send on connect.
            public string Vsn = "2.0.0";

            // required parameters
            public Options(IMessageSerializer messageSerializer)
            {
                MessageSerializer = messageSerializer ?? throw new ArgumentNullException(nameof(messageSerializer));
            }
        }
    }
}
