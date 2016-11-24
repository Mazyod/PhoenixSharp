using System;
using System.Collections.Generic;

namespace Phoenix {
	
	public class Push {

		internal Message message;
		private Channel channel;

		private Reply? reply = null;
		private Dictionary<Reply.Status, Action<Reply>> replyHooks = new Dictionary<Reply.Status, Action<Reply>>();
		private Timer timeoutTimer = null;


		public Push(Channel channel, Message message, TimeSpan timeout) {

			message.topic = channel.topic;
			message.@ref = null;

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

		public void Resend(TimeSpan timeout) {

			Abort();

			message.@ref = null;
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
			CancelRefEvent();
		}

		#region private

		private void InitializeTimeoutTimer(TimeSpan delay) {

			var timeoutReply = new Reply() { status = Reply.Status.Timeout };
			timeoutTimer = new Timer(() => TriggerReplyCallback(timeoutReply), delay);
		}

		private void TriggerReplyCallback(Dictionary<string, object> rawReply) {
			TriggerReplyCallback(ReplySerialization.Deserialize(rawReply));
		}

		internal void TriggerReplyCallback(Reply reply) {
			Console.WriteLine(string.Format("reply: {0}, {1}", reply.status.AsString(), message.@event));
			CancelRefEvent();
			timeoutTimer.Reset();

			this.reply = reply;

			var callback = replyHooks[reply.status];
			callback?.Invoke(reply);
		}

		private void CancelRefEvent() {
			if (message.@ref != null) {
				channel.Off(Reply.EventName(message.@ref));
			}
		}

		internal void StartTimeout() { 

			if (timeoutTimer.isActive) { 
				return;
			}

			message.@ref = channel.socket.MakeRef();
			channel.On(Reply.EventName(message.@ref), 
				msg => TriggerReplyCallback(msg.payload)
			);

			timeoutTimer.ScheduleTimeout();
		}

		#endregion
	}
}