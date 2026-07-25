#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StatusHookTable = System.Collections.Generic.Dictionary<
    Phoenix.ReplyStatus, System.Collections.Generic.List<System.Action<Phoenix.Reply>>>;

namespace Phoenix
{
    public sealed class Push
    {
        private readonly Channel _channel;
        private readonly string _event;
        private readonly Func<IJsonBox?>? _payload;
        private readonly object _stateLock = new object();

        private readonly StatusHookTable _recHooks = new StatusHookTable();
        private long _attempt;
        private bool _completed;
        private IDelayedExecution? _delayedExecution;
        private Reply? _receivedResp;
        private string? _ref;
        private string? _refEvent;
        private ChannelSubscription? _refEventSubscription;
        private TimeSpan _timeout;
        private bool _timeoutActive;

        // define a constructor that takes a channel, event, payload, and timeout
        public Push(Channel channel, string @event, Func<IJsonBox?>? payload, TimeSpan timeout)
        {
            if (channel == null)
                throw new ArgumentNullException(nameof(channel));
            if (@event == null)
                throw new ArgumentNullException(nameof(@event));
            if (string.IsNullOrWhiteSpace(@event))
                throw new ArgumentException("Event name cannot be empty or whitespace.", nameof(@event));

            _channel = channel;
            _event = @event;
            _payload = payload;
            _timeout = timeout;
        }

        // internal state
        internal string? Ref
        {
            get
            {
                lock (_stateLock)
                {
                    return _ref;
                }
            }
        }

        internal void MakeMessageRefs(
            Push sender,
            out string messageRef,
            out string? joinRef
        )
        {
            lock (_stateLock)
            {
                // Keep the join generation pinned while allocating the message
                // ref so the pair represents one consistent channel view.
                messageRef = _channel.Socket.MakeRef();
                joinRef = ReferenceEquals(sender, this)
                    ? messageRef
                    : _ref;
            }
        }

        public void Resend(TimeSpan timeout)
        {
            Reset(timeout);
            Send();
        }

        public void Send()
        {
            if (!StartTimeout(
                true,
                out var messageRef,
                out var joinRef
            ))
            {
                return;
            }

            IJsonBox? payload;
            try
            {
                payload = _payload?.Invoke();
            }
            catch (Exception ex)
            {
                _channel.Socket.ReportError(
                    new PhoenixError(
                        "Message payload serialization failed",
                        PhoenixErrorKind.Serialization,
                        ex
                    )
                );
                return;
            }

            // sent = true;
            _channel.Socket.Push(new Message(
                _channel.Topic,
                _event,
                payload,
                messageRef,
                joinRef
            ));
        }

        public Push Receive(ReplyStatus status, Action<Reply> callback)
        {
            if (callback == null)
                throw new ArgumentNullException(nameof(callback));

            Reply? receivedResponse;
            lock (_stateLock)
            {
                AddReceiveHookUnsafe(status, callback);
                receivedResponse = HasReceivedUnsafe(status)
                    ? _receivedResp
                    : null;
            }

            if (receivedResponse.HasValue)
            {
                InvokeReceiveCallback(callback, receivedResponse.Value);
            }

            return this;
        }

        private void InvokeReceiveCallback(
            Action<Reply> callback,
            Reply reply
        )
        {
            foreach (Action<Reply> handler
                in callback.GetInvocationList())
            {
                try
                {
                    handler(reply);
                }
                catch (Exception ex)
                {
                    _channel.Socket.ReportContainedCallbackException(
                        $"Push receive callback threw exception for '{_event}'",
                        LogSource.Push,
                        ex
                    );
                }
            }
        }

        private void AddReceiveHookUnsafe(ReplyStatus status, Action<Reply> callback)
        {
            if (!_recHooks.TryGetValue(status, out var callbacks))
            {
                callbacks = new List<Action<Reply>>();
                _recHooks[status] = callbacks;
            }

            callbacks.Add(callback);
        }

        internal void Reset()
        {
            Reset(null);
        }

        private void Reset(TimeSpan? timeout)
        {
            ChannelSubscription? refEventSubscription;
            IDelayedExecution? delayedExecution;
            lock (_stateLock)
            {
                _attempt++;
                _completed = false;
                _timeoutActive = false;
                refEventSubscription = _refEventSubscription;
                _refEventSubscription = null;
                delayedExecution = _delayedExecution;
                _delayedExecution = null;
                _ref = null;
                _refEvent = null;
                _receivedResp = null;
                if (timeout.HasValue)
                {
                    _timeout = timeout.Value;
                }
            }

            CancelRefEvent(refEventSubscription);
            delayedExecution?.Cancel();
            // sent = false;
        }

        private void MatchReceive(long attempt, Reply? reply)
        {
            if (!reply.HasValue)
            {
                return;
            }

            List<Action<Reply>>? callbacks = null;
            ChannelSubscription? refEventSubscription;
            IDelayedExecution? delayedExecution;
            var receivedResponse = reply.Value;
            lock (_stateLock)
            {
                if (attempt != _attempt
                    || _completed
                    || (receivedResponse.ReplyStatus == ReplyStatus.Timeout && !_timeoutActive))
                {
                    return;
                }

                _completed = true;
                _timeoutActive = false;
                _receivedResp = receivedResponse;
                refEventSubscription = _refEventSubscription;
                _refEventSubscription = null;
                delayedExecution = _delayedExecution;
                _delayedExecution = null;

                if (_recHooks.TryGetValue(receivedResponse.ReplyStatus, out var registeredCallbacks))
                {
                    callbacks = new List<Action<Reply>>(registeredCallbacks);
                }
            }

            CancelRefEvent(refEventSubscription);
            delayedExecution?.Cancel();
            if (callbacks == null)
            {
                return;
            }

            foreach (var callback in callbacks)
            {
                InvokeReceiveCallback(callback, receivedResponse);
            }
        }

        private void CancelRefEvent(ChannelSubscription? subscription)
        {
            if (subscription != null)
            {
                _channel.Off(subscription);
            }
        }

        internal void CancelTimeout()
        {
            IDelayedExecution? delayedExecution;
            lock (_stateLock)
            {
                _timeoutActive = false;
                delayedExecution = _delayedExecution;
                _delayedExecution = null;
            }

            delayedExecution?.Cancel();
        }

        internal void StartTimeout()
        {
            StartTimeout(false, out _, out _);
        }

        private bool StartTimeout(
            bool stopAfterTimeout,
            out string? messageRef,
            out string? joinRef
        )
        {
            ChannelSubscription? previousSubscription;
            IDelayedExecution? previousExecution;
            string refEvent;
            TimeSpan timeout;
            long attempt;
            lock (_stateLock)
            {
                if (stopAfterTimeout && HasReceivedUnsafe(ReplyStatus.Timeout))
                {
                    messageRef = _ref;
                    joinRef = null;
                    return false;
                }

                previousSubscription = _refEventSubscription;
                _refEventSubscription = null;
                previousExecution = _delayedExecution;
                _delayedExecution = null;

                attempt = ++_attempt;
                _channel.MakeMessageRefs(
                    this,
                    out var nextMessageRef,
                    out joinRef
                );
                messageRef = nextMessageRef;
                _ref = messageRef;
                refEvent = Channel.ReplyEventName(messageRef);
                _refEvent = refEvent;
                timeout = _timeout;
                _completed = false;
                _timeoutActive = true;
            }

            CancelRefEvent(previousSubscription);
            previousExecution?.Cancel();

            var subscription = _channel.On(refEvent, message =>
            {
                var reply = message.Payload?.Unbox<Reply?>();
                MatchReceive(attempt, reply);
            });

            bool keepSubscription;
            lock (_stateLock)
            {
                keepSubscription = attempt == _attempt && !_completed;
                if (keepSubscription)
                {
                    _refEventSubscription = subscription;
                }
            }

            if (!keepSubscription)
            {
                CancelRefEvent(subscription);
                return true;
            }

            var delayedExecution = _channel.Socket.Opts.DelayedExecutor.Execute(
                () => Trigger(attempt, refEvent, ReplyStatus.Timeout),
                timeout
            );

            bool keepExecution;
            lock (_stateLock)
            {
                keepExecution = attempt == _attempt && !_completed && _timeoutActive;
                if (keepExecution)
                {
                    _delayedExecution = delayedExecution;
                }
            }

            if (!keepExecution)
            {
                delayedExecution.Cancel();
            }

            return true;
        }

        private bool HasReceivedUnsafe(ReplyStatus status)
        {
            return _receivedResp?.ReplyStatus == status;
        }

        internal void Trigger(ReplyStatus status)
        {
            long attempt;
            string? refEvent;
            lock (_stateLock)
            {
                attempt = _attempt;
                refEvent = _refEvent;
            }

            Trigger(attempt, refEvent, status);
        }

        private void Trigger(long attempt, string? refEvent, ReplyStatus status)
        {
            lock (_stateLock)
            {
                if (attempt != _attempt
                    || _completed
                    || (status == ReplyStatus.Timeout && !_timeoutActive))
                {
                    return;
                }
            }

            var serializer = _channel.Socket.Opts.MessageSerializer;

            _channel.Socket.TriggerChannel(
                _channel,
                new Message(
                    @event: refEvent,
                    payload: serializer.Box(new Dictionary<string, object>
                        {
                            {"status", status.Serialized()}
                        }
                    )
                )
            );
        }

        /// <summary>
        /// Waits asynchronously for any reply (Ok, Error, or Timeout) to this push.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to cancel the wait operation.</param>
        /// <returns>A task that completes with the reply when received.</returns>
        /// <remarks>
        /// This method registers callbacks for all reply statuses and returns once any reply is received.
        /// If the push has already received a reply, it returns immediately with that reply.
        /// </remarks>
        public Task<Reply> ReceiveAsync(CancellationToken cancellationToken = default)
        {
            var tcs = new TaskCompletionSource<Reply>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnReply(Reply reply)
            {
                tcs.TrySetResult(reply);
            }

            Reply? receivedResponse;
            lock (_stateLock)
            {
                receivedResponse = _receivedResp;
                if (!receivedResponse.HasValue)
                {
                    AddReceiveHookUnsafe(ReplyStatus.Ok, OnReply);
                    AddReceiveHookUnsafe(ReplyStatus.Error, OnReply);
                    AddReceiveHookUnsafe(ReplyStatus.Timeout, OnReply);
                }
            }

            if (receivedResponse.HasValue)
            {
                return Task.FromResult(receivedResponse.Value);
            }

            var cancellationRegistration = cancellationToken.Register(
                () => tcs.TrySetCanceled()
            );

            return TaskUtilities.AwaitAndDisposeCancellationRegistrationAsync(
                tcs.Task,
                cancellationRegistration
            );
        }

        //private bool sent = false;
    }
}
