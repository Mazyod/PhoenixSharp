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

        private readonly StatusHookTable _recHooks = new StatusHookTable();
        private IDelayedExecution? _delayedExecution;
        private Reply? _receivedResp;
        private string? _refEvent;
        private TimeSpan _timeout;

        // internal state
        internal string? Ref;

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

        public void Resend(TimeSpan timeout)
        {
            _timeout = timeout;
            Reset();
            Send();
        }

        public void Send()
        {
            if (HasReceived(ReplyStatus.Timeout))
            {
                return;
            }

            StartTimeout();
            // sent = true;
            _channel.Socket.Push(new Message(
                _channel.Topic,
                _event,
                _payload?.Invoke(),
                Ref,
                _channel.JoinRef
            ));
        }

        public Push Receive(ReplyStatus status, Action<Reply> callback)
        {
            if (callback == null)
                throw new ArgumentNullException(nameof(callback));

            if (HasReceived(status) && _receivedResp.HasValue)
            {
                callback(_receivedResp.Value);
            }

            if (!_recHooks.TryGetValue(status, out var callbacks))
            {
                callbacks = new List<Action<Reply>>();
                _recHooks[status] = callbacks;
            }

            callbacks.Add(callback);

            return this;
        }

        internal void Reset()
        {
            CancelRefEvent();
            Ref = null;
            _refEvent = null;
            _receivedResp = null;
            // sent = false;
        }

        private void MatchReceive(Reply? reply)
        {
            if (!reply.HasValue || !_recHooks.TryGetValue(reply.Value.ReplyStatus, out var callbacks))
            {
                return;
            }

            callbacks.ForEach(callback => callback(reply.Value));
        }

        private void CancelRefEvent()
        {
            if (_refEvent != null)
            {
                _channel.Off(_refEvent);
            }
        }

        internal void CancelTimeout()
        {
            _delayedExecution?.Cancel();
            _delayedExecution = null;
        }

        internal void StartTimeout()
        {
            // PhoenixJS: null check implicit
            CancelTimeout();

            Ref = _channel.Socket.MakeRef();
            _refEvent = Channel.ReplyEventName(Ref);

            _channel.On(_refEvent, message =>
            {
                CancelRefEvent();
                CancelTimeout();
                _receivedResp = message.Payload?.Unbox<Reply?>();
                MatchReceive(_receivedResp);
            });

            _delayedExecution =
                _channel.Socket.Opts.DelayedExecutor.Execute(() => { Trigger(ReplyStatus.Timeout); }, _timeout);
        }

        private bool HasReceived(ReplyStatus status)
        {
            return _receivedResp?.ReplyStatus == status;
        }

        internal void Trigger(ReplyStatus status)
        {
            var serializer = _channel.Socket.Opts.MessageSerializer;

            _channel.Trigger(new Message(
                @event: _refEvent,
                payload: serializer.Box(new Dictionary<string, object>
                    {
                        {"status", status.Serialized()}
                    }
                )
            ));
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

            // If we already have a response, return it immediately
            if (_receivedResp.HasValue)
            {
                return Task.FromResult(_receivedResp.Value);
            }

            void OnReply(Reply reply)
            {
                tcs.TrySetResult(reply);
            }

            Receive(ReplyStatus.Ok, OnReply);
            Receive(ReplyStatus.Error, OnReply);
            Receive(ReplyStatus.Timeout, OnReply);

            cancellationToken.Register(() => tcs.TrySetCanceled());

            return tcs.Task;
        }

        //private bool sent = false;
    }
}
