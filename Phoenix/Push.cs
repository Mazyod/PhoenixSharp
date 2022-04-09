using System;
using System.Collections.Generic;

using StatusHookTable = System.Collections.Generic.Dictionary<
	Phoenix.Reply.Status,
	System.Collections.Generic.List<System.Action<Phoenix.Reply>>>;

namespace Phoenix {

	public sealed class Push {

		#region properties

		private readonly Channel channel;
		private readonly string @event;
		private readonly Func<Dictionary<string, object>> payload;
		private TimeSpan timeout;

		// internal state
		internal string @ref = null;
		private string refEvent = null;
		private Reply? receivedResp = null;
		private DelayedExecution? delayedExecution = null;
		private readonly StatusHookTable recHooks = new StatusHookTable();
		//private bool sent = false;

		internal uint timerId;

		#endregion


		// define a constructor that takes a channel, event, payload, and timeout
		public Push(Channel channel, string @event, Func<Dictionary<string, object>> payload, TimeSpan timeout) {
			this.channel = channel;
			this.@event = @event;
			this.payload = payload;
			this.timeout = timeout;
		}

		public void Resend(TimeSpan timeout) {
			this.timeout = timeout;
			Reset();
			Send();
		}

		public void Send() {
			if (HasReceived(Reply.Status.Timeout)) {
				return;
			}

			StartTimeout();
			// sent = true;
			channel.socket.Push(new Message(
					topic: channel.topic,
					@event: @event,
					payload: payload != null ? payload() : null,
					@ref: @ref,
					joinRef: channel.joinRef
			));
		}

		public Push Receive(Reply.Status status, Action<Reply> callback) {
			if (HasReceived(status)) {
				callback(receivedResp.Value);
			}

			var callbacks = recHooks.GetValueOrDefault(status) ?? (
				recHooks[status] = new List<Action<Reply>>()
			);
			callbacks.Add(callback);

			return this;
		}

		internal void Reset() {
			CancelRefEvent();
			@ref = null;
			refEvent = null;
			receivedResp = null;
			// sent = false;
		}

		private void MatchReceive(Reply? reply) {

			if (!reply.HasValue) {
				return;
			}

			recHooks
				.GetValueOrDefault(reply.Value.replyStatus)?
				.ForEach(callback => callback(reply.Value));
		}

		private void CancelRefEvent() {
			if (refEvent != null) {
				channel.Off(refEvent);
			}
		}

		internal void CancelTimeout() {
			delayedExecution?.Cancel();
			delayedExecution = null;
		}

		internal void StartTimeout() {
			// PhoenixJS: null check implicit
			CancelTimeout();

			@ref = channel.socket.MakeRef();
			refEvent = Channel.ReplyEventName(@ref);

			var serializer = channel.socket.opts.messageSerializer;
			channel.On(refEvent, message => {
				CancelRefEvent();
				CancelTimeout();
				receivedResp = serializer.MapReply(message.payload);
				MatchReceive(receivedResp);
			});

			delayedExecution = channel.socket.opts.delayedExecutor.Execute(() => {
				Trigger(Reply.Status.Timeout);
			}, timeout);
		}

		private bool HasReceived(Reply.Status status) {
			return receivedResp?.replyStatus == status;
		}

		internal void Trigger(Reply.Status status) {
			channel.Trigger(new Message(
				@event: refEvent,
				payload: new Dictionary<string, object> {
					{ "status", status.ToString().ToLower() },
				}
			));
		}
	}
}
