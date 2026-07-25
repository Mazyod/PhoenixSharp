#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SubscriptionTable = System.Collections.Generic.Dictionary<
    string, Phoenix.ChannelSubscription[]>;

namespace Phoenix
{
    /// <summary>
    /// Internal interface for cleanup that doesn't require IDisposable in public API.
    /// </summary>
    internal interface IChannelCleanup
    {
        void Cleanup();
    }
    /**
     * Subscription
     * Represents a subscription to a channel event.
     * We use a class since subscriptions are stored in an array.
     */
    public sealed class ChannelSubscription
    {
        public Action<Message> Callback = null!;
        public string Event = null!;
    }

    public enum ChannelState
    {
        Closed,
        Joining,
        Joined,
        Leaving,
        Errored // errored channels are rejoined automatically
    }

    public class Channel : IChannelCleanup
    {
        private readonly SubscriptionTable _bindings = new SubscriptionTable();
        private readonly Push _joinPush;
        private readonly List<Push> _pushBuffer = new List<Push>();
        private readonly object _pushBufferLock = new object();

        /**
         * See the stateChangeRefs comment in Socket.cs
         */
        // internal List<object> stateChangeRefs = new();
        private readonly Scheduler? _rejoinTimer;

        /// <summary>
        /// Lock for state transitions and bindings access.
        /// The leave epoch changes only when Leave or Cleanup invalidates user callbacks.
        /// </summary>
        private readonly object _stateLock = new object();
        private long _leaveEpoch;
        private ChannelState _state = ChannelState.Closed;

        public readonly Socket Socket;
        public readonly string Topic;
        private bool _joinedOnce;
        private TimeSpan _timeout;
        private bool _disposed;


        public ChannelState State
        {
            get
            {
                lock (_stateLock)
                {
                    return _state;
                }
            }
        }

        /// <summary>
        /// Sets the channel state.
        /// </summary>
        private void SetState(ChannelState newState)
        {
            lock (_stateLock)
            {
                _state = newState;
            }
        }

        private bool TrySetStateFromJoinReply(ChannelState newState, out long leaveEpoch)
        {
            lock (_stateLock)
            {
                leaveEpoch = _leaveEpoch;
                if (_state == ChannelState.Leaving || _state == ChannelState.Closed)
                {
                    return false;
                }

                _state = newState;
                return true;
            }
        }

        private bool HasLeaveEpochChanged(long expectedLeaveEpoch)
        {
            lock (_stateLock)
            {
                return _leaveEpoch != expectedLeaveEpoch;
            }
        }

        // TODO: possibly support lazy instantiation of payload (same as Phoenix js)
        public Channel(string topic, Dictionary<string, object>? @params, Socket socket)
        {
            if (topic == null)
                throw new ArgumentNullException(nameof(topic));
            if (string.IsNullOrWhiteSpace(topic))
                throw new ArgumentException("Topic cannot be empty or whitespace.", nameof(topic));
            if (socket == null)
                throw new ArgumentNullException(nameof(socket));

            Topic = topic;
            Socket = socket;

            _timeout = socket.Opts.Timeout;
            _joinPush = new Push(
                this,
                Message.OutBoundEvent.Join.Serialized(),
                () => socket.Opts.MessageSerializer.Box(@params),
                _timeout
            );

            if (socket.Opts.RejoinAfter != null)
            {
                _rejoinTimer = new Scheduler(
                    () =>
                    {
                        if (socket.IsConnected())
                        {
                            Rejoin();
                        }
                    },
                    socket.Opts.RejoinAfter,
                    socket.Opts.DelayedExecutor
                );
            }

            socket.OnError += SocketOnError;
            socket.OnOpen += SocketOnOpen;

            _joinPush.Receive(ReplyStatus.Ok, _ =>
            {
                if (!TrySetStateFromJoinReply(ChannelState.Joined, out var leaveEpoch))
                {
                    return;
                }

                _rejoinTimer?.Reset();
                List<Push> bufferCopy;
                lock (_pushBufferLock)
                {
                    bufferCopy = new List<Push>(_pushBuffer);
                    _pushBuffer.Clear();
                }

                foreach (var push in bufferCopy)
                {
                    if (HasLeaveEpochChanged(leaveEpoch))
                    {
                        return;
                    }

                    push.Send();
                }
            });

            _joinPush.Receive(ReplyStatus.Error, _reply =>
            {
                if (!TrySetStateFromJoinReply(ChannelState.Errored, out _))
                {
                    return;
                }

                if (socket.IsConnected())
                {
                    _rejoinTimer?.ScheduleTimeout();
                }
            });

            OnClose(_ =>
            {
                _rejoinTimer?.Reset();
                var logger = socket.GetEnabledLogger(
                    LogLevel.Debug,
                    LogSource.Channel
                );
                if (logger != null)
                {
                    logger.Log(
                        LogLevel.Debug,
                        LogSource.Channel,
                        $"close {topic}",
                        null
                    );
                }

                SetState(ChannelState.Closed);
                // PhoenixJS: See note in socket regarding this
                // basically, we unregister delegates directly in c# instead of offing an array
                // this.off(channel.stateChangeRefs)
                socket.OnError -= SocketOnError;
                socket.OnOpen -= SocketOnOpen;
                socket.Remove(this);
            });

            OnError(_ =>
            {
                var logger = socket.GetEnabledLogger(
                    LogLevel.Debug,
                    LogSource.Channel
                );
                if (logger != null)
                {
                    logger.Log(
                        LogLevel.Debug,
                        LogSource.Channel,
                        $"error {topic}",
                        null
                    );
                }

                if (IsJoining())
                {
                    _joinPush.Reset();
                }

                SetState(ChannelState.Errored);
                if (socket.IsConnected())
                {
                    _rejoinTimer?.ScheduleTimeout();
                }
            });

            _joinPush.Receive(ReplyStatus.Timeout, _reply =>
            {
                if (!TrySetStateFromJoinReply(ChannelState.Errored, out _))
                {
                    return;
                }

                var logger = socket.GetEnabledLogger(
                    LogLevel.Debug,
                    LogSource.Channel
                );
                if (logger != null)
                {
                    logger.Log(
                        LogLevel.Debug,
                        LogSource.Channel,
                        $"timeout {topic} ({JoinRef})",
                        null
                    );
                }

                var leaveEvent = Message.OutBoundEvent.Leave.Serialized();
                var leavePush = new Push(this, leaveEvent, null, _timeout);
                leavePush.Send();

                _joinPush.Reset();

                if (socket.IsConnected())
                {
                    _rejoinTimer?.ScheduleTimeout();
                }
            });

            // on phx_reply, also trigger a message for the push using replyEventName
            On(Message.InBoundEvent.Reply.Serialized(), message =>
            {
                var replyMessage = message with { Event = ReplyEventName(message.Ref) };
                Trigger(replyMessage);
            });
        }

        internal string? JoinRef => _joinPush.Ref;

        internal void MakeMessageRefs(
            Push sender,
            out string messageRef,
            out string? joinRef
        )
        {
            _joinPush.MakeMessageRefs(sender, out messageRef, out joinRef);
        }


        public Push Join(TimeSpan? timeout = null)
        {
            if (_joinedOnce)
            {
                throw new InvalidOperationException(
                    "tried to join multiple times. 'join' can only be called a single time per channel instance");
            }

            _timeout = timeout ?? _timeout;
            _joinedOnce = true;
            Rejoin();
            return _joinPush;
        }

        public ChannelSubscription OnClose(Action<Message> callback)
        {
            return On(Message.InBoundEvent.Close, callback);
        }

        public ChannelSubscription OnError(Action<Message> callback)
        {
            return On(Message.InBoundEvent.Error, callback);
        }

        public ChannelSubscription On(Message.InBoundEvent @event, Action<Message> callback)
        {
            return On(@event.Serialized(), callback);
        }

        public ChannelSubscription On(string anyEvent, Action<Message> callback)
        {
            if (anyEvent == null)
                throw new ArgumentNullException(nameof(anyEvent));
            if (string.IsNullOrWhiteSpace(anyEvent))
                throw new ArgumentException("Event name cannot be empty or whitespace.", nameof(anyEvent));
            if (callback == null)
                throw new ArgumentNullException(nameof(callback));

            var subscription = new ChannelSubscription
            {
                Event = anyEvent,
                Callback = callback
            };

            lock (_stateLock)
            {
                if (!_bindings.TryGetValue(anyEvent, out var subscriptions))
                {
                    _bindings[anyEvent] = new[] { subscription };
                    return subscription;
                }

                var updatedSubscriptions =
                    new ChannelSubscription[subscriptions.Length + 1];
                Array.Copy(
                    subscriptions,
                    updatedSubscriptions,
                    subscriptions.Length
                );
                updatedSubscriptions[subscriptions.Length] = subscription;
                _bindings[anyEvent] = updatedSubscriptions;
            }

            return subscription;
        }

        /// <summary>
        /// Subscribes a callback that receives the event payload unboxed as
        /// <typeparamref name="T"/>.
        /// </summary>
        /// <remarks>
        /// A null payload or an unboxing failure skips this typed callback,
        /// logs a warning, and is surfaced through
        /// <see cref="Socket.OnUnhandledError"/> as a dispatch error.
        /// Exceptions thrown by <paramref name="callback"/> continue through
        /// the channel's per-subscriber dispatch containment.
        /// </remarks>
        public ChannelSubscription On<T>(string anyEvent, Action<T> callback)
        {
            return On(
                anyEvent,
                message =>
                {
                    if (message.Payload == null)
                    {
                        ReportTypedPayloadIssue(
                            $"Typed event callback for '{anyEvent}' skipped "
                            + "because its payload is null",
                            null
                        );
                        return;
                    }

                    T payload;
                    try
                    {
                        payload = message.Payload.Unbox<T>()!;
                    }
                    catch (Exception ex)
                    {
                        var targetType = typeof(T);
                        var targetTypeName =
                            targetType.FullName ?? targetType.Name;
                        ReportTypedPayloadIssue(
                            $"Typed event callback for '{anyEvent}' could not "
                            + $"unbox its payload as '{targetTypeName}'",
                            ex
                        );
                        return;
                    }

                    if (payload is null)
                    {
                        ReportTypedPayloadIssue(
                            $"Typed event callback for '{anyEvent}' skipped "
                            + "because its payload is null",
                            null
                        );
                        return;
                    }

                    callback(payload);
                }
            );
        }

        private void ReportTypedPayloadIssue(
            string message,
            Exception? exception
        )
        {
            Socket.ReportUnhandledError(
                new PhoenixError(
                    message,
                    PhoenixErrorKind.Dispatch,
                    exception
                )
            );
            var logger = Socket.GetEnabledLogger(
                LogLevel.Warn,
                LogSource.Channel
            );
            if (logger != null)
            {
                logger.Log(
                    LogLevel.Warn,
                    LogSource.Channel,
                    message,
                    exception
                );
            }
        }

        public bool Off(ChannelSubscription subscription)
        {
            if (subscription == null)
                throw new ArgumentNullException(nameof(subscription));

            lock (_stateLock)
            {
                if (!_bindings.TryGetValue(
                    subscription.Event,
                    out var subscriptions
                ))
                {
                    return false;
                }

                var index = Array.IndexOf(subscriptions, subscription);
                if (index < 0)
                {
                    return false;
                }

                if (subscriptions.Length == 1)
                {
                    _bindings[subscription.Event] =
                        Array.Empty<ChannelSubscription>();
                    return true;
                }

                var updatedSubscriptions =
                    new ChannelSubscription[subscriptions.Length - 1];
                if (index > 0)
                {
                    Array.Copy(
                        subscriptions,
                        0,
                        updatedSubscriptions,
                        0,
                        index
                    );
                }

                if (index < subscriptions.Length - 1)
                {
                    Array.Copy(
                        subscriptions,
                        index + 1,
                        updatedSubscriptions,
                        index,
                        subscriptions.Length - index - 1
                    );
                }

                _bindings[subscription.Event] = updatedSubscriptions;
                return true;
            }
        }

        public bool Off(Message.InBoundEvent @event)
        {
            return Off(@event.Serialized());
        }

        public bool Off(Message.OutBoundEvent @event)
        {
            return Off(@event.Serialized());
        }

        public bool Off(string anyEvent)
        {
            if (anyEvent == null)
                throw new ArgumentNullException(nameof(anyEvent));
            if (string.IsNullOrWhiteSpace(anyEvent))
                throw new ArgumentException("Event name cannot be empty or whitespace.", nameof(anyEvent));

            lock (_stateLock)
            {
                return _bindings.Remove(anyEvent);
            }
        }

        /// <summary>
        /// Clears all user-defined event bindings, keeping only internal events
        /// (phx_* and chan_reply_*) that are needed for channel lifecycle management.
        /// Called when leaving a channel to prevent callbacks from firing after Leave().
        /// MUST be called while holding _stateLock.
        /// </summary>
        private void ClearUserBindingsUnsafe()
        {
            var keysToRemove = new List<string>();
            foreach (var key in _bindings.Keys)
            {
                if (!key.StartsWith("phx_") && !key.StartsWith(Reply.ReplyEventPrefix))
                {
                    keysToRemove.Add(key);
                }
            }

            foreach (var key in keysToRemove)
            {
                _bindings.Remove(key);
            }
        }

        public bool CanPush()
        {
            return Socket.IsConnected() && IsJoined();
        }

        public Push Push(string @event, object? payload = null, TimeSpan? timeout = null)
        {
            if (@event == null)
                throw new ArgumentNullException(nameof(@event));
            if (string.IsNullOrWhiteSpace(@event))
                throw new ArgumentException("Event name cannot be empty or whitespace.", nameof(@event));

            if (!_joinedOnce)
            {
                throw new InvalidOperationException(
                    $"tried to push '{@event}' to '{Topic}' before joining."
                    + " Use channel.join() before pushing events"
                );
            }

            var serializer = Socket.Opts.MessageSerializer;
            var pushEvent = new Push(
                this,
                @event,
                () => serializer.Box(payload),
                timeout ?? _timeout
            );

            if (CanPush())
            {
                pushEvent.Send();
            }
            else
            {
                pushEvent.StartTimeout();
                lock (_pushBufferLock)
                {
                    _pushBuffer.Add(pushEvent);
                }
            }

            return pushEvent;
        }

        public Push Leave(TimeSpan? timeout = null)
        {
            _rejoinTimer?.Reset();
            _joinPush.CancelTimeout();

            // Set state to Leaving and clear user bindings atomically
            // This ensures no race condition where callbacks can fire after Leave()
            lock (_stateLock)
            {
                _state = ChannelState.Leaving;
                _leaveEpoch++;
                ClearUserBindingsUnsafe();
            }

            void TriggerClose()
            {
                var logger = Socket.GetEnabledLogger(
                    LogLevel.Debug,
                    LogSource.Channel
                );
                if (logger != null)
                {
                    logger.Log(
                        LogLevel.Debug,
                        LogSource.Channel,
                        $"leave {Topic}",
                        null
                    );
                }

                Trigger(Message.InBoundEvent.Close);
            }

            var leaveEvent = Message.OutBoundEvent.Leave.Serialized();
            var leavePush = new Push(this, leaveEvent, null, timeout ?? _timeout);
            leavePush
                .Receive(ReplyStatus.Ok, _ => TriggerClose())
                .Receive(ReplyStatus.Error, _ => TriggerClose())
                .Receive(ReplyStatus.Timeout, _ => TriggerClose());
            leavePush.Send();

            if (!CanPush())
            {
                leavePush.Trigger(ReplyStatus.Ok);
            }

            return leavePush;
        }

        /// <summary>
        /// Joins the channel asynchronously.
        /// </summary>
        public Task<JoinResult> JoinAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            var tcs = new TaskCompletionSource<JoinResult>(TaskCreationOptions.RunContinuationsAsynchronously);

            var push = Join(timeout);

            push.Receive(ReplyStatus.Ok, reply => tcs.TrySetResult(JoinResult.Success(reply)));
            push.Receive(ReplyStatus.Error, reply => tcs.TrySetResult(JoinResult.Failure(reply)));
            push.Receive(ReplyStatus.Timeout, _ => tcs.TrySetResult(JoinResult.Timeout()));

            var cancellationRegistration = cancellationToken.Register(
                () => tcs.TrySetCanceled()
            );

            return TaskUtilities.AwaitAndDisposeCancellationRegistrationAsync(
                tcs.Task,
                cancellationRegistration
            );
        }

        /// <summary>
        /// Pushes a message to the channel asynchronously.
        /// </summary>
        public Task<PushResult> PushAsync(string @event, object? payload = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            var tcs = new TaskCompletionSource<PushResult>(TaskCreationOptions.RunContinuationsAsynchronously);

            var push = Push(@event, payload, timeout);

            push.Receive(ReplyStatus.Ok, reply => tcs.TrySetResult(PushResult.Success(reply)));
            push.Receive(ReplyStatus.Error, reply => tcs.TrySetResult(PushResult.Failure(reply, ReplyStatus.Error)));
            push.Receive(ReplyStatus.Timeout, _ => tcs.TrySetResult(PushResult.Timeout()));

            var cancellationRegistration = cancellationToken.Register(
                () => tcs.TrySetCanceled()
            );

            return TaskUtilities.AwaitAndDisposeCancellationRegistrationAsync(
                tcs.Task,
                cancellationRegistration
            );
        }

        /// <summary>
        /// Pushes a message to the channel asynchronously and deserializes the response.
        /// </summary>
        public Task<PushResult<T>> PushAsync<T>(string @event, object? payload = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            var tcs = new TaskCompletionSource<PushResult<T>>(TaskCreationOptions.RunContinuationsAsynchronously);

            var push = Push(@event, payload, timeout);

            push.Receive(ReplyStatus.Ok, reply =>
            {
                try
                {
                    var response = reply.Response != null ? reply.Response.Unbox<T>() : default!;
                    tcs.TrySetResult(PushResult<T>.Success(response, reply));
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
            push.Receive(ReplyStatus.Error, reply => tcs.TrySetResult(PushResult<T>.Failure(reply, ReplyStatus.Error)));
            push.Receive(ReplyStatus.Timeout, _ => tcs.TrySetResult(PushResult<T>.Timeout()));

            var cancellationRegistration = cancellationToken.Register(
                () => tcs.TrySetCanceled()
            );

            return TaskUtilities.AwaitAndDisposeCancellationRegistrationAsync(
                tcs.Task,
                cancellationRegistration
            );
        }

        /// <summary>
        /// Leaves the channel asynchronously.
        /// </summary>
        public Task LeaveAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var push = Leave(timeout);

            push.Receive(ReplyStatus.Ok, _ => tcs.TrySetResult(true));
            push.Receive(ReplyStatus.Error, _ => tcs.TrySetResult(true));
            push.Receive(ReplyStatus.Timeout, _ => tcs.TrySetResult(true)); // Leave completes even on timeout

            var cancellationRegistration = cancellationToken.Register(
                () => tcs.TrySetCanceled()
            );

            return TaskUtilities.AwaitAndDisposeCancellationRegistrationAsync(
                tcs.Task,
                cancellationRegistration
            );
        }

        /// <summary>
        /// Waits asynchronously for a single occurrence of a specific event on the channel.
        /// </summary>
        /// <param name="eventName">The name of the event to wait for.</param>
        /// <param name="timeout">
        /// Optional timeout for waiting. If null, waits indefinitely until the event occurs or cancellation.
        /// </param>
        /// <param name="cancellationToken">A cancellation token to cancel the wait operation.</param>
        /// <returns>A task that completes with the message when the event is received.</returns>
        /// <exception cref="ArgumentNullException">Thrown when eventName is null.</exception>
        /// <exception cref="ArgumentException">Thrown when eventName is empty or whitespace.</exception>
        /// <exception cref="TimeoutException">Thrown when the timeout expires before the event is received.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the cancellation token is triggered.</exception>
        /// <remarks>
        /// This method subscribes to the event, waits for a single occurrence, then automatically unsubscribes.
        /// The subscription is properly cleaned up on timeout, cancellation, or successful receipt.
        /// </remarks>
        public Task<Message> WaitForEventAsync(
            string eventName,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            if (eventName == null)
                throw new ArgumentNullException(nameof(eventName));
            if (string.IsNullOrWhiteSpace(eventName))
                throw new ArgumentException("Event name cannot be empty or whitespace.", nameof(eventName));

            var tcs = new TaskCompletionSource<Message>(TaskCreationOptions.RunContinuationsAsynchronously);

            CancellationTokenSource? timeoutCts = null;
            CancellationTokenRegistration timeoutRegistration = default;
            CancellationTokenRegistration cancellationRegistration = default;
            try
            {
                if (timeout.HasValue)
                {
                    timeoutCts = new CancellationTokenSource();
                    timeoutRegistration = timeoutCts.Token.Register(() =>
                        tcs.TrySetException(new TimeoutException(
                            $"Timeout waiting for event '{eventName}' after "
                            + $"{timeout.Value.TotalMilliseconds}ms."
                        ))
                    );
                    timeoutCts.CancelAfter(timeout.Value);
                }

                cancellationRegistration = cancellationToken.Register(() =>
                    tcs.TrySetCanceled()
                );
            }
            catch
            {
                timeoutRegistration.Dispose();
                timeoutCts?.Dispose();
                cancellationRegistration.Dispose();
                throw;
            }

            ChannelSubscription? subscription = null;
            try
            {
                if (!tcs.Task.IsCompleted)
                {
                    subscription = On(
                        eventName,
                        message => tcs.TrySetResult(message)
                    );
                }
            }
            catch
            {
                timeoutRegistration.Dispose();
                timeoutCts?.Dispose();
                cancellationRegistration.Dispose();
                throw;
            }

            return AwaitEventAndCleanupAsync(
                tcs.Task,
                subscription,
                timeoutCts,
                timeoutRegistration,
                cancellationRegistration
            );
        }

        private async Task<Message> AwaitEventAndCleanupAsync(
            Task<Message> waitTask,
            ChannelSubscription? subscription,
            CancellationTokenSource? timeoutCts,
            CancellationTokenRegistration timeoutRegistration,
            CancellationTokenRegistration cancellationRegistration
        )
        {
            try
            {
                return await TaskUtilities
                    .AwaitAndDisposeCancellationRegistrationAsync(
                        waitTask,
                        cancellationRegistration
                    )
                    .ConfigureAwait(false);
            }
            finally
            {
                if (subscription != null)
                {
                    Off(subscription);
                }

                timeoutRegistration.Dispose();
                timeoutCts?.Dispose();
            }
        }

        // overrideable message hook
        public virtual IJsonBox? OnMessage(Message message)
        {
            return message.Payload;
        }

        internal bool IsMember(Message message)
        {
            if (Topic != message.Topic)
            {
                return false;
            }

            if (message.JoinRef == null || message.JoinRef == JoinRef)
            {
                return true;
            }

            var logger = Socket.GetEnabledLogger(
                LogLevel.Info,
                LogSource.Channel
            );
            if (logger != null)
            {
                logger.Log(
                    LogLevel.Info,
                    LogSource.Channel,
                    $"dropping outdated message for topic '{Topic}' (joinRef {message.JoinRef} does not match joinRef {JoinRef})",
                    null
                );
            }

            return false;
        }

        private void Rejoin(TimeSpan? timeout = null)
        {
            if (_disposed || IsLeaving())
            {
                return;
            }

            Socket.LeaveOpenTopic(Topic);
            SetState(ChannelState.Joining);
            _joinPush.Resend(timeout ?? _timeout);
        }

        // Helper method not found in PhoenixJS
        internal void Trigger(Message.InBoundEvent @event)
        {
            Trigger(new Message(@event: @event.Serialized()));
        }

        internal void Trigger(Message message)
        {
            ChannelSubscription[]? callbacks = null;
            long leaveEpochWhenWeStarted;
            bool isInternalEvent = message.Event?.StartsWith("phx_") == true
                || message.Event?.StartsWith(Reply.ReplyEventPrefix) == true;
            bool isReplyEvent =
                message.Event == Message.InBoundEvent.Reply.Serialized()
                || message.Event?.StartsWith(Reply.ReplyEventPrefix) == true;

            lock (_stateLock)
            {
                // Reject non-internal messages if channel is leaving (race condition guard)
                // This prevents user callbacks from firing after Leave() is called
                if (_state == ChannelState.Leaving && !isInternalEvent)
                {
                    return;
                }

                leaveEpochWhenWeStarted = _leaveEpoch;

                // Capture the immutable callbacks snapshot for this dispatch.
                if (message.Event != null && _bindings.TryGetValue(message.Event, out var bindings))
                {
                    callbacks = bindings;
                }
            }

            // Process message through OnMessage hook (outside lock to avoid deadlocks)
            var handledPayload = OnMessage(message);
            if (message.Payload != null && handledPayload == null)
            {
                throw new InvalidOperationException(
                    "channel onMessage callbacks must return payload, modified or unmodified"
                );
            }

            // Early exit if no event or no callbacks
            if (message.Event == null || callbacks == null)
            {
                return;
            }

            var callbackJoinRef = message.JoinRef ?? JoinRef;
            Message callbackMessage;
            if (isReplyEvent
                && ReferenceEquals(handledPayload, message.Payload)
                && callbackJoinRef == message.JoinRef)
            {
                // The phx_reply remap supplies the one final clone. Reuse reply
                // messages whose hook and join-ref handling left them unchanged.
                callbackMessage = message;
            }
            else
            {
                callbackMessage = message with
                {
                    Payload = handledPayload,
                    JoinRef = callbackJoinRef
                };
            }

            // Execute callbacks, checking the leave epoch for non-internal events only
            // Internal events (phx_*, chan_reply_*) should always be processed
            foreach (var subscription in callbacks)
            {
                // A general state change must not truncate dispatch. Only Leave/Cleanup
                // invalidates the remaining user callbacks from this snapshot.
                if (!isInternalEvent)
                {
                    lock (_stateLock)
                    {
                        if (_leaveEpoch != leaveEpochWhenWeStarted)
                        {
                            return;
                        }
                    }
                }

                try
                {
                    subscription.Callback(callbackMessage);
                }
                catch (Exception ex)
                {
                    var errorMessage =
                        $"Event callback threw exception for '{callbackMessage.Event}'";
                    Socket.ReportUnhandledError(
                        new PhoenixError(
                            errorMessage,
                            PhoenixErrorKind.Dispatch,
                            ex
                        )
                    );
                    var logger = Socket.GetEnabledLogger(
                        LogLevel.Error,
                        LogSource.Channel
                    );
                    if (logger != null)
                    {
                        logger.Log(
                            LogLevel.Error,
                            LogSource.Channel,
                            errorMessage,
                            ex
                        );
                    }
                }
            }
        }

        internal static string ReplyEventName(string? @ref)
        {
            return $"{Reply.ReplyEventPrefix}{@ref}";
        }

        internal bool IsClosed()
        {
            return State == ChannelState.Closed;
        }

        internal bool IsErrored()
        {
            return State == ChannelState.Errored;
        }

        internal bool IsJoined()
        {
            return State == ChannelState.Joined;
        }

        internal bool IsJoining()
        {
            return State == ChannelState.Joining;
        }

        internal bool IsLeaving()
        {
            return State == ChannelState.Leaving;
        }


        private void SocketOnError(PhoenixError error)
        {
            if (_disposed) return;
            if (error.Kind == PhoenixErrorKind.Transport)
            {
                _rejoinTimer?.Reset();
            }
        }

        private void SocketOnOpen()
        {
            if (_disposed) return;
            _rejoinTimer?.Reset();
            if (IsErrored())
            {
                Rejoin();
            }
        }

        /// <summary>
        /// Internal cleanup method to release all resources and unsubscribe from events.
        /// Called by Socket when it is disposed, or can be called directly if needed.
        /// </summary>
        void IChannelCleanup.Cleanup()
        {
            if (_disposed) return;
            _disposed = true;

            // Cancel all timers
            _rejoinTimer?.Reset();
            _joinPush.CancelTimeout();

            // Unsubscribe from socket events to prevent memory leaks
            Socket.OnError -= SocketOnError;
            Socket.OnOpen -= SocketOnOpen;

            // Clear all bindings and set state under lock
            lock (_stateLock)
            {
                _bindings.Clear();
                _state = ChannelState.Closed;
                _leaveEpoch++;
            }

            lock (_pushBufferLock)
            {
                _pushBuffer.Clear();
            }
        }
    }
}
