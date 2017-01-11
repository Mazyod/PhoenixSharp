using Newtonsoft.Json.Linq;


namespace Phoenix {

	public static class MessageSerialization {

		public static string Serialize(this Message message) {

			return JObject
				.FromObject(message)
				.ToString(Newtonsoft.Json.Formatting.None);
		}

		public static Message Deserialize(string data) {
			return JObject
				.Parse(data)
				.ToObject<Message>();
		}
	}
}

