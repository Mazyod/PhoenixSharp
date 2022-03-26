using System;
using System.Linq;
using System.Collections.Generic;


namespace Phoenix {

	public sealed class Socket {

		#region nested types

		public enum Event {
			Open,
			Close,
			Error,
			Message
		}

		private struct Subscription {
			public readonly Action callback;

			public Subscription(Action callback) {
				this.callback = callback;
			}
		}

		public sealed class Options {
			// The serializer's protocol version to send on connect.
			public string vsn = "2.0.0";
			// Message serializer to allow different serialization methods
			public IMessageSerializer messageSerializer = new JSONMessageSerializer();
			// The default timeout to trigger push timeouts.
			public TimeSpan timeout = TimeSpan.FromSeconds(10);
			// The interval for rejoining an errored channel. Null means none
			public Func<int, TimeSpan> rejoinAfter = (tries) => {
				List<uint> rawIntervals = new() { 1_000, 2_000, 5_000 };
				List<TimeSpan> intervals = rawIntervals.Select(i => TimeSpan.FromMilliseconds(i)).ToList();

				if (tries > intervals.Count) {
					return TimeSpan.FromSeconds(10);
				} else {
					return intervals[tries - 1];
				}
			};
			// The interval for reconnecting in the event of a connection error.
			public Func<int, TimeSpan> reconnectAfter = (tries) => {
				// TODO: cache these objects to avoid allocations
				List<uint> rawIntervals = new() { 10, 50, 100, 150, 200, 250, 500, 1000, 2000 };
				List<TimeSpan> intervals = rawIntervals.Select(x => TimeSpan.FromMilliseconds(x)).ToList();

				if (tries > intervals.Count) {
					return TimeSpan.FromSeconds(5);
				} else {
					return intervals[tries - 1];
				}
			};
			// The interval to send a heartbeat message. Null means disable
			public TimeSpan? heartbeatInterval = TimeSpan.FromSeconds(30);
			// The optional function for specialized logging
			public ILogger logger = null;
			// The object responsible for performing delayed executions
			public IDelayedExecutor delayedExecutor = new TimerBasedExecutor();
		}

		#endregion


		#region events

		public delegate void OnOpenDelegate();
		public OnOpenDelegate OnOpen;

		public delegate void OnMessageDelegate(Message message);
		public OnMessageDelegate OnMessage;

		public delegate void OnClosedDelegate(ushort code, string message);
		public OnClosedDelegate OnClose;

		public delegate void OnErrorDelegate(string message);
		public OnErrorDelegate OnError;

		#endregion


		#region properties

		/**
		 *	In PhoenixJS, listening to socket events is done by passing a callback and
		 *	holding the returned reference string in order to unsubscribe later.
		 *
		 *	In C#, delegates are much more convenient and fit the paradigm better. Hence,
		 *	we simple use delegate +=, -= to subscribe and unsubscribe.
		 */
		// private readonly Dictionary<Event, List<Subscription>> stateChangeCallbacks = new();

		private readonly List<Channel> channels = new();
		private readonly List<Action> sendBuffer = new();
		private uint @ref = 0;

		// TODO: support defaultEncode/defaultDecoder

		private bool closeWasClean = false;

		// TODO: binaryType?

		// private uint connectClock = 1;
		private readonly string endPoint;
		private readonly Dictionary<string, string> @params;
		private readonly Scheduler reconnectTimer;

		public IWebsocket conn { get; private set; }
		// convenience
		public WebsocketState? state {
			get { return conn?.state; }
		}
		private readonly IWebsocketFactory websocketFactory;
		internal readonly Options opts;

		private DelayedExecution? heartbeatTimer = null;
		private string pendingHeartbeatRef = null;

		#endregion


		public Socket(string endPoint, Dictionary<string, string> @params, IWebsocketFactory websocketFactory, Options opts = null) {
			this.endPoint = endPoint;
			this.@params = @params;
			this.websocketFactory = websocketFactory;
			this.opts = opts ?? new Options();

			reconnectTimer = new(
					() => Teardown(() => Connect()),
					opts.reconnectAfter,
					opts.delayedExecutor
			);
		}

		// NOTE: ReplaceTransport functionality not support in this library

		// NOTE: Protocol inference not support in C# client

		private Uri EndPointURL() {
			// very primitive query string builder
			var @params = this.@params ?? new();
			@params["vsn"] = opts.vsn;

			var stringParams = @params
					.Select(pair => string.Format("{0}={1}", pair.Key, pair.Value))
					.ToArray();

			var builder = new UriBuilder($"{@endPoint}/websocket") {
				Query = string.Join("&", stringParams)
			};

			return builder.Uri;
		}

		public void Disconnect(Action callback = null, ushort? code = null, string reason = null) {
			// connectClock++;
			closeWasClean = true;
			reconnectTimer.Reset();
			Teardown(callback, code, reason);
		}

		public void Connect() {
			// connectClock++;
			if (conn != null) {
				return;
			}

			closeWasClean = false;

			var config = new WebsocketConfiguration() {
				uri = EndPointURL(),
				onOpenCallback = OnConnOpen,
				onCloseCallback = OnConnClose,
				onErrorCallback = OnConnError,
				onMessageCallback = OnConnMessage
			};

			conn = websocketFactory.Build(config);

			conn.Connect();
		}

		internal void Log(LogLevel level, string source, string message) {
			opts.logger.Log(level, source, message);
		}

		internal bool HasLogger() {
			return opts.logger != null;
		}

		// PhoenixJS: we use C# delegates instead of callbacks
		//
		// public Subscription OnOpen(Action callback)
		// public Subscription OnClose(Action callback)
		// public Subscription OnError(Action callback)
		// public Subscription OnMessage(Action callback)

		private void OnConnOpen(IWebsocket websocket) {
			if (HasLogger()) {
				Log(LogLevel.Debug, "transport", $"Connected to {EndPointURL()}");
			}

			closeWasClean = false;
			// establishedConnections++;
			FlushSendBuffer();
			reconnectTimer.Reset();
			ResetHeartbeat();

			OnOpen?.Invoke();
		}

		private void HeartbeatTimeout() {
			if (pendingHeartbeatRef != null) {
				pendingHeartbeatRef = null;
				if (HasLogger()) {
					Log(LogLevel.Debug, "transport", "heartbeat timeout. Attempting to re-establish connection");
				}
				AbnormalClose("heartbeat timeout");
			}
		}

		private void ResetHeartbeat() {
			// we don't check skipHeartbeat on conn since we always use websocket transport
			// however, we do check if heartbeatInterval is set
			if (opts.heartbeatInterval == null) {
				return;
			}

			pendingHeartbeatRef = null;
			heartbeatTimer?.Cancel();

			opts.delayedExecutor.Execute(SendHeartbeat, opts.heartbeatInterval.Value);
		}

		private void Teardown(Action callback = null, ushort? code = null, string reason = null) {
			if (conn == null) {
				callback?.Invoke();
				return;
			}

			// See: comment on the method itself
			// WaitForBufferDone(() => {

			// if (conn != null) {
			if (code.HasValue) {
				conn.Close(code.Value, reason);
			} else {
				conn.Close();
			}
			// }

			WaitForSocketClosed(() => {
				if (conn != null) {
					// TODO: not sure if this is important at all?
					// this.conn.onclose = function (){ } // noop

					conn = null;
				}
				callback?.Invoke();
			});

			// });
		}

		// PhoenixJS: not sure how to check for bufferedAmount in C#
		//
		// private void WaitForBufferDone(Action callback, uint tries = 1) {
		// 	if (tries == 5 || conn == null || conn.bufferedAmount == 0) {
		// 		callback();
		// 		return;
		// 	}

		// 	opts.delayedExecutor.Execute(
		// 			() => WaitForBufferDone(callback, tries + 1),
		// 			TimeSpan.FromMilliseconds(150 * tries)
		// 	);
		// }

		private void WaitForSocketClosed(Action callback, uint tries = 1) {
			if (tries == 5 || conn == null || conn.state == WebsocketState.Closed) {
				callback();
				return;
			}

			opts.delayedExecutor.Execute(
					() => WaitForSocketClosed(callback, tries + 1),
					TimeSpan.FromMilliseconds(150 * tries)
			);
		}

		private void OnConnClose(IWebsocket websocket, ushort code, string reason) {
			if (HasLogger()) {
				Log(LogLevel.Debug, "transport", $"Close {code} {reason}");
			}

			TriggerChanError();
			heartbeatTimer?.Cancel();

			if (!closeWasClean && code != 1_000) {
				reconnectTimer.ScheduleTimeout();
			}

			OnClose?.Invoke(code, reason);
		}

		private void OnConnError(IWebsocket websocket, string error) {
			if (HasLogger()) {
				Log(LogLevel.Debug, "transport", $"Error {error}");
			}

			// TODO: pass callback parameters
			// callback(error, transportBefore, establishedBefore)
			
			OnError?.Invoke(error);

			TriggerChanError();
		}

		private void TriggerChanError() {
			channels.ForEach(channel => {
				if (!(channel.IsErrored() || channel.IsLeaving() || channel.IsClosed())) {
					channel.Trigger(new Message(@event: Message.InBoundEvent.phx_error.ToString()));
				}
			});
		}

		internal bool IsConnected() {
			return state == WebsocketState.Open;
		}

		internal void Remove(Channel channel) {
			// PhoenixJS: see the note above regarding stateChangeCallbacks
			// this.off(channel.stateChangeRefs)
			channels.Remove(channel);
		}

		// private void Off(List<string> refs)

		public Channel Channel(string topic, Dictionary<string, object> chanParams = null) {
			var chan = new Channel(topic, chanParams, this);
			channels.Add(chan);
			return chan;
		}

		internal void Push(Message message) {
			if (HasLogger()) {
				// let {topic, event, payload, ref, join_ref} = data
				Log(LogLevel.Debug, "push", $"Pushing {message}");
			}

			void encodeThenSend() => conn.Send(opts.messageSerializer.Serialize(message));
			if (IsConnected()) {
				encodeThenSend();
			} else {
				sendBuffer.Add(encodeThenSend);
			}
		}

		internal string MakeRef() {
			// overflows are fine in C#, they just wrap
			return (++@ref).ToString();
		}

		private void SendHeartbeat() {
			if (!opts.heartbeatInterval.HasValue
					|| (pendingHeartbeatRef != null && !IsConnected())) {
				return;
			}

			pendingHeartbeatRef = MakeRef();
			Push(new Message(
					topic: "phoenix",
					@event: "heartbeat",
					@ref: pendingHeartbeatRef
			));

			heartbeatTimer = opts.delayedExecutor.Execute(
					() => HeartbeatTimeout(),
					opts.heartbeatInterval.Value
			);
		}

		private void AbnormalClose(string reason) {
			closeWasClean = false;
			if (IsConnected()) {
				conn.Close(1_000, reason);
			}
		}

		private void FlushSendBuffer() {
			if (IsConnected() && sendBuffer.Count > 0) {
				sendBuffer.ForEach(callback => callback());
				sendBuffer.Clear();
			}
		}

		private void OnConnMessage(IWebsocket websocket, string rawMessage) {
			var message = opts.messageSerializer.Deserialize(rawMessage);

			if (message.@ref != null && message.@ref == pendingHeartbeatRef && opts.heartbeatInterval.HasValue) {
				heartbeatTimer?.Cancel();
				pendingHeartbeatRef = null;
				opts.delayedExecutor.Execute(() => SendHeartbeat(), opts.heartbeatInterval.Value);
			}

			if (HasLogger()) {
				// TODO: `${payload.status || ""} ${topic} ${event} ${ref && "(" + ref + ")" || ""}`, payload
				Log(LogLevel.Debug, "receive", $"Received {message}");
			}

			channels.ForEach(channel => {
				// violates tell don't ask, but that's how Phoenix JS is implemented
				if (channel.IsMember(message)) {
					channel.Trigger(message);
				}
			});

			OnMessage?.Invoke(message);
		}

		internal void LeaveOpenTopic(string topic) {
			var dupChannel = channels.Find(channel =>
				channel.topic == topic && (channel.IsJoined() || channel.IsJoining()));

			if (dupChannel != null) {
				if (HasLogger()) {
					Log(LogLevel.Debug, "transport", $"Leaving duplicate channel topic {topic}");
					dupChannel.Leave();
				}
			}
		}
	}
}
