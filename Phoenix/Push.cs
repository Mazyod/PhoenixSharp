using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;


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
		private Message receivedResp = null;
		private DelayedExecution? delayedExecution = null;
		private readonly Dictionary<Message.Reply.Status, List<Action<Message>>> recHooks = new();
		private bool sent = false;

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
			if (HasReceived(Message.Reply.Status.timeout)) {
				return;
			}

			StartTimeout();
			sent = true;
			channel.socket.Push(new Message(
					topic: channel.topic,
					@event: @event,
					payload: payload != null ? payload() : null,
					@ref: @ref,
					joinRef: channel.joinRef
			));
		}

		public Push Receive(Message.Reply.Status status, Action<Message> callback) {
			if (HasReceived(status)) {
				callback(receivedResp);
			}

			var callbacks = recHooks.GetValueOrDefault(status) ?? (recHooks[status] = new());
			callbacks.Add(callback);

			return this;
		}

		internal void Reset() {
			CancelRefEvent();
			@ref = null;
			refEvent = null;
			receivedResp = null;
			sent = false;
		}

		private void MatchReceive(Message message) {
			channel.socket.Log(LogLevel.Debug, "channel", $"channel.matchReceive({message})");

			var reply = message.ParseReply();
			if (reply == null) {
				return;
			}

			channel.socket.Log(LogLevel.Debug, "channel", $"find hooks for status {reply.status}");
			recHooks
				.GetValueOrDefault(reply.replyStatus)?
				.ForEach(callback => callback(message));
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
			refEvent = channel.ReplyEventName(@ref);

			channel.On(refEvent, payload => {
				CancelRefEvent();
				CancelTimeout();
				receivedResp = payload;
				MatchReceive(payload);
			});

			delayedExecution = channel.socket.opts.delayedExecutor.Execute(() => {
				Trigger(Message.Reply.Status.timeout);
			}, timeout);
		}

		private bool HasReceived(Message.Reply.Status status) {
			return receivedResp?.ParseReply()?.replyStatus == status;
		}

		internal void Trigger(Message.Reply.Status status) {
			channel.Trigger(new Message(
				@event: refEvent,
				payload: new Dictionary<string, object> {
					{ "status", status.ToString() },
				}
			));
		}
	}
}
