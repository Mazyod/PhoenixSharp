using System;
using Newtonsoft.Json.Linq;


namespace Phoenix {

	public struct Reply: IEquatable<Reply> {

		#region nested types

		public enum Status {
			Ok,
			Error,
			Timeout,
		}

		#endregion


		#region properties

		public Status status;
		public JObject response;

		#endregion

		#region IEquatable methods

		public override int GetHashCode() {
			return status.GetHashCode() 
				+ (response == null ? 0 : response.GetHashCode());
		}

		public override bool Equals(object obj) {
			return obj is Reply && Equals((Reply)obj);
		}

		public bool Equals(Reply that) {
			return this.status == that.status
				/* Collection comparison is tricky */
				;
		}

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
