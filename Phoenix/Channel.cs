using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

using ParamsType = System.Collections.Generic.Dictionary<string, object>;

namespace Phoenix {

	public class Channel {

		#region nested types

		public enum State {
			Closed,
			Joining,
			Joined,
			Leaving,
			Errored, // errored channels are rejoined automatically
		}

		public struct Subscription {
			public string @event;
			public Action<Message> callback;
		}

		#endregion


		#region properties

		public State state = State.Closed;
		public readonly string topic;
		public readonly Socket socket;

		private readonly Dictionary<string, List<Subscription>> bindings = new();
		private TimeSpan timeout;
		private bool joinedOnce = false;
		private readonly Push joinPush;
		private readonly List<Push> pushBuffer = new();

		/** 
		 *	See the stateChangeRefs comment in Socket.cs
		 */
		// internal List<object> stateChangeRefs = new();

		private readonly Scheduler rejoinTimer;
		internal string joinRef {
			get {
				return joinPush.@ref;
			}
		}

		#endregion


		// TODO: possibly support lazy instantiation of payload (same as Phoenix js)
		public Channel(string topic, ParamsType @params, Socket socket) {

			this.topic = topic;
			this.socket = socket;

			timeout = socket.opts.timeout;
			joinPush = new Push(
				this,
				Message.OutBoundEvent.phx_join.ToString(),
				() => @params,
				timeout
			);

			rejoinTimer = new Scheduler(
					() => { if (socket.IsConnected()) Rejoin(); },
					socket.opts.rejoinAfter,
					socket.opts.delayedExecutor
			);

			socket.OnError += SocketOnError;
			socket.OnOpen += SocketOnOpen;

			joinPush.Receive(Reply.Status.ok, message => {
				state = State.Joined;
				rejoinTimer.Reset();
				pushBuffer.ForEach(push => push.Send());
				pushBuffer.Clear();
			});

			joinPush.Receive(Reply.Status.error, message => {
				state = State.Errored;
				if (socket.IsConnected()) {
					rejoinTimer.ScheduleTimeout();
				}
			});

			OnClose(message => {
				rejoinTimer.Reset();
				if (socket.HasLogger()) {
					socket.Log(LogLevel.Debug, "channel", $"close {topic}");
				}
				state = State.Closed;
				// PhoenixJS: See note in socket regarding this
				// basically, we unregister delegates directly in c# instead of offing an array
				// this.off(channel.stateChangeRefs)
				socket.OnError -= SocketOnError;
				socket.OnOpen -= SocketOnOpen;
				socket.Remove(this);
			});

			OnError(message => {
				if (socket.HasLogger()) {
					socket.Log(LogLevel.Debug, "channel", $"error {topic}");
				}
				if (IsJoining()) {
					joinPush.Reset();
				}
				state = State.Errored;
				if (socket.IsConnected()) {
					rejoinTimer.ScheduleTimeout();
				}
			});

			joinPush.Receive(Reply.Status.timeout, message => {
				if (socket.HasLogger()) {
					socket.Log(LogLevel.Debug, "channel", $"timeout {topic} ({joinRef})");
				}

				var leaveEvent = Message.OutBoundEvent.phx_leave.ToString();
				var leavePush = new Push(this, leaveEvent, null, timeout);
				leavePush.Send();

				state = State.Errored;
				joinPush.Reset();

				if (socket.IsConnected()) {
					rejoinTimer.ScheduleTimeout();
				}
			});

			// on phx_reply, also trigger a message for the push using replyEventName
			On(Message.InBoundEvent.phx_reply.ToString(), message => {
				Trigger(new Message(
						topic: topic,
						@event: ReplyEventName(message.@ref),
						payload: message.payload,
						@ref: message.@ref,
						joinRef: message.joinRef
				));
			});
		}


		public Push Join(TimeSpan? timeout = null) {
			if (joinedOnce) {
				throw new Exception("tried to join multiple times. 'join' can only be called a single time per channel instance");
			}

			this.timeout = timeout ?? this.timeout;
			joinedOnce = true;
			Rejoin();
			return joinPush;
		}

		public void OnClose(Action<Message> callback) {
			On(Message.InBoundEvent.phx_close, callback);
		}

		public void OnError(Action<Message> callback) {
			On(Message.InBoundEvent.phx_error, callback);
		}

		public void On(Message.InBoundEvent @event, Action<Message> callback) {
			On(@event.ToString(), callback);
		}

		public Subscription On(string anyEvent, Action<Message> callback) {
			var subscription = new Subscription() {
				@event = anyEvent,
				callback = callback
			};

			var subscriptions = bindings.GetValueOrDefault(anyEvent) ?? (bindings[anyEvent] = new());
			subscriptions.Add(subscription);

			return subscription;
		}

		public bool Off(Subscription subscription) {
			return bindings
					.GetValueOrDefault(subscription.@event)?
					.Remove(subscription) ?? false;
		}

		public bool Off(Enum eventEnum) {
			return Off(eventEnum.ToString());
		}

		public bool Off(string anyEvent) {
			return bindings.Remove(anyEvent);
		}

		internal bool CanPush() {
			return socket.IsConnected() && IsJoined();
		}

		public Push Push(string @event, ParamsType payload = null, TimeSpan? timeout = null) {
			if (!joinedOnce) {
				throw new Exception($"tried to push '{@event}' to '{topic}' before joining. Use channel.join() before pushing events");
			}

			var pushEvent = new Push(this, @event, () => payload, timeout ?? this.timeout);
			if (CanPush()) {
				pushEvent.Send();
			} else {
				pushEvent.StartTimeout();
				pushBuffer.Add(pushEvent);
			}

			return pushEvent;
		}

		public Push Leave(TimeSpan? timeout = null) {
			rejoinTimer.Reset();
			joinPush.CancelTimeout();

			state = State.Leaving;

			void onClose() {
				if (socket.HasLogger()) {
					socket.Log(LogLevel.Debug, "channel", $"leave {topic}");
				}

				Trigger(new Message(
					@event: Message.InBoundEvent.phx_close.ToString()
				));
			}

			var leaveEvent = Message.OutBoundEvent.phx_leave.ToString();
			var leavePush = new Push(this, leaveEvent, null, timeout ?? this.timeout);
			leavePush
					.Receive(Reply.Status.ok, (_) => onClose())
					.Receive(Reply.Status.timeout, (_) => onClose());
			leavePush.Send();

			if (!CanPush()) {
				leavePush.Trigger(Reply.Status.ok);
			}
			return leavePush;
		}

		// overrideable message hook
		public virtual ParamsType OnMessage(Message message) {
			return message.payload;
		}

		internal bool IsMember(Message message) {
			if (topic != message.topic) {
				return false;
			}

			if (message.joinRef != null && message.joinRef != joinRef) {
				if (socket.HasLogger()) {
					socket.Log(
							LogLevel.Info,
							"Channel",
							$"dropping outdated message for topic '{topic}' (joinRef {message.joinRef} does not match joinRef {joinRef})"
					);
				}
				return false;
			} else {
				return true;
			}
		}

		private void Rejoin(TimeSpan? timeout = null) {
			if (IsLeaving()) {
				return;
			}

			socket.LeaveOpenTopic(topic);
			state = State.Joining;
			joinPush.Resend(timeout ?? this.timeout);
		}

		internal void Trigger(Message message) {
			var handledPayload = OnMessage(message);
			if (message.payload != null && handledPayload == null) {
				throw new Exception($"channel onMessage callbacks must return payload, modified or unmodified");
			}

			var eventBindings = bindings.GetValueOrDefault(message.@event);

			eventBindings?.ForEach(subscription => {
				subscription.callback(new Message(
						payload: handledPayload,
						@ref: message.@ref,
						joinRef: message.joinRef ?? joinRef
				));
			});
		}

		internal static string ReplyEventName(string @ref) {
			return $"{Reply.replyEventPrefix}{@ref}";
		}

		internal bool IsClosed() => state == State.Closed;
		internal bool IsErrored() => state == State.Errored;
		internal bool IsJoined() => state == State.Joined;
		internal bool IsJoining() => state == State.Joining;
		internal bool IsLeaving() => state == State.Leaving;

		#region Socket Events

		private void SocketOnError(string message) {
			rejoinTimer.Reset();
		}

		private void SocketOnOpen() {
			rejoinTimer.Reset();
			if (IsErrored()) {
				Rejoin();
			}
		}

		#endregion
	}
}
