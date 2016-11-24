using System;
using System.Linq;
using System.Collections.Generic;
using WebSocketSharp;
using Newtonsoft.Json;


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

	public class Socket {

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
			//   timeout - The default timeout to trigger push timeouts.
			public TimeSpan timeout = TimeSpan.FromSeconds(10);
			//   heartbeatInterval - The interval to send a heartbeat message
			public TimeSpan heartbeatInterval = TimeSpan.FromSeconds(30);
			//   reconnectAfter - The optional function that returns the reconnect interval.
			public Func<int, TimeSpan> reconnectAfter = (tries) => { 
				int[] expBackoff = { 1, 2, 5, 10 };
				return TimeSpan.FromSeconds(tries < expBackoff.Length ? expBackoff[tries] : 10);
			};
			//   logger - The optional function for specialized logging, ie:
			//     `logger: (kind, msg, data) => { console.log(`${kind}: ${msg}`, data) }
			public Action<string, string, object> logger = (a, b, c) => {
			};
		}

		#endregion

		#region properties

		private WebSocket websocket;
		internal Options opts;

		private string urlCache;
		private Dictionary<string, string> paramCache;

		private Timer reconnectTimer;
		private Timer heartbeatTimer;

		private HashSet<Channel> channels = new HashSet<Channel>();
		private List<Action> sendBuffer = new List<Action>();
		private uint refCount = 0;
		private Dictionary<Events, Action> stateChangeCallbacks = new Dictionary<Events, Action>();

		public State state {
			get {
				if (websocket == null) {
					return State.Closed;
				}

				switch (websocket.ReadyState) {
				case WebSocketState.Connecting:
					return State.Connecting;
				case WebSocketState.Open:
					return State.Open;
				case WebSocketState.Closing:
					return State.Closing;
				default:
					return State.Closed;
				}
			}
		}

		#endregion

		// Initializes the Socket
		//
		// endPoint - The string WebSocket endpoint, ie, "ws://example.com/ws",
		//                                               "wss://example.com"
		// opts - Optional configuration
		public Socket(Options options = null) {

			opts = options ?? new Options();

			reconnectTimer = new Timer(Reconnect, opts.reconnectAfter);
			heartbeatTimer = new Timer(SendHeartbeat, opts.heartbeatInterval);
		}


		#region private & internal methods

		private Uri BuildEndpointURL(string url, Dictionary<string, string> parameters) {
			// very primitive query string builder
			var stringParams = parameters
				.Select(pair => string.Format("{0}={1}", pair.Key, pair.Value))
				.ToArray();

			var query = String.Join("&", stringParams);

			var builder = new UriBuilder(string.Format("{0}/websocket", url));
			builder.Query = query;

			return builder.Uri;
		}

		private void Reconnect() {

			Disconnect();
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

		private void TriggerStateChangeCallback(Events @event) {
			if (stateChangeCallbacks.ContainsKey(@event)) {
				stateChangeCallbacks[@event].Invoke();
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
		internal void Log(string kind, string msg, object data) {
			opts.logger(kind, msg, data);
		}

		#endregion


		#region public methods

		public void Disconnect(ushort? code = null, string reason = null) {

			reconnectTimer.Reset();
			heartbeatTimer.Reset();

			if (websocket == null) {
				return;
			}

			websocket.OnClose -= WebsocketOnClose; // noop
			websocket.OnError -= WebsocketOnError; // noop

			if (code.HasValue) {
				websocket.Close(code.Value, reason ?? "");
			} else {
				websocket.Close();
			}
			websocket = null;
		}

		// params - The params to send when connecting, for example `{user_id: userToken}`
		public void Connect(string url, Dictionary<string, string> parameters = null) {

			if (websocket != null) {
				Disconnect();
			}

			urlCache = url;
			paramCache = parameters;

			websocket = new WebSocket(BuildEndpointURL(url, parameters).AbsoluteUri);
			websocket.OnOpen += WebsocketOnOpen;
			websocket.OnError += WebsocketOnError;
			websocket.OnClose += WebsocketOnClose;
			websocket.OnMessage += WebsocketOnMessage;

			websocket.Connect();
		}

		// Registers callbacks for connection state change events
		public void On(Events socketEvent, Action callback) {
			stateChangeCallbacks[socketEvent] = callback;
		}

		public Channel MakeChannel(string topic, Dictionary<string, object> channelParameters = null) {

			var channel = new Channel(topic, channelParameters, this);
			channels.Add(channel);

			return channel;
		}

		#endregion

		#region websocket callbacks

		private void WebsocketOnOpen(object sender, EventArgs eventArgs) {
			// Log("transport", string.Format("connected to {0}", endpointURL), null);
			FlushSendBuffer();

			reconnectTimer.Reset();
			heartbeatTimer.Reset();
			heartbeatTimer.ScheduleTimeout();

			TriggerStateChangeCallback(Events.Open);
		}

		private void WebsocketOnClose(object sender, CloseEventArgs eventArgs) {
			Log("transport", "close", eventArgs);
			TriggerChanError();

			heartbeatTimer.Reset();
			reconnectTimer.ScheduleTimeout();

			TriggerStateChangeCallback(Events.Close);
		}

		private void WebsocketOnError(object sender, ErrorEventArgs eventArgs) {
			// Log("transport", error);
			TriggerChanError();
			TriggerStateChangeCallback(Events.Error);
		}

		private void WebsocketOnMessage(object sender, MessageEventArgs eventArgs) {

			var msg = MessageSerialization.Deserialize(eventArgs.Data);

			// this.log("receive", `${payload.status || ""} ${topic} ${event} ${ref && "(" + ref + ")" || ""}`, payload)
			var subscribedChannels = channels.Where(ch => ch.topic == msg.topic);
			foreach (var channel in subscribedChannels) {
				channel.Trigger(msg);
			}

			// TODO: possible customize params
			TriggerStateChangeCallback(Events.Message);
		}

		#endregion
	}
}