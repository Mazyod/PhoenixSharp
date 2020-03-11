using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;


namespace Phoenix {

	public sealed class Channel {

		#region nested types

		public enum State {
			Closed,
			Joining,
			Joined,
			Errored, // errored channels are rejoined automatically
		}

		#endregion


		#region properties

		private static uint refCount = 0;


		public Socket socket;
		public State state = State.Closed;
		public readonly string topic;

		private Dictionary<string, Action<Message>> bindings = new Dictionary<string, Action<Message>>();
		private Dictionary<string, Push> activePushes = new Dictionary<string, Push>();
		private List<Message> sendBuffer = new List<Message>();

		private string joinRef;
		private uint? reconnectTimer;

		public bool canPush {
			get { return socket.state == Socket.State.Open && state == State.Joined; }
		}

		private Push joinPush {
			get { return activePushes[joinRef]; }
		}

		#endregion


		public Channel(string topic, Socket socket) {

			this.topic = topic;
			this.socket = socket;
		}


		#region public methods

		public Push Join(Dictionary<string, object> parameters = null, TimeSpan? timeout = null) {

			if (joinRef != null) {
				throw new Exception("tried to join multiple times. 'join' can only be called a single time per channel instance");
			}

			state = State.Joining;

			var payload = parameters == null ? null : JObject.FromObject(parameters);
			var message = MakeMessage(Message.OutBoundEvent.phx_join, null, payload);
			joinRef = message.@ref;
			var push = Push(message, timeout);

			return push;
		}

		public void Rejoin() {

			if (joinRef == null) {
				throw new Exception(string.Format("tried to rejoin before joining once: {0}", topic));
			}

			if (state != State.Errored) {
				return;
			}

			if (socket.state != Socket.State.Open) {
				if (socket.opts.channelRejoinInterval.HasValue) {
					reconnectTimer = socket.opts.delayedExecutor.Execute(Rejoin, socket.opts.channelRejoinInterval.Value);
				}
				return;
			}

			state = State.Joining;

			joinPush.reply = null;
			Push(joinPush, null);
		}


		public void On(Message.InBoundEvent @event, Action<Message> callback) {
			On(@event.ToString(), callback);
		}

		public void On(string anyEvent, Action<Message> callback) {
			bindings[anyEvent] = callback;
		}

		public void Off(Enum eventEnum) {
			Off(eventEnum.ToString());
		}

		public void Off(string anyEvent) {
			bindings.Remove(anyEvent);
		}

		public Push Push(string @event, Dictionary<string, object> payload = null, TimeSpan? timeout = null) {

			var obj = JObject.FromObject(payload ?? new Dictionary<string, object>());
			return PushJson(@event, obj, timeout);
		}

		public Push PushJson(string @event, JObject obj, TimeSpan? timeout = null) {
			return Push(MakeMessage(@event, null, obj), timeout);
		}

		private Push Push(Message message, TimeSpan? timeout) {

			if (joinRef == null && message.@event != Message.OutBoundEvent.phx_join.ToString()) {
				throw new Exception(string.Format("tried to push '{0}' to '{1}' before joining", message.@event, topic));
			}

			return Push(new Push(message), timeout);
		}

		private Push Push(Push push, TimeSpan? timeout) {

			activePushes[push.message.@ref] = push;

			push.timerId = socket.opts.delayedExecutor.Execute(
				() => { 
					push.TriggerTimeout();
					CleanUp(push.message.@ref);
				}, timeout ?? socket.opts.timeout);

			if (!socket.Push(push.message)) {
				sendBuffer.Add(push.message);
			}

			return push;
		}

		/// To register a callback, use channel.On(Message.InboundEvent.phx_close)
		public void Leave(TimeSpan? timeout = null) {
			socket.Log(LogLevel.Debug, "channel", string.Format("leave {0}", topic));

			// cleanups
			activePushes.Values
				.ToList() // copy to avoid mutation while iterating
				.ForEach(push => CleanUp(push.message.@ref));


			// eagerly set state to closed to avoid reconnecting/triggering errors
			var oldState = state;
			state = State.Closed;

			if (oldState != State.Closed && oldState != State.Errored) {
				Push(MakeMessage(Message.OutBoundEvent.phx_leave), timeout);
			}

			Trigger(MakeMessage(Message.InBoundEvent.phx_close, joinRef));
		}

		#endregion


		#region private & internal methods

		// Return the next message ref, accounting for overflows
		private string MakeRef() {
			return (++refCount).ToString();
		}

		private Message MakeMessage(Enum @event, string @ref = null, JObject payload = null) {
			return MakeMessage(@event.ToString(), @ref, payload);
		}

		private Message MakeMessage(string @event, string @ref = null, JObject payload = null) {
			return new Message(topic, @event, @ref ?? MakeRef(), payload);
		}

		private void FlushSendBuffer() {
			sendBuffer = sendBuffer
				.Where(m => !socket.Push(m))
				.ToList();
		}

		private void CleanUp(string @ref) {

			if (activePushes.ContainsKey(@ref)) {
				var timerId = activePushes[@ref].timerId;
				socket.opts.delayedExecutor.Cancel(timerId);
			}

			if (@ref != joinRef) {
				activePushes.Remove(@ref);
			}
		}

		internal void SocketTerminated(string reason) {
			Trigger(MakeMessage(Message.InBoundEvent.phx_error, joinRef), reason);
		}

		private void OnJoinReply(Reply reply) {

			switch (reply.status) {
			case Reply.Status.Ok:
				socket.Log(LogLevel.Debug, "channel", string.Format("join ok {0}", topic));
				state = State.Joined;
				reconnectTimer = null;
				FlushSendBuffer();
				break;

			case Reply.Status.Error:
				TriggerError("join error");
				break;

			case Reply.Status.Timeout:
				if (state == State.Joining) {
					TriggerError("join timeout");
				}
				break;
			}
		}

		private void OnReply(Message msg) {

			var reply = ReplySerialization.Deserialize(msg.payload);
			var isJoinReply = msg.@ref == joinRef;

			if (isJoinReply) {
				OnJoinReply(reply);
			}

			if (activePushes.ContainsKey(msg.@ref)) {
				activePushes[msg.@ref].TriggerReplyCallback(reply);
				CleanUp(msg.@ref);
			}
		}

		private void TriggerClose() {
			socket.Log(LogLevel.Debug, "channel", string.Format("close {0}", topic));

			state = State.Closed;
			socket.Remove(this);
		}

		private void TriggerError(string reason) {

			if (state == State.Closed) {
				return;
			}

			socket.Log(LogLevel.Info, "channel", string.Format("{0}: {1}", topic, reason));
			state = State.Errored;

			var interval = socket.opts.channelRejoinInterval;
			if (reconnectTimer == null && interval.HasValue) {
				reconnectTimer = socket.opts.delayedExecutor.Execute(Rejoin, interval.Value);
			}
		}

		internal void Trigger(Message msg, string info = null) {

			var inboundEvent = MessageInBoundEventExtensions.Parse(msg.@event);

			bool isOldJoinEvent = 
				msg.@ref != null 
				&& msg.@ref != joinRef 
				&& inboundEvent != Message.InBoundEvent.phx_reply;

			if (isOldJoinEvent) {
				return;
			}

			switch (inboundEvent) {
			case Message.InBoundEvent.phx_reply:
				OnReply(msg);
				break;

			case Message.InBoundEvent.phx_close:
				TriggerClose();
				break;

			case Message.InBoundEvent.phx_error:
				TriggerError(info ?? "channel errored abnormally");
				break;

			case null:
				// custom, app-specific event
				socket.Log(LogLevel.Trace, "channel", string.Format("{0}: received {1}", topic, msg.@event));
				break;

			default:
				throw new ArgumentOutOfRangeException();
			}

			if (bindings.ContainsKey(msg.@event)) {
				bindings[msg.@event].Invoke(msg);
			}
		}

		#endregion
	}
}
