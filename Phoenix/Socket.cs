using System;
using System.Linq;
using System.Collections.Generic;


namespace Phoenix {
	// ## Socket Connection
	//
	// A single connection is established to the server and
	// channels are multiplexed over the connection.
	// Connect to the server using the `Socket` class:
	//
	//     let socket = new Socket("/ws", {params: {userToken: "123"}})
	//     socket.connect()
	//
	// The `Socket` constructor takes the mount point of the socket,
	// the authentication params, as well as options that can be found in
	// the Socket docs, such as configuring the `LongPoll` transport, and
	// heartbeat.

	// ## Socket Hooks
	//
	// Lifecycle events of the multiplexed connection can be hooked into via
	// `socket.onError()` and `socket.onClose()` events, ie:
	//
	//     socket.onError( () => console.log("there was an error with the connection!") )
	//     socket.onClose( () => console.log("the connection dropped") )

	// ### onError hooks
	//
	// `onError` hooks are invoked if the socket connection drops, or the channel
	// crashes on the server. In either case, a channel rejoin is attempted
	// automatically in an exponential backoff manner.
	//
	// ### onClose hooks
	//
	// `onClose` hooks are invoked only in two cases. 1) the channel explicitly
	// closed on the server, or 2). The client explicitly closed, by calling
	// `channel.leave()`

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

		public class Options {
			// optimizing object allocation
			private static int[] expBackoff = { 1, 2, 5, 10 };

			// timeout - The default timeout to trigger push timeouts.
			public TimeSpan timeout = TimeSpan.FromSeconds(10);
			// heartbeatInterval - The interval to send a heartbeat message
			public TimeSpan heartbeatInterval = TimeSpan.FromSeconds(30);
			// reconnectAfter - The optional function that returns the reconnect interval.
			public Func<int, TimeSpan> reconnectAfter = (tries) => {
				return TimeSpan.FromSeconds(tries < expBackoff.Length ? expBackoff[tries] : 10);
			};
			// logger - The optional function for specialized logging
			public ILogger logger = null;
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

		private string urlCache;
		private Dictionary<string, string> paramCache;

		private readonly Timer reconnectTimer;
		private readonly Timer heartbeatTimer;

		private HashSet<Channel> channels = new HashSet<Channel>();
		private List<Action> sendBuffer = new List<Action>();
		private uint refCount = 0;

		public State state { get; private set; }


		#endregion

		// Initializes the Socket
		//
		// factory - websocket object factory
		// opts - Optional configuration
		public Socket(IWebsocketFactory factory, Options options = null) {

			websocketFactory = factory;
			opts = options ?? new Options();

			reconnectTimer = new Timer(Reconnect, opts.reconnectAfter);
			heartbeatTimer = new Timer(SendHeartbeat, opts.heartbeatInterval);
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

		private void Reconnect() {
			Connect(urlCache, paramCache);
		}

		private void SendHeartbeat() {

			if (state != State.Open) {
				return;
			}

			Push(new Message() {
				topic = "phoenix",
				@event = "heartbeat",
				payload = null,
				@ref = MakeRef()
			});

			heartbeatTimer.ScheduleTimeout();
		}

		private void FlushSendBuffer() {

			if (state == State.Open) {
				sendBuffer.ForEach(callback => callback());
				sendBuffer.Clear();
			}
		}

		private void TriggerChanError() {
			foreach (var channel in channels) {
				channel.Trigger(new Message() { @event = Message.InBoundEvent.Error.AsString() });
			}
		}

		internal void Remove(Channel channel) {
			channels.Remove(channel);
		}

		internal void Push(Message msg) {

			var json = msg.Serialize();
			Action callback = () => websocket.Send(json);
			// Log("push", data.ToString(), data);

			if (state == State.Open) {
				callback();
			} else {
				sendBuffer.Add(callback);
			}
		}

		// Return the next message ref, accounting for overflows
		internal string MakeRef() {
			return (++refCount).ToString();
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

			reconnectTimer.Reset();
			heartbeatTimer.Reset();

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

			urlCache = url;
			paramCache = parameters;

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

		public Channel MakeChannel(string topic, Dictionary<string, object> channelParameters = null) {

			var channel = new Channel(topic, channelParameters, this);
			channels.Add(channel);

			return channel;
		}

		#endregion

		#region websocket callbacks

		private void WebsocketOnOpen() {
			// Log("transport", string.Format("connected to {0}", endpointURL), null);
			FlushSendBuffer();

			reconnectTimer.Reset();
			heartbeatTimer.Reset();
			heartbeatTimer.ScheduleTimeout();

			state = State.Open;

			if (OnOpen != null) {
				OnOpen();
			}
		}

		private void WebsocketOnClose(ushort code, string message) {
			Log(LogLevel.Debug, "socket", "on close");

			if (state == State.Closed) {
				return; // noop
			}

			TriggerChanError();

			heartbeatTimer.Reset();
			reconnectTimer.ScheduleTimeout();

			state = State.Closed;
			websocket = null;

			if (OnClose != null) {
				OnClose(code, message);
			}
		}

		private void WebsocketOnError(string message) {
			Log(LogLevel.Info, "socket", message ?? "unknown");

			if (state == State.Closed) {
				return; // noop
			}

			TriggerChanError();

			heartbeatTimer.Reset();
			reconnectTimer.ScheduleTimeout();

			state = State.Closed;
			websocket = null;

			if (OnError != null) {
				OnError(message);
			}
		}

		private void WebsocketOnMessage(string data) {

			var msg = MessageSerialization.Deserialize(data);
			Log(LogLevel.Trace, "socket", string.Format("received: {0}", msg.ToString()));

			channels
				.Where(ch => ch.topic == msg.topic)
				.ToList() // create a copy to avoid mutating while iterating
				.ForEach(channel => channel.Trigger(msg));

			if (OnMessage != null) {
				OnMessage(data);
			}
		}

		#endregion
	}
}