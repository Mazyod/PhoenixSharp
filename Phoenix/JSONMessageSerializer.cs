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

		public Reply? MapReply(object payload) {
			var jObject = JObject.FromObject(payload);
			return new Reply(
				status: jObject.Value<string>("status"),
				response: jObject["response"]
			);
		}

		public T MapPayload<T>(object payload) {
			return payload == null
				? default
				: JToken.FromObject(payload).ToObject<T>();
		}
	}

	public static class JSONPayloadExtensions {

		public static T JSONResponse<T>(this Reply reply) {
			return ((JToken)reply.response).ToObject<T>();
		}

		public static T JSONPayload<T>(this Message message) {
			return ((JToken)message.payload).ToObject<T>();
		}
	}
}
