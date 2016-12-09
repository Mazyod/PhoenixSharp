using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;


namespace Phoenix {
	
	public sealed class Channel {

		#region nested types

		public enum State {
			Closed,
			Joining,
			Joined
		}

		#endregion


		#region properties

		private static uint refCount = 0;


		public Socket socket;
		public State state = State.Closed;
		public readonly string topic;

		private Dictionary<string, Action<Message>> bindings = new Dictionary<string, Action<Message>>();
		private Dictionary<string, Push> activePushes = new Dictionary<string, Phoenix.Push>();

		private Push joinPush;

		public bool canPush {
			get { return socket.state == Socket.State.Open && state == State.Joined; }
		}

		#endregion


		public Channel(string topic, Socket socket) {

			this.topic = topic;
			this.socket = socket;
		}


		#region public methods

		public Push Join(Dictionary<string, object> parameters = null, TimeSpan? timeout = null) {

			if (joinPush != null) {
				throw new Exception("tried to join multiple times. 'join' can only be called a single time per channel instance");
			}

			state = State.Joining;
			joinPush = Push(Message.OutBoundEvent.Join.AsString(), parameters, timeout);

			return joinPush;
		}


		public void On(Message.InBoundEvent @event, Action<Message> callback) {
			On(@event.AsString(), callback);
		}

		public void On(string anyEvent, Action<Message> callback) {
			bindings[anyEvent] = callback;
		}

		public void Off(string anyEvent) {
			bindings[anyEvent] = null;
		}

		public Push Push(string @event, Dictionary<string, object> payload = null, TimeSpan? timeout = null) {

			var msg = new Message() { @event = @event };

			if (payload != null) {
				msg.payload = JObject.FromObject(payload);
			}

			return Push(msg, timeout ?? socket.opts.timeout);
		}

		private Push Push(Message message, TimeSpan timeout) {

			if (joinPush == null && message.@event != Message.OutBoundEvent.Join.AsString()) {
				throw new Exception(string.Format("tried to push '{0}' to '{1}' before joining", message.@event, topic));
			}

			message.topic = topic;
			message.@ref = MakeRef();

			var pushEvent = new Push(message.@ref);
			activePushes[message.@ref] = pushEvent;

			pushEvent.timerId = socket.opts.delayedExecutor.Execute(
				() => { 
					pushEvent.TriggerTimeout();
					CleanUp(pushEvent.@ref);
				}, timeout);

			socket.Push(message);

			return pushEvent;
		}

		// Leaves the channel
		//
		// Unsubscribes from server events, and
		// instructs channel to terminate on server
		//
		// Triggers onClose() hooks
		public Push Leave(TimeSpan? timeout = null) {

			// cleanups
			foreach (var push in activePushes.Values) {
				CleanUp(push.@ref);
			}

			state = State.Closed;

			Action onClose = () => {
				socket.Log(LogLevel.Debug, "channel", string.Format("leave {0}", topic));
				Trigger(new Message() { @event = Message.InBoundEvent.Close.AsString() });
			};

			var leavePush = Push(Message.OutBoundEvent.Leave.AsString(), null, timeout)
				.Receive(Reply.Status.Ok, _ => onClose())
				.Receive(Reply.Status.Timeout, _ => onClose());

			return leavePush;
		}

		#endregion


		#region private & internal methods

		// Return the next message ref, accounting for overflows
		private string MakeRef() {
			return (++refCount).ToString();
		}

		private void CleanUp(string @ref) {

			if (activePushes.ContainsKey(@ref)) {
				var timerId = activePushes[@ref].timerId;
				socket.opts.delayedExecutor.Cancel(timerId);
			}

			activePushes.Remove(@ref);
		}

		private void OnJoinReply(Reply reply) {

			switch (reply.status) {
			case Reply.Status.Ok:
				socket.Log(LogLevel.Debug, "channel", string.Format("join ok {0}", topic));
				state = State.Joined;
				break;

			case Reply.Status.Error:
				socket.Log(LogLevel.Debug, "channel", string.Format("join error {0}", topic));
				state = Channel.State.Closed;
				break;

			case Reply.Status.Timeout:
				if (state == State.Joining) {
					socket.Log(LogLevel.Debug, "channel", string.Format("join timeout {0}", topic));
					state = State.Closed;
				}
				break;
			}
		}

		private void OnReply(Message msg) {

			var reply = ReplySerialization.Deserialize(msg.payload);
			if (msg.@ref == joinPush.@ref) {
				OnJoinReply(reply);
			}

			if (activePushes.ContainsKey(msg.@ref)) {
				activePushes[msg.@ref].TriggerReplyCallback(reply);
				CleanUp(msg.@ref);
			}
		}

		internal void TriggerClose() {
			socket.Log(LogLevel.Debug, "channel", string.Format("close {0}", topic));

			state = State.Closed;
			socket.Remove(this);
		}

		internal void TriggerError() {

			if (state == State.Closed) {
				return;
			}

			socket.Log(LogLevel.Info, "channel", string.Format("{0}: channel errored abnormally", topic));
			state = State.Closed;
		}

		internal void Trigger(Message msg) {

			var inboundEvent = MessageInBoundEventExtensions.Parse(msg.@event);
			if (msg.@ref != null && msg.@ref != joinPush.@ref && inboundEvent.HasValue && inboundEvent.Value != Message.InBoundEvent.Reply) {
				return;
			}

			switch (inboundEvent) {
			case Message.InBoundEvent.Reply:
				OnReply(msg);
				break;

			case Message.InBoundEvent.Close:
				TriggerClose();
				break;

			case Message.InBoundEvent.Error:
				TriggerError();
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
