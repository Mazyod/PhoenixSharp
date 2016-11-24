using System;
using System.Collections.Generic;
using System.Linq;


namespace Phoenix {
	// ## Channels
	//
	// Channels are isolated, concurrent processes on the server that
	// subscribe to topics and broker events between the client and server.
	// To join a channel, you must provide the topic, and channel params for
	// authorization. Here's an example chat room example where `"new_msg"`
	// events are listened for, messages are pushed to the server, and
	// the channel is joined with ok/error/timeout matches:
	//
	//     let channel = socket.channel("room:123", {token: roomToken})
	//     channel.on("new_msg", msg => console.log("Got message", msg) )
	//     $input.onEnter( e => {
	//       channel.push("new_msg", {body: e.target.val}, 10000)
	//        .receive("ok", (msg) => console.log("created message", msg) )
	//        .receive("error", (reasons) => console.log("create failed", reasons) )
	//        .receive("timeout", () => console.log("Networking issue...") )
	//     })
	//     channel.join()
	//       .receive("ok", ({messages}) => console.log("catching up", messages) )
	//       .receive("error", ({reason}) => console.log("failed join", reason) )
	//       .receive("timeout", () => console.log("Networking issue. Still waiting...") )
	//
	//
	// ## Joining
	//
	// Creating a channel with `socket.channel(topic, params)`, binds the params to
	// `channel.params`, which are sent up on `channel.join()`.
	// Subsequent rejoins will send up the modified params for
	// updating authorization params, or passing up last_message_id information.
	// Successful joins receive an "ok" status, while unsuccessful joins
	// receive "error".
	//
	// ## Duplicate Join Subscriptions
	//
	// While the client may join any number of topics on any number of channels,
	// the client may only hold a single subscription for each unique topic at any
	// given time. When attempting to create a duplicate subscription,
	// the server will close the existing channel, log a warning, and
	// spawn a new channel for the topic. The client will have their
	// `channel.onClose` callbacks fired for the existing channel, and the new
	// channel join will have its receive hooks processed as normal.
	//
	// ## Pushing Messages
	//
	// From the previous example, we can see that pushing messages to the server
	// can be done with `channel.push(eventName, payload)` and we can optionally
	// receive responses from the push. Additionally, we can use
	// `receive("timeout", callback)` to abort waiting for our other `receive` hooks
	//  and take action after some period of waiting. The default timeout is 5000ms.
	//
	// ## Channel Hooks
	//
	// For each joined channel, you can bind to `onError` and `onClose` events
	// to monitor the channel lifecycle, ie:
	//
	//     channel.onError( () => console.log("there was an error!") )
	//     channel.onClose( () => console.log("the channel has gone away gracefully") )
	//
	public class Channel {

		#region nested types

		public enum State {
			Closed,
			Errored,
			Joining,
			Leaving,
			Joined
		}

		#endregion

		#region properties

		public Socket socket;
		public State state = State.Closed;
		public string topic;

		private Dictionary<string,Action<Message>> bindings = new Dictionary<string, Action<Message>>();
		private List<Push> pushBuffer = new List<Push>();
		private TimeSpan timeout;
		private bool joinedOnce = false;
		private Push joinPush;
		private Timer rejoinTimer;

		#endregion


		public Channel(string topic, Dictionary<string, object> parameters, Socket socket) {

			this.topic = topic;
			this.socket = socket;

			timeout = socket.opts.timeout;

			rejoinTimer = new Timer(
				() => RejoinUntilConnected(),
				socket.opts.reconnectAfter
			);

			var joinMessage = new Message() {
				@event = Message.OutBoundEvent.Join.AsString(),
				payload = parameters
			};

			joinPush = new Push(this, joinMessage, timeout)
				.Receive(Reply.Status.Ok, (p) => {
					state = State.Joined;
					rejoinTimer.Reset();
					pushBuffer.ForEach(pushEvent => pushEvent.Send());
					pushBuffer.Clear();
				})
				.Receive(Reply.Status.Timeout, (p) => {
					if (state == State.Joining) {
						// this.socket.log("channel", `timeout ${this.topic}`, this.joinPush.timeout)
						state = State.Errored;
						rejoinTimer.ScheduleTimeout();
					}
				});			
		}

		public void RejoinUntilConnected() {

			rejoinTimer.ScheduleTimeout();

			if (socket.state == Socket.State.Open) {
				Rejoin();
			}
		}

		public Push Join(TimeSpan? opTimeout = null) {
			
			if (joinedOnce) {
				throw new Exception("tried to join multiple times. 'join' can only be called a single time per channel instance");
			}
	    
			var timeout = opTimeout ?? this.timeout;
			joinedOnce = true;
			Rejoin(timeout);

			return joinPush;
		}


		public void On(string anyEvent, Action<Message> callback) {
			bindings[anyEvent] = callback;
		}

		public void Off(string anyEvent) {
			bindings[anyEvent] = null;
		}

		public bool CanPush() {
			return socket.state == Socket.State.Open && state == State.Joined;
		}

		public Push Push(Message message, TimeSpan? timeout = null) {
			if (!joinedOnce) {
				throw new Exception(string.Format("tried to push '{0}' to '{1}' before joining. Use channel.join() before pushing events", message.@event, topic));
			}

			var pushEvent = new Push(this, message, timeout ?? this.timeout);

			if (CanPush()) {
				pushEvent.Send();
			} else {
				Console.WriteLine("Can't push?!");
				pushEvent.StartTimeout();
				pushBuffer.Add(pushEvent);
			}

			return pushEvent;
		}

		// Leaves the channel
		//
		// Unsubscribes from server events, and
		// instructs channel to terminate on server
		//
		// Triggers onClose() hooks
		//
		// To receive leave acknowledgements, use the a `receive`
		// hook to bind to the server ack, ie:
		//
		//     channel.leave().receive("ok", () => alert("left!") )
		//
		public Push Leave(TimeSpan? timeout = null) {

			// cleanups
			rejoinTimer.Reset();
			joinPush.Abort();

			pushBuffer.ForEach(p => p.Abort());
			pushBuffer.Clear();

			state = State.Leaving;

			Action onClose = () => {
				// socket.Log(string.Format("channel", "leave {0}", topic));
				Trigger(new Message() { @event = Message.InBoundEvent.Close.AsString() });
			};

			var leaveMessage = new Message() {
				@event = Message.OutBoundEvent.Leave.AsString()
			};

			var leavePush = new Push(this, leaveMessage, timeout ?? this.timeout)
				.Receive(Reply.Status.Ok, _ => onClose())
				.Receive(Reply.Status.Timeout, _ => onClose());

			if (CanPush()) {
				leavePush.Send();
			} else {
				// short-circuit a success callback
				leavePush.TriggerReplyCallback(new Reply() { status = Reply.Status.Ok });
			}

			return leavePush;
		}

		// private

		private void SendJoin(TimeSpan timeout) {
			state = State.Joining;
			joinPush.Resend(timeout);
		}

		private void Rejoin(TimeSpan? timeout = null) {
			if (state == State.Leaving) { 
				return; 
			}
			SendJoin(timeout ?? this.timeout);
		}

		internal void Trigger(Message msg) {

			var inboundEvent = MessageInBoundEventExtensions.Parse(msg.@event);
			if (msg.@ref != null && msg.@ref != joinPush.message.@ref && inboundEvent != Message.InBoundEvent.Reply) {
				return;
			}

			switch (inboundEvent) {
			case Message.InBoundEvent.Reply:
				var replyEvent = Reply.EventName(msg.@ref);
				if (bindings.ContainsKey(replyEvent)) {
					bindings[replyEvent].Invoke(msg);
				}
				break;

			case Message.InBoundEvent.Close:

				rejoinTimer.Reset();
				// socket.log("channel", `close ${this.topic} ${this.joinPush.ref()}`)
				state = State.Closed;
				socket.Remove(this);
				break;

			case Message.InBoundEvent.Error:
				if (state != State.Leaving && state != State.Closed) {
					// this.socket.log("channel", `error ${this.topic}`, reason)
					state = State.Errored;
					rejoinTimer.ScheduleTimeout();
				}
				break;

			case null:
				if (bindings.ContainsKey(msg.@event)) {
					bindings[msg.@event].Invoke(msg);
				}
				break;

			default:
				throw new ArgumentOutOfRangeException();
			}
		}
	}
}