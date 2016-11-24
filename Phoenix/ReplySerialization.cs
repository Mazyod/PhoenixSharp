using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;


namespace Phoenix {
	public static class ReplySerialization {

		public static string Serialize(this Reply reply) {
			return JObject.FromObject(reply).ToString(Newtonsoft.Json.Formatting.None);
		}

		public static Reply Deserialize(string data) {
			return JObject
				.Parse(data)
				.ToObject<Reply>();
		}

		public static Reply Deserialize(Dictionary<string, object> data) {

			return new Reply() {
				status = ReplyStatusExtensions.FromString(data.ContainsKey("status") ? data["status"] as string : null),
				payload = data.ContainsKey("payload") ? data["payload"] as Dictionary<string, object> : null
			};
		}
	}
}

