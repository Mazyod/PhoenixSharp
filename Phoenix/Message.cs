using System;


namespace Phoenix {

	public interface IMessageSerializer {
		string Serialize(Message message);
		Message Deserialize(string message);

		Reply? MapReply(object payload);
		T MapPayload<T>(object payload);
	}

	#region payloads

	/** 
		* A reply payload, in response to a push.
		*/
	public struct Reply {

		public enum Status {
			Ok,
			Error,
			Timeout,

			// extension methods also implemented below
		}

		// PhoenixJS maps incoming phx_reply to chan_reply_{ref} when broadcasting the event
		public static readonly string replyEventPrefix = "chan_reply_";

		public readonly string status;
		public readonly object response;

		[System.Runtime.Serialization.IgnoreDataMember]
		public Status replyStatus {
			get {
				if (status == null) {
					// shouldn't happen
					return Status.Error;
				}

				return status switch {
					"ok" => Status.Ok,
					"error" => Status.Error,
					"timeout" => Status.Timeout,
					_ => throw new ArgumentException("Unknown status: " + status),
				};
			}

		}

		public Reply(string status, object response) {
			this.status = status;
			this.response = response;
		}
	}

	#endregion

	public struct Message {

		#region nested types

		public enum InBoundEvent {
			Reply,
			Close,
			Error,

			// extension methods defined below
		}

		public enum OutBoundEvent {
			Join,
			Leave,

			// extension methods defined below
		}

		#endregion

		public readonly string topic;
		// unfortunate mutation of the original message
		public string @event;
		public readonly string @ref;
		public object payload;
		public string joinRef;

		public Message(
			string topic = null,
			string @event = null,
			object payload = null,
			string @ref = null,
			string joinRef = null
		) {
			this.topic = topic;
			this.@event = @event;
			this.payload = payload;
			this.@ref = @ref;
			this.joinRef = joinRef;
		}
	}

	public static class StatusExtensions {

		/** 
		 * Serialized value of the enum.
		 * Is apparently much more performant than ToString.
		 */
		public static string Serialized(this Reply.Status status) {
			return status switch {
				Reply.Status.Ok => "ok",
				Reply.Status.Error => "error",
				Reply.Status.Timeout => "timeout",
				_ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
			};
		}
	}

	public static class MessageInBoundEventExtensions {

		/** 
		 * Serialized value of the enum.
		 * Is apparently much more performant than ToString.
		 */
		public static string Serialized(this Message.InBoundEvent @event) {
			return @event switch {
				Message.InBoundEvent.Reply => "phx_reply",
				Message.InBoundEvent.Close => "phx_close",
				Message.InBoundEvent.Error => "phx_error",
				_ => throw new ArgumentOutOfRangeException(nameof(@event), @event, null)
			};
		}
	}

	public static class MessageOutBoundEventExtensions {

		/** 
		 * Serialized value of the enum.
		 * Is apparently much more performant than ToString.
		 */
		public static string Serialized(this Message.OutBoundEvent @event) {
			return @event switch {
				Message.OutBoundEvent.Join => "phx_join",
				Message.OutBoundEvent.Leave => "phx_leave",
				_ => throw new ArgumentOutOfRangeException(nameof(@event), @event, null)
			};
		}
	}
}