using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;


namespace Phoenix {
	
	public class Push {

		internal Message message;
		private Channel channel;
		private Dictionary<Reply.Status, Action<Reply>> replyHooks = new Dictionary<Reply.Status, Action<Reply>>();

		private Reply? reply = null;
		private Timer timeoutTimer = null;


		public Push(Channel channel, Message message, TimeSpan timeout) {

			message.topic = channel.topic;
			message.@ref = channel.socket.MakeRef();

			this.channel = channel;
			this.message = message;

			InitializeTimeoutTimer(timeout);
		}

		internal void Send() {
			
			if (reply.HasValue && reply.Value.status == Reply.Status.Timeout) {
				return; 
			}

			StartTimeout();

			channel.socket.Push(message);
		}

		internal void Resend(TimeSpan timeout) {

			Abort();

			message.@ref = channel.socket.MakeRef();
			reply = null;

			InitializeTimeoutTimer(timeout);
			Send();
		}

		public Push Receive(Reply.Status status, Action<Reply> callback) {
			
			if (reply.HasValue && reply.Value.status == status) {
				callback(reply.Value);
			}

			replyHooks[status] = callback;
			return this;
		}

		public void Abort() {
			timeoutTimer.Reset();
			channel.Clear(this);
		}

		#region private

		private void InitializeTimeoutTimer(TimeSpan delay) {

			var timeoutReply = new Reply() { status = Reply.Status.Timeout };
			timeoutTimer = new Timer(() => TriggerReplyCallback(timeoutReply), delay);
		}

		internal void TriggerReplyCallback(JObject rawReply) {
			TriggerReplyCallback(ReplySerialization.Deserialize(rawReply));
		}

		internal void TriggerReplyCallback(Reply reply) {

			Abort();

			this.reply = reply;

			if (replyHooks.ContainsKey(reply.status)) {
				replyHooks[reply.status].Invoke(reply);
			}
		}

		internal void StartTimeout() { 
			if (!timeoutTimer.isActive) { 
				timeoutTimer.ScheduleTimeout();
				channel.Register(this);
			}
		}

		#endregion
	}
}