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
			// The interval for rejoining an errored channel. Null means none
			public TimeSpan? channelRejoinInterval = TimeSpan.FromSeconds(1);
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

		public delegate void OnMessageDelegate(string message);
		public OnMessageDelegate OnMessage;

		public delegate void OnClosedDelegate(ushort code, string message);
		public OnClosedDelegate OnClose;

		public delegate void OnErrorDelegate(string message);
		public OnErrorDelegate OnError;

		#endregion


		#region properties

		public IWebsocket websocket { get; private set; }
		private readonly IWebsocketFactory websocketFactory;
		internal readonly Options opts;

		private uint? heartbeatTimer = null;
		private Dictionary<string, Channel> channels = new Dictionary<string, Channel>();

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

			var query = string.Join("&", stringParams);

			var builder = new UriBuilder(string.Format("{0}/websocket", url));
			builder.Query = query;

			return builder.Uri;
		}

		private void SendHeartbeat() {

			if (state != State.Open || opts.heartbeatInterval == null) {
				return;
			}

			Push(new Message("phoenix", "heartbeat", null, null));
			heartbeatTimer = opts.delayedExecutor.Execute(SendHeartbeat, opts.heartbeatInterval.Value);
		}

		private void RejoinChannels() {
			foreach (var channel in channels.Values) {
				channel.Rejoin();
			}
		}

		private void TriggerChannelError(string reason) {
			channels.Values
				.ToList() // copy to allow mutation of channels
				.ForEach(ch => ch.SocketTerminated(reason));
		}

		private void CancelHeartbeat() {
			if (heartbeatTimer.HasValue) {
				opts.delayedExecutor.Cancel(heartbeatTimer.Value);
				heartbeatTimer = null;
			}
		}

		internal void Remove(Channel channel) {
			if (channels.ContainsKey(channel.topic) && channels[channel.topic] == channel) {
				channels.Remove(channel.topic);
			}
		}

		internal bool Push(Message msg) {

			var json = msg.Serialize();
			Log(LogLevel.Trace, "push", json);

			if (state == State.Open) {
				websocket.Send(json);
				return true;
			}

			return false;
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

			if (websocket == null) {
				return;
			}

			CancelHeartbeat();
			TriggerChannelError("socket disconnect");
			
			websocket.Close(code, reason);

			// disables callbacks
			state = State.Closed;

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
			// to accommodate for that, we immediately leave and remove existing the channel
			if (channels.ContainsKey(topic)) {
				channels[topic].SocketTerminated("channel replaced");
				channels[topic].Leave();
			}

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
			RejoinChannels();
			SendHeartbeat();

			if (OnOpen != null) {
				OnOpen();
			}
		}

		private void WebsocketOnClose(IWebsocket ws, ushort code, string message) {

			if (ws != websocket || state == State.Closed) {
				return;
			}

			Log(LogLevel.Debug, "socket", string.Format("on close: ({0}) - {1}", code, message ?? "NONE"));

			state = State.Closed;
			TriggerChannelError("socket close");
			CancelHeartbeat();

			if (OnClose != null) {
				OnClose(code, message);
			}

			websocket = null;
		}

		private void WebsocketOnError(IWebsocket ws, string message) {

			if (ws != websocket || state == State.Closed) {
				return;
			}

			Log(LogLevel.Info, "socket", message ?? "unknown");

			state = State.Closed;
			TriggerChannelError("socket error");
			CancelHeartbeat();

			if (OnError != null) {
				OnError(message);
			}
			
			websocket = null;
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
