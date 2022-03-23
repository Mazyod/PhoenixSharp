using Newtonsoft.Json.Linq;


namespace Phoenix {

	public sealed class JSONMessageSerializer : IMessageSerializer {
		public string Serialize(Message message) {
			return JObject
				.FromObject(message)
				.ToString(
					Newtonsoft.Json.Formatting.None,
					new Newtonsoft.Json.Converters.StringEnumConverter()
				);
		}

		public Message Deserialize(string message) {
			System.Console.WriteLine("===> Receive: {0}", message.Trim('\u0000'));

			return JObject
				.Parse(message)
				.ToObject<Message>();
		}
	}
}

