using System;
using System.Collections.Generic;


namespace Phoenix {
	public struct Reply {

		#region nested types

		public enum Status {
			Ok,
			Error,
			Timeout,
		}

		#endregion

		#region static convenience

		public static string EventName(string refCount) {
			return string.Format("chan_reply_{0}", refCount);
		}

		#endregion

		#region properties

		public Status status;
		public Dictionary<string, object> payload;

		#endregion
	}

	public static class ReplyStatusExtensions {
		
		public static Reply.Status FromString(string rawStatus) {
			switch (rawStatus) {
			case "ok":
				return Reply.Status.Ok;
			case "error":
				return Reply.Status.Error;
			case "timeout":
				return Reply.Status.Timeout;
			default:
				throw new ArgumentOutOfRangeException("unexpected status");
			}
		}

		public static string AsString(this Reply.Status status) {

			switch (status) {
			case Reply.Status.Ok:
				return "ok";
			case Reply.Status.Error:
				return "error";
			case Reply.Status.Timeout:
				return "timeout";
			default:
				throw new ArgumentOutOfRangeException("unexpected value");
			}
		}
	}
}
