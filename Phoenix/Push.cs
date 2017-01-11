using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;


namespace Phoenix {
	
	public sealed class Push {

		#region properties

		public readonly Message message;

		internal uint timerId;
		internal Reply? reply = null;

		private readonly Dictionary<Reply.Status, Action<Reply>> replyHooks = new Dictionary<Reply.Status, Action<Reply>>();

		#endregion


		public Push(Message message) {
			this.message = message;
		}


		#region public methods

		public Push Receive(Reply.Status status, Action<Reply> callback) {
			
			if (reply.HasValue && reply.Value.status == status) {
				callback(reply.Value);
			}

			replyHooks[status] = callback;
			return this;
		}

		#endregion


		#region private & internal methods

		internal void TriggerTimeout() {
			TriggerReplyCallback(new Reply { status = Reply.Status.Timeout });
		}

		internal void TriggerReplyCallback(Reply reply) {

			if (this.reply.HasValue) {
				return;
			}

			this.reply = reply;

			if (replyHooks.ContainsKey(reply.status)) {
				replyHooks[reply.status].Invoke(reply);
			}
		}

		#endregion
	}
}
