using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;


namespace Phoenix {
	
	public class Message : IEquatable<Message> {

		public enum InBoundEvent {
			phx_reply,
			phx_close,
			phx_error,
		}

		public enum OutBoundEvent {
			phx_join,
			phx_leave,
		}

		public readonly string topic;
		public readonly string @event;
		public readonly string @ref;
		public readonly JObject payload;


		public Message(string topic, string @event, string @ref, JObject payload) {

			this.topic = topic;
			this.@event = @event;
			this.@ref = @ref;
			this.payload = payload ?? new JObject();
		}

		public override string ToString() {
			return string.Format("[{0}] {1}: {2}", @ref, topic, @event);
		}

		#region IEquatable methods

		public override int GetHashCode() {
			return topic.GetHashCode() + @event.GetHashCode() + @ref.GetHashCode();
		}

		public override bool Equals(object obj) {
			return (obj is Message) && Equals((Message)obj);
		}

		public bool Equals(Message that) {
			return this.topic == that.topic
				&& this.@event == that.@event
				&& this.@ref == that.@ref
				/* dictionary equality is hard */
				;
		}

		#endregion
	}


	public static class MessageInBoundEventExtensions {

		public static Message.InBoundEvent? Parse(string rawChannelEvent) {

			foreach (Message.InBoundEvent inboundEvent in Enum.GetValues(typeof(Message.InBoundEvent))) {
				if (inboundEvent.ToString() == rawChannelEvent) {
					return inboundEvent;
				}
			}

			return null;
		}
	}

	public static class MessageOutBoundEventExtensions {

		public static Message.OutBoundEvent Parse(string rawChannelEvent) {

			foreach (Message.OutBoundEvent outboundEvent in Enum.GetValues(typeof(Message.OutBoundEvent))) {
				if (outboundEvent.ToString() == rawChannelEvent) {
					return outboundEvent;
				}
			}

			throw new ArgumentOutOfRangeException(rawChannelEvent);
		}
	}
}