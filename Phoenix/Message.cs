using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;


namespace Phoenix {
	
	public struct Message : IEquatable<Message> {

		public enum InBoundEvent {
			Reply,
			Close,
			Error,
		}

		public enum OutBoundEvent {
			Join,
			Leave,
		}

		public string topic;
		public JObject payload;

		public string @event;
		public string @ref;


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

			var controlEvents = new Dictionary<string, Message.InBoundEvent> {
				{ "phx_close", Message.InBoundEvent.Close },
				{ "phx_error", Message.InBoundEvent.Error },
				{ "phx_reply", Message.InBoundEvent.Reply },
			};

			if (controlEvents.ContainsKey(rawChannelEvent)) { 
				return controlEvents[rawChannelEvent];
			}

			return null;
		}

		public static string AsString(this Message.InBoundEvent channelEvent) {

			return new Dictionary<Message.InBoundEvent, string> {
				{ Message.InBoundEvent.Close, "phx_close" },
				{ Message.InBoundEvent.Error, "phx_error" },
				{ Message.InBoundEvent.Reply, "phx_reply" },
			}[channelEvent];
		}
	}

	public static class MessageOutBoundEventExtensions {

		public static Message.OutBoundEvent Parse(string rawChannelEvent) {

			return new Dictionary<string, Message.OutBoundEvent> {
				{ "phx_join", Message.OutBoundEvent.Join },
				{ "phx_leave", Message.OutBoundEvent.Leave },
			}[rawChannelEvent];
		}

		public static string AsString(this Message.OutBoundEvent channelEvent) {

			return new Dictionary<Message.OutBoundEvent, string> {
				{ Message.OutBoundEvent.Join, "phx_join" },
				{ Message.OutBoundEvent.Leave, "phx_leave" },
			}[channelEvent];
		}
	}
}