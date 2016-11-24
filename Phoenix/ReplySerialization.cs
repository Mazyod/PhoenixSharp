using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;


namespace Phoenix {
	public static class ReplySerialization {

		public static Reply Deserialize(Dictionary<string, object> data) {
			return new Reply() {
				status = ReplyStatusExtensions.FromString(data.ContainsKey("status") ? data["status"] as string : null),
				response = data.ContainsKey("response") ? data["response"] as Dictionary<string, object> : null
			};
		}
	}
}

