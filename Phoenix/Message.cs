using System;
using System.Collections.Generic;


namespace Phoenix {

	public interface IMessageSerializer {
		string Serialize(Message message);
		Message Deserialize(string message);

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
		}

		// PhoenixJS maps incoming phx_reply to chan_reply_{ref} when broadcasting the event
		public static readonly string replyEventPrefix = "chan_reply_";

		public readonly string status;
		public readonly Dictionary<string, object> response;

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

		public Reply(string status, Dictionary<string, object> response) {
			this.status = status;
			this.response = response;
		}
	}

	#endregion

	public struct Message {

		#region nested types

		public enum InBoundEvent {
			phx_reply,
			phx_close,
			phx_error,
		}

		public enum OutBoundEvent {
			phx_join,
			phx_leave,
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

		public T Payload<T>() {
			return (T)payload;
		}
	}
}