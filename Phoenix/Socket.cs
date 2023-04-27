using System;
using System.Collections.Generic;
using System.Linq;

namespace Phoenix
{
    public sealed class Socket
    {
        public delegate void OnClosedDelegate(ushort code, string message);

        public delegate void OnErrorDelegate(string message);

        public delegate void OnMessageDelegate(Message message);


        public delegate void OnOpenDelegate();


        /**
         * In PhoenixJS, listening to socket events is done by passing a callback and
         * holding the returned reference string in order to unsubscribe later.
         * 
         * In C#, delegates are much more convenient and fit the paradigm better. Hence,
         * we simple use delegate +=, -= to subscribe and unsubscribe.
         */
        // private readonly Dictionary<Event, List<Subscription>> stateChangeCallbacks = new();
        private readonly List<Channel> _channels = new List<Channel>();

        // TODO: binaryType?

        // private uint connectClock = 1;
        private readonly string _endPoint;
        private readonly Dictionary<string, string> _params;
        private readonly Scheduler _reconnectTimer;

        private readonly IWebsocketFactory _websocketFactory;
        internal readonly Options Opts;

        internal readonly List<Action> SendBuffer = new List<Action>();

        private bool _closeWasClean;

        private IDelayedExecution _heartbeatTimer;
        private string _pendingHeartbeatRef;
        private uint _ref;

        public OnClosedDelegate OnClose;

        public OnErrorDelegate OnError;

        public OnMessageDelegate OnMessage;

        public OnOpenDelegate OnOpen;

        public Socket(
            string endPoint,
            Dictionary<string, string> @params,
            IWebsocketFactory websocketFactory,
            Options opts
        )
        {
            _endPoint = endPoint;
            _params = @params;
            _websocketFactory = websocketFactory;
            Opts = opts ?? throw new NullReferenceException("Socket options required");

            if (Opts.ReconnectAfter != null)
            {
                _reconnectTimer = new Scheduler(
                    () => Teardown(Connect),
                    Opts.ReconnectAfter,
                    Opts.DelayedExecutor
                );
            }
        }

        public IWebsocket Conn { get; private set; }

        // convenience
        public WebsocketState? State => Conn?.State;

        // NOTE: ReplaceTransport functionality not support in this library

        // NOTE: Protocol inference not support in C# client

        private Uri EndPointUrl()
        {
            // very primitive query string builder
            var @params = _params ?? new Dictionary<string, string>();
            @params["vsn"] = Opts.Vsn;

            var stringParams = @params
                .Select(pair => $"{pair.Key}={pair.Value}")
                .ToArray();

            var builder = new UriBuilder($"{_endPoint}/websocket")
            {
                Query = string.Join("&", stringParams)
            };

            return builder.Uri;
        }

        public void Disconnect(Action callback = null, ushort? code = null, string reason = null)
        {
            // connectClock++;
            _closeWasClean = true;
            _reconnectTimer?.Reset();
            Teardown(callback, code, reason);
        }

        public void Connect()
        {
            // connectClock++;
            if (Conn != null)
            {
                return;
            }

            _closeWasClean = false;

            var config = new WebsocketConfiguration
            {
                uri = EndPointUrl(),
                onOpenCallback = OnConnOpen,
                onCloseCallback = OnConnClose,
                onErrorCallback = OnConnError,
                onMessageCallback = OnConnMessage
            };

            Conn = _websocketFactory.Build(config);

            Conn.Connect();
        }

        internal void Log(LogLevel level, string source, string message)
        {
            Opts.Logger.Log(level, source, message);
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

            _closeWasClean = false;
            // establishedConnections++;
            FlushSendBuffer();
            _reconnectTimer?.Reset();
            ResetHeartbeat();

            OnOpen?.Invoke();
        }

        private void HeartbeatTimeout()
        {
            if (_pendingHeartbeatRef == null)
            {
                return;
            }

            _pendingHeartbeatRef = null;
            if (HasLogger())
            {
                Log(LogLevel.Debug, "transport", "heartbeat timeout. Attempting to re-establish connection");
            }

            AbnormalClose("heartbeat timeout");
        }

        private void ResetHeartbeat()
        {
            // we don't check skipHeartbeat on conn since we always use websocket transport
            // however, we do check if heartbeatInterval is set
            if (Opts.HeartbeatInterval == null)
            {
                return;
            }

            _pendingHeartbeatRef = null;
            _heartbeatTimer?.Cancel();

            Opts.DelayedExecutor.Execute(SendHeartbeat, Opts.HeartbeatInterval.Value);
        }

        private void Teardown(Action callback = null, ushort? code = null, string reason = null)
        {
            if (Conn == null || Conn.State == WebsocketState.Closed)
            {
                Conn = null;
                callback?.Invoke();
                return;
            }

            // See: comment on the method itself
            // WaitForBufferDone(() => {

            // if (conn != null) {
            if (code.HasValue)
            {
                Conn.Close(code.Value, reason);
            }
            else
            {
                Conn.Close();
            }
            // }

            WaitForSocketClosed(() =>
            {
                if (Conn != null)
                {
                    // TODO: not sure if this is important at all?
                    // this.conn.onclose = function (){ } // noop
                    Conn = null;
                }

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

        private void WaitForSocketClosed(Action callback, uint tries = 1)
        {
            if (tries == 5 || Conn == null || Conn.State == WebsocketState.Closed)
            {
                callback();
                return;
            }

            Opts.DelayedExecutor.Execute(
                () => WaitForSocketClosed(callback, tries + 1),
                TimeSpan.FromMilliseconds(150 * tries)
            );
        }

        private void OnConnClose(IWebsocket websocket, ushort code, string reason)
        {
            if (HasLogger())
            {
                Log(LogLevel.Debug, "transport", $"Close {code} {reason}");
            }

            TriggerChanError();
            _heartbeatTimer?.Cancel();

            if (!_closeWasClean && code != 1_000)
            {
                _reconnectTimer?.ScheduleTimeout();
            }

            OnClose?.Invoke(code, reason);
        }

        private void OnConnError(IWebsocket websocket, string error)
        {
            if (HasLogger())
            {
                Log(LogLevel.Debug, "transport", $"Error {error}");
            }

            OnError?.Invoke(error);

            TriggerChanError();
        }

        private void TriggerChanError()
        {
            _channels.ForEach(channel =>
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
            _channels.Remove(channel);
        }

        // private void Off(List<string> refs)

        public Channel Channel(string topic, Dictionary<string, object> chanParams = null)
        {
            var chan = new Channel(topic, chanParams, this);
            _channels.Add(chan);
            return chan;
        }

        internal void Push(Message message)
        {
            if (HasLogger()) // let {topic, event, payload, ref, join_ref} = data
            {
                Log(LogLevel.Debug, "push", $"Pushing {message}");
            }

            void EncodeThenSend()
            {
                Conn.Send(Opts.MessageSerializer.Serialize(message));
            }

            if (IsConnected())
            {
                EncodeThenSend();
            }
            else
            {
                SendBuffer.Add(EncodeThenSend);
            }
        }

        internal string MakeRef()
        {
            // overflows are fine in C#, they just wrap
            return (++_ref).ToString();
        }

        private void SendHeartbeat()
        {
            if (!Opts.HeartbeatInterval.HasValue
                || (_pendingHeartbeatRef != null && !IsConnected()))
            {
                return;
            }

            _pendingHeartbeatRef = MakeRef();
            Push(new Message(
                "phoenix",
                "heartbeat",
                @ref: _pendingHeartbeatRef
            ));

            _heartbeatTimer = Opts.DelayedExecutor.Execute(
                HeartbeatTimeout,
                Opts.HeartbeatInterval.Value
            );
        }

        internal void AbnormalClose(string reason)
        {
            _closeWasClean = false;
            if (IsConnected())
            {
                Conn.Close(1_000, reason);
            }
        }

        internal void FlushSendBuffer()
        {
            if (!IsConnected() || SendBuffer.Count <= 0)
            {
                return;
            }

            SendBuffer.ForEach(callback => callback());
            SendBuffer.Clear();
        }

        private void OnConnMessage(IWebsocket websocket, string rawMessage)
        {
            var message = Opts.MessageSerializer.Deserialize<Message>(rawMessage);

            if (message.Ref != null && message.Ref == _pendingHeartbeatRef && Opts.HeartbeatInterval.HasValue)
            {
                _heartbeatTimer?.Cancel();
                _pendingHeartbeatRef = null;
                Opts.DelayedExecutor.Execute(SendHeartbeat, Opts.HeartbeatInterval.Value);
            }

            if (HasLogger())
            {
                Log(LogLevel.Debug, "receive", $"Received {message}");
            }

            // copy channels before triggering callbacks, since they might modify the channels list
            _channels.ToList().ForEach(channel =>
            {
                // violates tell don't ask, but that's how Phoenix JS is implemented
                if (channel.IsMember(message))
                {
                    channel.Trigger(message);
                }
            });

            OnMessage?.Invoke(message);
        }

        internal void LeaveOpenTopic(string topic)
        {
            var dupChannel = _channels.Find(channel =>
                channel.Topic == topic && (channel.IsJoined() || channel.IsJoining()));

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
            public ILogger Logger = null;

            // The interval for reconnecting in the event of a connection error. Null means none.
            public Func<int, TimeSpan> ReconnectAfter = tries =>
                tries > ConnectIntervals.Length
                    ? TimeSpan.FromSeconds(5)
                    : TimeSpan.FromMilliseconds(ConnectIntervals[tries - 1]);

            // The interval for rejoining an errored channel. Null means none.
            public Func<int, TimeSpan> RejoinAfter = tries =>
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
                MessageSerializer = messageSerializer;
            }
        }
    }
}