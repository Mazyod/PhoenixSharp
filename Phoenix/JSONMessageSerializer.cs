using System;
using Newtonsoft.Json.Linq;


namespace Phoenix {

	public sealed class JSONMessageSerializer : IMessageSerializer {

		public string Serialize(Message message) {

			return new JArray(
					message.joinRef,
					message.@ref,
					message.topic,
					message.@event,
					message.payload == null ? null : JObject.FromObject(message.payload)
				)
				.ToString(
					Newtonsoft.Json.Formatting.None,
					new Newtonsoft.Json.Converters.StringEnumConverter()
				);
		}

		public Message Deserialize(string message) {

			var array = JArray.Parse(message);
			return new Message(
				joinRef: array[0].ToObject<string>(),
				@ref: array[1].ToObject<string>(),
				topic: array[2].ToObject<string>(),
				@event: array[3].ToObject<string>(),
				payload: array[4]
			);
		}

		public T MapPayload<T>(object payload) {
			return payload == null ? default : JObject.FromObject(payload).ToObject<T>();
		}
	}

	public static class JSONMessageSerializerExtensions {

		public static Action<Message> MapPayload<T>(this Action<T> callback) {
			return message => callback(
				message.payload == null
					? default
					: message.Payload<JObject>().ToObject<T>()
			);
		}
	}
}

