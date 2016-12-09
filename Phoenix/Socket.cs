using System;
using System.Linq;
using System.Collections.Generic;


namespace Phoenix {

	public sealed class Socket {

		#region nested types

		public enum Events {
			Open,
			Close,
			Error,
			Message
		}

		public enum State {
			Closed,
			Closing,
			Connecting,
			Open,
		}

		public sealed class Options {
			// The default timeout to trigger push timeouts.
			public TimeSpan timeout = TimeSpan.FromSeconds(10);
			// The interval to send a heartbeat message.
			public TimeSpan heartbeatInterval = TimeSpan.FromSeconds(30);
			// The optional function for specialized logging
			public ILogger logger = null;
			// The object responsible for performing delayed executions
			public IDelayedExecutor delayedExecutor = new TimerBasedExecutor();
		}

		#endregion


		#region events

		public delegate void OnOpenDelegate();
		public OnOpenDelegate OnOpen;

		public delegate void OnMessageDelegate(string message);
		public OnMessageDelegate OnMessage;

		public delegate void OnClosedDelegate(ushort code, string message);
		public OnClosedDelegate OnClose;

		public delegate void OnErrorDelegate(string message);
		public OnErrorDelegate OnError;

		#endregion


		#region properties

		private readonly IWebsocketFactory websocketFactory;
		internal readonly Options opts;
		private IWebsocket websocket;

		private uint? heartbeatTimer = null;

		private Dictionary<string, Channel> channels = new Dictionary<string, Channel>();
		private List<string> sendBuffer = new List<string>();

		public State state { get; private set; }

		#endregion


		public Socket(IWebsocketFactory factory, Options options = null) {

			websocketFactory = factory;
			opts = options ?? new Options();
		}


		#region private & internal methods

		private Uri BuildEndpointURL(string url, Dictionary<string, string> parameters) {
			// very primitive query string builder
			var stringParams = (parameters ?? new Dictionary<string, string>())
				.Select(pair => string.Format("{0}={1}", pair.Key, pair.Value))
				.ToArray();

			var query = String.Join("&", stringParams);

			var builder = new UriBuilder(string.Format("{0}/websocket", url));
			builder.Query = query;

			return builder.Uri;
		}

		private void SendHeartbeat() {

			if (state != State.Open) {
				return;
			}

			Push(new Message() {
				topic = "phoenix",
				@event = "heartbeat",
				payload = null
			});

			heartbeatTimer = opts.delayedExecutor.Execute(SendHeartbeat, opts.heartbeatInterval);
		}

		private void FlushSendBuffer() {
			sendBuffer.ForEach(websocket.Send);
			sendBuffer.Clear();
		}

		private void TriggerChannelError() {
			foreach (var channel in channels.Values) {
				channel.TriggerError();
			}
		}

		private void CancelHeartbeat() {
			if (heartbeatTimer.HasValue) {
				opts.delayedExecutor.Cancel(heartbeatTimer.Value);
				heartbeatTimer = null;
			}
		}

		internal void Remove(Channel channel) {
			channels.Remove(channel.topic);
		}

		internal void Push(Message msg) {

			var json = msg.Serialize();

			Log(LogLevel.Debug,"push", json);

			if (state == State.Open) {
				websocket.Send(json);
			} else {
				sendBuffer.Add(json);
			}
		}

		// Logs the message. Override `this.logger` for specialized logging. noops by default
		internal void Log(LogLevel level, string kind, string msg) {
			if (opts.logger != null) {
				opts.logger.Log(level, kind, msg);
			}
		}

		#endregion


		#region public methods

		public void Disconnect(ushort? code = null, string reason = null) {

			CancelHeartbeat();
			TriggerChannelError();

			if (websocket == null) {
				return;
			}

			// disables callbacks
			state = State.Closed;

			websocket.Close(code, reason);
			websocket = null;
		}

		// params - The params to send when connecting, for example `{user_id: userToken}`
		public void Connect(string url, Dictionary<string, string> parameters = null) {

			if (websocket != null) {
				Disconnect();
			}

			var config = new WebsocketConfiguration() {
				uri = BuildEndpointURL(url, parameters),
				onOpenCallback = WebsocketOnOpen,
				onCloseCallback = WebsocketOnClose,
				onErrorCallback = WebsocketOnError,
				onMessageCallback = WebsocketOnMessage
			};

			websocket = websocketFactory.Build(config);

			state = State.Connecting;
			websocket.Connect();
		}

		public Channel MakeChannel(string topic) {
			// Phoenix 1.2+ returns a new channel and closes the old one if we join a topic twice
			var channel = new Channel(topic, this);
			channels[topic] = channel;

			return channel;
		}

		#endregion


		#region websocket callbacks

		private void WebsocketOnOpen(IWebsocket ws) {

			if (ws != websocket) {
				return;
			}

			Log(LogLevel.Debug, "socket", "on open");

			state = State.Open;
			FlushSendBuffer();
			SendHeartbeat();

			if (OnOpen != null) {
				OnOpen();
			}
		}

		private void WebsocketOnClose(IWebsocket ws, ushort code, string message) {

			if (ws != websocket || state == State.Closed) {
				return;
			}

			Log(LogLevel.Debug, "socket", "on close");

			state = State.Closed;
			TriggerChannelError();
			CancelHeartbeat();

			websocket = null;

			if (OnClose != null) {
				OnClose(code, message);
			}
		}

		private void WebsocketOnError(IWebsocket ws, string message) {

			if (ws != websocket || state == State.Closed) {
				return;
			}

			Log(LogLevel.Info, "socket", message ?? "unknown");

			state = State.Closed;
			TriggerChannelError();
			CancelHeartbeat();

			websocket = null;

			if (OnError != null) {
				OnError(message);
			}
		}

		private void WebsocketOnMessage(IWebsocket ws, string data) {

			if (ws != websocket) {
				return;
			}

			var msg = MessageSerialization.Deserialize(data);
			Log(LogLevel.Trace, "socket", string.Format("received: {0}", msg.ToString()));

			if (channels.ContainsKey(msg.topic)) {
				channels[msg.topic].Trigger(msg);
			}

			if (OnMessage != null) {
				OnMessage(data);
			}
		}

		#endregion
	}
}
