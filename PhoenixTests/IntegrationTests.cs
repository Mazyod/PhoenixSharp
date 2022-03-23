using System;
using System.Collections.Generic;
using Phoenix;
using NUnit.Framework;
using System.Net;
using Newtonsoft.Json.Linq;
using WebSocketSharp;


namespace PhoenixTests {

	public sealed class BasicLogger : ILogger {

		#region ILogger implementation

		public void Log(Phoenix.LogLevel level, string source, string message) {
			Console.WriteLine("[{0}]: {1} - {2}", level, source, message);
		}

		#endregion
	}


	[TestFixture()]
	public class IntegrationTests {

		private const int networkDelay = 5_000 /* ms */;
		private const string host = "phoenix-integration-tester.herokuapp.com";

		[Test()]
		public void GeneralIntegrationTest() {

			/// 
			/// setup
			/// 
			var address = string.Format("http://{0}/api/health-check", host);

			// heroku health check
			using (WebClient client = new WebClient()) {
				client.Headers.Add("Content-Type", "application/json");
				client.DownloadString(address);
			}

			var onOpenCount = 0;
			Socket.OnOpenDelegate onOpenCallback = () => onOpenCount++;

			List<Message> onMessageData = new();
			Socket.OnMessageDelegate onMessageCallback = m => onMessageData.Add(m);

			// connecting is synchronous as implemented above
			var socketAddress = string.Format("ws://{0}/socket", host);
			var socketFactory = new WebsocketSharpFactory();
			var socket = new Socket(socketAddress, null, socketFactory, new Socket.Options {
				reconnectAfter = _ => TimeSpan.FromMilliseconds(200),
				logger = new BasicLogger()
			});

			socket.OnOpen += onOpenCallback;
			socket.OnMessage += onMessageCallback;

			socket.Connect();
			Assert.IsTrue(socket.state == WebsocketState.Open);
			Assert.AreEqual(1, onOpenCount);

			/// 
			/// test channel error on join
			/// 
			Message okReply = null;
			Message errorReply = null;
			bool closeCalled = false;

			var errorChannel = socket.Channel("tester:phoenix-sharp");
			errorChannel.On(Message.InBoundEvent.phx_close, _ => closeCalled = true);

			errorChannel.Join()
				.Receive(Message.Reply.Status.ok, r => okReply = r)
				.Receive(Message.Reply.Status.error, r => errorReply = r);

			Assert.That(() => errorReply != null, Is.True.After(networkDelay, 10));
			Assert.IsNull(okReply);
			Assert.AreEqual(Channel.State.Errored, errorChannel.state);
			// call leave explicitly to cleanup and avoid rejoin attempts
			errorChannel.Leave();
			Assert.IsTrue(closeCalled);

			/// 
			/// test channel joining and receiving a custom event
			/// 
			Message joinOkReply = null;
			Message joinErrorReply = null;

			Message afterJoinMessage = null;
			Message closeMessage = null;
			Message errorMessage = null;

			var param = new Dictionary<string, object> {
				{ "auth", "doesn't matter" },
			};

			var roomChannel = socket.Channel("tester:phoenix-sharp", param);
			roomChannel.On(Message.InBoundEvent.phx_close, m => closeMessage = m);
			roomChannel.On(Message.InBoundEvent.phx_error, m => errorMessage = m);
			roomChannel.On("after_join", m => afterJoinMessage = m);

			roomChannel.Join()
				.Receive(Message.Reply.Status.ok, r => joinOkReply = r)
				.Receive(Message.Reply.Status.error, r => joinErrorReply = r);

			Assert.That(() => joinOkReply != null, Is.True.After(networkDelay, 10));
			Assert.IsNull(joinErrorReply);

			Assert.That(() => afterJoinMessage != null, Is.True.After(networkDelay, 10));
			Assert.AreEqual("Welcome!", afterJoinMessage.payload["message"] as string);

			// 1. heartbeat, 2. error, 3. join, 4. after_join
			// TODO: see what changed here
			// Assert.AreEqual(4, onMessageData.Count, "Unexpected message count: " + string.Join("; ", onMessageData));

			/// 
			/// test echo reply
			/// 
			var payload = new Dictionary<string, object> {
					{ "echo", "test" }
			};

			Message.Reply testOkReply = null;

			roomChannel
				.Push("reply_test", payload)
				.Receive(Message.Reply.Status.ok, r => testOkReply = r.ParseReply());

			Assert.That(() => testOkReply != null, Is.True.After(networkDelay, 10));
			Assert.IsNotNull(testOkReply.response);
			CollectionAssert.AreEquivalent(testOkReply.response, payload);

			/// 
			/// test error reply
			/// 
			Message.Reply testErrorReply = null;

			roomChannel
				.Push("error_test")
				.Receive(Message.Reply.Status.error, r => testErrorReply = r.ParseReply());

			Assert.That(() => testErrorReply != null, Is.True.After(networkDelay, 10));
			Assert.AreEqual(testErrorReply.replyStatus, Message.Reply.Status.error);

			/// 
			/// test timeout reply
			/// 
			Message testTimeoutReply = null;

			roomChannel
				.Push("timeout_test", null, TimeSpan.FromMilliseconds(50))
				.Receive(Message.Reply.Status.timeout, r => testTimeoutReply = r);

			Assert.That(() => testTimeoutReply != null, Is.False.After(20));
			Assert.That(() => testTimeoutReply != null, Is.True.After(40));

			///	
			/// test channel error/rejoin
			/// 
			Assert.IsNull(errorMessage);
			// we track rejoining through the same join push callback we setup
			joinOkReply = null;

			socket.Disconnect();
			socket.Connect();

			Assert.That(() => errorMessage != null, Is.True.After(networkDelay, 10));
			Assert.That(() => joinOkReply != null, Is.True.After(networkDelay, 10));
			Assert.That(() => roomChannel.CanPush(), Is.True.After(networkDelay, 10));

			/// 
			/// test channel replace
			/// 
			joinOkReply = null;
			joinErrorReply = null;
			errorMessage = null;
			Assert.IsNull(closeMessage);
			Message newCloseMessage = null;

			var newRoomChannel = socket.Channel("tester:phoenix-sharp", param);
			newRoomChannel.On(Message.InBoundEvent.phx_close, m => newCloseMessage = m);

			newRoomChannel.Join()
				.Receive(Message.Reply.Status.ok, r => joinOkReply = r)
				.Receive(Message.Reply.Status.error, r => joinErrorReply = r);

			Assert.That(() => joinOkReply != null, Is.True.After(networkDelay, 10));
			Assert.IsNull(joinErrorReply);
			Assert.IsNotNull(errorMessage);
			Assert.IsNotNull(closeMessage);

			/// 
			/// test channel leave
			/// also, it should discard any additional messages
			/// 
			Assert.IsNull(newCloseMessage);
			Message pushMessage = null;

			newRoomChannel.On("push_test", m => pushMessage = m);
			newRoomChannel.Push("push_test", payload);

			Assert.IsNull(pushMessage);
			newRoomChannel.Leave();

			Assert.That(() => newCloseMessage != null, Is.True.After(networkDelay, 10));
			Assert.IsNull(pushMessage); // ignored
		}

		[Test()]
		public void MultipleJoinIntegrationTest() {
			var onOpenCount = 0;
			Socket.OnOpenDelegate onOpenCallback = () => onOpenCount++;
			Socket.OnClosedDelegate onClosedCallback = (code, message) => onOpenCount--;

			List<Message> onMessageData = new();
			Socket.OnMessageDelegate onMessageCallback = m => onMessageData.Add(m);

			var socketAddress = string.Format("ws://{0}/socket", host);
			var socketFactory = new DotNetWebSocketFactory();
			var socket = new Socket(socketAddress, null, socketFactory, new Socket.Options {
				rejoinAfter = (_) => TimeSpan.FromMilliseconds(200),
				logger = new BasicLogger()
			});

			socket.OnOpen += onOpenCallback;
			socket.OnClose += onClosedCallback;
			socket.OnMessage += onMessageCallback;

			socket.Connect();
			Assert.IsTrue(socket.state == WebsocketState.Open);
			Assert.AreEqual(1, onOpenCount);

			Message joinOkReply = null;
			Message joinErrorReply = null;
			Message afterJoinMessage = null;
			Message closeMessage = null;
			Message errorMessage = null;

			//Try to join for the first time
			var param = new Dictionary<string, object> {
				{ "auth", "doesn't matter" },
			};

			var roomChannel = socket.Channel("tester:phoenix-sharp", param);
			roomChannel.On(Message.InBoundEvent.phx_close, m => closeMessage = m);
			roomChannel.On(Message.InBoundEvent.phx_error, m => errorMessage = m);
			roomChannel.On("after_join", m => afterJoinMessage = m);

			roomChannel.Join()
				.Receive(Message.Reply.Status.ok, r => joinOkReply = r)
				.Receive(Message.Reply.Status.error, r => joinErrorReply = r);

			Assert.That(() => joinOkReply != null, Is.True.After(networkDelay, 10));
			Assert.IsNull(joinErrorReply);

			Assert.That(() => afterJoinMessage != null, Is.True.After(networkDelay, 10));
			Assert.AreEqual("Welcome!", afterJoinMessage.payload["message"] as string);

			Assert.AreEqual(Channel.State.Joined, roomChannel.state);

			var conn = socket.conn;
			socket.Disconnect();

			Assert.That(() => socket.conn == null, Is.True.After(networkDelay, 10));
			Assert.That(() => conn.state == WebsocketState.Closed, Is.True.After(networkDelay, 10));

			socket.Connect();
			Assert.IsTrue(socket.state == WebsocketState.Open);
			Assert.AreEqual(1, onOpenCount);
		}
	}
}

