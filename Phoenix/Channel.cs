using System;
using System.Collections.Generic;
using SubscriptionTable = System.Collections.Generic.Dictionary<
    string, System.Collections.Generic.List<Phoenix.ChannelSubscription>>;

namespace Phoenix
{
    /**
     * Subscription
     * Represents a subscription to a channel event.
     * We use a class since subscriptions are stored in an array.
     */
    public sealed class ChannelSubscription
    {
        public Action<Message> Callback;
        public string Event;
    }

    public enum ChannelState
    {
        Closed,
        Joining,
        Joined,
        Leaving,
        Errored // errored channels are rejoined automatically
    }

    public class Channel
    {
        private readonly SubscriptionTable _bindings = new SubscriptionTable();
        private readonly Push _joinPush;
        private readonly List<Push> _pushBuffer = new List<Push>();

        /**
         * See the stateChangeRefs comment in Socket.cs
         */
        // internal List<object> stateChangeRefs = new();
        private readonly Scheduler _rejoinTimer;

        public readonly Socket Socket;
        public readonly string Topic;
        private bool _joinedOnce;
        private TimeSpan _timeout;


        public ChannelState State = ChannelState.Closed;

        // TODO: possibly support lazy instantiation of payload (same as Phoenix js)
        public Channel(string topic, Dictionary<string, object> @params, Socket socket)
        {
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

            _joinPush.Receive(ReplyStatus.Ok, message =>
            {
                State = ChannelState.Joined;
                _rejoinTimer?.Reset();
                _pushBuffer.ForEach(push => push.Send());
                _pushBuffer.Clear();
            });

            _joinPush.Receive(ReplyStatus.Error, message =>
            {
                State = ChannelState.Errored;
                if (socket.IsConnected())
                {
                    _rejoinTimer?.ScheduleTimeout();
                }
            });

            OnClose(message =>
            {
                _rejoinTimer?.Reset();
                if (socket.HasLogger())
                {
                    socket.Log(LogLevel.Debug, "channel", $"close {topic}");
                }

                State = ChannelState.Closed;
                // PhoenixJS: See note in socket regarding this
                // basically, we unregister delegates directly in c# instead of offing an array
                // this.off(channel.stateChangeRefs)
                socket.OnError -= SocketOnError;
                socket.OnOpen -= SocketOnOpen;
                socket.Remove(this);
            });

            OnError(message =>
            {
                if (socket.HasLogger())
                {
                    socket.Log(LogLevel.Debug, "channel", $"error {topic}");
                }

                if (IsJoining())
                {
                    _joinPush.Reset();
                }

                State = ChannelState.Errored;
                if (socket.IsConnected())
                {
                    _rejoinTimer?.ScheduleTimeout();
                }
            });

            _joinPush.Receive(ReplyStatus.Timeout, message =>
            {
                if (socket.HasLogger())
                {
                    socket.Log(LogLevel.Debug, "channel", $"timeout {topic} ({JoinRef})");
                }

                var leaveEvent = Message.OutBoundEvent.Leave.Serialized();
                var leavePush = new Push(this, leaveEvent, null, _timeout);
                leavePush.Send();

                State = ChannelState.Errored;
                _joinPush.Reset();

                if (socket.IsConnected())
                {
                    _rejoinTimer?.ScheduleTimeout();
                }
            });

            // on phx_reply, also trigger a message for the push using replyEventName
            On(Message.InBoundEvent.Reply.Serialized(), message =>
            {
                message.Event = ReplyEventName(message.Ref);
                Trigger(message);
            });
        }

        internal string JoinRef => _joinPush.Ref;


        public Push Join(TimeSpan? timeout = null)
        {
            if (_joinedOnce)
            {
                throw new Exception(
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
            var subscription = new ChannelSubscription
            {
                Event = anyEvent,
                Callback = callback
            };

            if (!_bindings.TryGetValue(anyEvent, out var subscriptions))
            {
                subscriptions = new List<ChannelSubscription>();
                _bindings[anyEvent] = subscriptions;
            }

            subscriptions.Add(subscription);

            return subscription;
        }

        public ChannelSubscription On<T>(string anyEvent, Action<T> callback)
        {
            return On(
                anyEvent,
                message => callback(message.Payload.Unbox<T>())
            );
        }

        public bool Off(ChannelSubscription subscription)
        {
            return _bindings.TryGetValue(subscription.Event, out var subscriptions) &&
                   subscriptions.Remove(subscription);
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
            return _bindings.Remove(anyEvent);
        }

        internal bool CanPush()
        {
            return Socket.IsConnected() && IsJoined();
        }

        public Push Push(string @event, object payload = null, TimeSpan? timeout = null)
        {
            if (!_joinedOnce)
            {
                throw new Exception(
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
                _pushBuffer.Add(pushEvent);
            }

            return pushEvent;
        }

        public Push Leave(TimeSpan? timeout = null)
        {
            _rejoinTimer?.Reset();
            _joinPush.CancelTimeout();

            State = ChannelState.Leaving;

            void TriggerClose()
            {
                if (Socket.HasLogger())
                {
                    Socket.Log(LogLevel.Debug, "channel", $"leave {Topic}");
                }

                Trigger(Message.InBoundEvent.Close);
            }

            var leaveEvent = Message.OutBoundEvent.Leave.Serialized();
            var leavePush = new Push(this, leaveEvent, null, timeout ?? _timeout);
            leavePush
                .Receive(ReplyStatus.Ok, _ => TriggerClose())
                .Receive(ReplyStatus.Timeout, _ => TriggerClose());
            leavePush.Send();

            if (!CanPush())
            {
                leavePush.Trigger(ReplyStatus.Ok);
            }

            return leavePush;
        }

        // overrideable message hook
        public virtual IJsonBox OnMessage(Message message)
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

            if (Socket.HasLogger())
            {
                Socket.Log(
                    LogLevel.Info,
                    "Channel",
                    $"dropping outdated message for topic '{Topic}' (joinRef {message.JoinRef} does not match joinRef {JoinRef})"
                );
            }

            return false;
        }

        private void Rejoin(TimeSpan? timeout = null)
        {
            if (IsLeaving())
            {
                return;
            }

            Socket.LeaveOpenTopic(Topic);
            State = ChannelState.Joining;
            _joinPush.Resend(timeout ?? _timeout);
        }

        // Helper method not found in PhoenixJS
        internal void Trigger(Message.InBoundEvent @event)
        {
            Trigger(new Message(@event: @event.Serialized()));
        }

        internal void Trigger(Message message)
        {
            var handledPayload = OnMessage(message);
            if (message.Payload != null && handledPayload == null)
            {
                throw new Exception("channel onMessage callbacks must return payload, modified or unmodified");
            }

            if (!_bindings.TryGetValue(message.Event, out var eventBindings))
            {
                return;
            }

            eventBindings?.ForEach(subscription =>
            {
                message.Payload = handledPayload;
                message.JoinRef ??= JoinRef;
                subscription.Callback(message);
            });
        }

        internal static string ReplyEventName(string @ref)
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


        private void SocketOnError(string message)
        {
            _rejoinTimer?.Reset();
        }

        private void SocketOnOpen()
        {
            _rejoinTimer?.Reset();
            if (IsErrored())
            {
                Rejoin();
            }
        }
    }
}