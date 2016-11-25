using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;


namespace Phoenix {
	public static class ReplySerialization {

		public static Reply Deserialize(JObject data) {
			return data.ToObject<Reply>();
		}
	}
}

