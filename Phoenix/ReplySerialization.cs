using Newtonsoft.Json.Linq;


namespace Phoenix {
	public static class ReplySerialization {

		public static Reply Deserialize(JObject data) {
			return data.ToObject<Reply>();
		}
	}
}

