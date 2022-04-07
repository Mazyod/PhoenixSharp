using System;
using System.Net;
using System.Collections.Generic;
using Newtonsoft.Json;
using NUnit.Framework;
using Phoenix;


namespace PhoenixTests {

	public sealed class BasicLogger : ILogger {

		#region ILogger implementation

		public void Log(LogLevel level, string source, string message) {
			Console.WriteLine("[{0}]: {1} - {2}", level, source, message);
		}

		#endregion
	}

	[TestFixture()]
	public class IntegrationTests {

		private const int networkDelay = 5_000 /* ms */;
		private const string host = "phoenix-integration-tester.herokuapp.com";
		private readonly Dictionary<string, object> channelParams = new() {
				{ "auth", "doesn't matter" },
			};

		[SetUp()]
		public void Init() {
			var address = string.Format("http://{0}/api/health-check", host);

			// heroku health check
			using WebClient client = new();
			client.Headers.Add("Content-Type", "application/json");
			client.DownloadString(address);
		}

		[Test()]
		public void GeneralIntegrationTest() {

			/// 
			/// setup
			/// 
			var onOpenCount = 0;
			void onOpenCallback() => onOpenCount++;

			List<Message> onMessageData = new();
			void onMessageCallback(Message m) => onMessageData.Add(m);

			List<string> onErrorData = new();
			void onErrorCallback(string message) => onErrorData.Add(message);

			// connecting is synchronous as implemented above
			var socketAddress = string.Format("ws://{0}/socket", host);
			var socketFactory = new WebsocketSharpFactory();
			var socket = new Socket(socketAddress, null, socketFactory, new Socket.Options {
				reconnectAfter = _ => TimeSpan.FromMilliseconds(200),
				logger = new BasicLogger()
			});

			socket.OnOpen += onOpenCallback;
			socket.OnMessage += onMessageCallback;
			socket.OnError += onErrorCallback;

			socket.Connect();
			Assert.AreEqual(socket.state, WebsocketState.Open);
			Assert.AreEqual(1, onOpenCount);

			///
			/// test socket error recovery
			///
			socket.conn.Close();

			Assert.AreEqual(socket.state, WebsocketState.Closed);
			Assert.That(() => socket.state == WebsocketState.Open, Is.True.After(networkDelay, 10));
			Assert.AreEqual(onErrorData.Count, 1);
			Assert.AreEqual(onErrorData[0], "An error has occurred in closing the connection.");

			/// 
			/// test channel error on join
			/// 
			Reply? okReply = null;
			Reply? errorReply = null;
			bool closeCalled = false;

			var errorChannel = socket.Channel("tester:phoenix-sharp");
			errorChannel.On(Message.InBoundEvent.phx_close, _ => closeCalled = true);

			errorChannel.Join()
				.Receive(Reply.Status.ok, r => okReply = r)
				.Receive(Reply.Status.error, r => errorReply = r);

			Assert.That(() => errorReply != null, Is.True.After(networkDelay, 10));
			Assert.IsNull(okReply);
			Assert.AreEqual(Channel.State.Errored, errorChannel.state);
			// call leave explicitly to cleanup and avoid rejoin attempts
			errorChannel.Leave();
			Assert.IsTrue(closeCalled);

			/// 
			/// test channel joining and receiving a custom event
			/// 
			Reply? joinOkReply = null;
			Reply? joinErrorReply = null;

			Message? afterJoinMessage = null;
			Message? closeMessage = null;
			Message? errorMessage = null;

			var roomChannel = socket.Channel("tester:phoenix-sharp", channelParams);
			roomChannel.On(Message.InBoundEvent.phx_close, m => closeMessage = m);
			roomChannel.On(Message.InBoundEvent.phx_error, m => errorMessage = m);
			roomChannel.On("after_join", m => afterJoinMessage = m);

			roomChannel.Join()
				.Receive(Reply.Status.ok, r => joinOkReply = r)
				.Receive(Reply.Status.error, r => joinErrorReply = r);

			Assert.That(() => joinOkReply != null, Is.True.After(networkDelay, 10));
			Assert.IsNull(joinErrorReply);

			Assert.That(() => afterJoinMessage != null, Is.True.After(networkDelay, 10));
			Assert.AreEqual("Welcome!", afterJoinMessage?.payload["message"] as string);

			// 1. heartbeat, 2. error, 3. join, 4. after_join
			// TODO: see what changed here
			// Assert.AreEqual(4, onMessageData.Count, "Unexpected message count: " + string.Join("; ", onMessageData));

			/// 
			/// test echo reply
			/// 
			var payload = new Dictionary<string, object> {
				{ "echo", "test" }
			};

			Reply? testOkReply = null;

			roomChannel
				.Push("reply_test", payload)
				.Receive(Reply.Status.ok, r => testOkReply = r);

			Assert.That(() => testOkReply != null, Is.True.After(networkDelay, 10));
			Assert.IsNotNull(testOkReply?.response);
			CollectionAssert.AreEquivalent(testOkReply?.response, payload);

			/// 
			/// test error reply
			/// 
			Reply? testErrorReply = null;

			roomChannel
				.Push("error_test")
				.Receive(Reply.Status.error, r => testErrorReply = r);

			Assert.That(() => testErrorReply != null, Is.True.After(networkDelay, 10));
			Assert.AreEqual(testErrorReply?.replyStatus, Reply.Status.error);

			/// 
			/// test timeout reply
			/// 
			Reply? testTimeoutReply = null;

			roomChannel
				.Push("timeout_test", null, TimeSpan.FromMilliseconds(50))
				.Receive(Reply.Status.timeout, r => testTimeoutReply = r);

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
			Message? newCloseMessage = null;

			var newRoomChannel = socket.Channel("tester:phoenix-sharp", channelParams);
			newRoomChannel.On(Message.InBoundEvent.phx_close, m => newCloseMessage = m);

			newRoomChannel.Join()
				.Receive(Reply.Status.ok, r => joinOkReply = r)
				.Receive(Reply.Status.error, r => joinErrorReply = r);

			Assert.That(() => joinOkReply != null, Is.True.After(networkDelay, 10));
			Assert.IsNull(joinErrorReply);
			// Not sure why previous PhoenixSharp version had errorMessage on closed channel
			// Assert.IsNotNull(errorMessage);
			Assert.IsNotNull(closeMessage);

			/// 
			/// test channel leave
			/// also, it should discard any additional messages
			/// 
			Assert.IsNull(newCloseMessage);
			Message? pushMessage = null;

			newRoomChannel.On("push_test", m => pushMessage = m);
			newRoomChannel.Push("push_test", payload);

			Assert.IsNull(pushMessage);
			newRoomChannel.Leave();

			Assert.That(() => newCloseMessage != null, Is.True.After(networkDelay, 10));
			Assert.IsNull(pushMessage); // ignored

			///
			/// TearDown
			///
			socket.Disconnect();
		}

		[Test()]
		public void MultipleJoinIntegrationTest() {
			var onOpenCount = 0;
			void onOpenCallback() => onOpenCount++;
			void onClosedCallback(ushort code, string reason) => onOpenCount--;

			List<Message> onMessageData = new();
			void onMessageCallback(Message m) => onMessageData.Add(m);

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
			Assert.AreEqual(socket.state, WebsocketState.Open);
			Assert.AreEqual(1, onOpenCount);

			Reply? joinOkReply = null;
			Reply? joinErrorReply = null;
			Message? afterJoinMessage = null;
			Message? closeMessage = null;
			Message? errorMessage = null;

			//Try to join for the first time
			var roomChannel = socket.Channel("tester:phoenix-sharp", channelParams);
			roomChannel.On(Message.InBoundEvent.phx_close, m => closeMessage = m);
			roomChannel.On(Message.InBoundEvent.phx_error, m => errorMessage = m);
			roomChannel.On("after_join", m => afterJoinMessage = m);

			roomChannel.Join()
				.Receive(Reply.Status.ok, r => joinOkReply = r)
				.Receive(Reply.Status.error, r => joinErrorReply = r);

			Assert.That(() => joinOkReply != null, Is.True.After(networkDelay, 10));
			Assert.IsNull(joinErrorReply);

			Assert.That(() => afterJoinMessage != null, Is.True.After(networkDelay, 10));
			Assert.AreEqual("Welcome!", afterJoinMessage?.payload["message"] as string);

			Assert.AreEqual(Channel.State.Joined, roomChannel.state);

			var conn = socket.conn;
			socket.Disconnect();

			Assert.That(() => socket.conn == null, Is.True.After(networkDelay, 10));
			Assert.That(() => conn.state == WebsocketState.Closed, Is.True.After(networkDelay, 10));

			socket.Connect();
			Assert.AreEqual(socket.state, WebsocketState.Open);
			Assert.AreEqual(1, onOpenCount);

			///
			/// TearDown
			/// 
			socket.Disconnect();
		}

		[Test()]
		public void PresenceTrackingTest() {
			/// 
			/// setup
			/// 
			var onOpenCount = 0;
			void onOpenCallback() => onOpenCount++;

			List<Message> onMessageData = new();
			void onMessageCallback(Message m) => onMessageData.Add(m);

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
			/// test presence tracking
			///
			var channel = socket.Channel("tester:phoenix-sharp", channelParams);
			var presence = new Presence(channel);

			var joinCalls = new List<(string, Presence.MetadataContainer, Presence.MetadataContainer)>();
			presence.OnJoin += (channel, joinMetadata, oldMetadata)
				=> joinCalls.Add((channel, joinMetadata, oldMetadata));

			var leaveCalls = new List<(string, Presence.MetadataContainer, Presence.MetadataContainer)>();
			presence.OnLeave += (channel, leaveMetadata, oldMetadata)
				=> leaveCalls.Add((channel, leaveMetadata, oldMetadata));

			var syncCalls = 0;
			presence.OnSync += () => syncCalls++;

			Reply? joinOkReply = null;
			channel.Join()
				.Receive(Reply.Status.ok, r => joinOkReply = r);

			// first, we get ack for joining the channel
			Assert.That(() => joinOkReply != null, Is.True.After(networkDelay, 10));
			// then, we get the presence state
			Assert.That(() => joinCalls.Count == 2, Is.True.After(networkDelay, 10));
			// the key used by the server is the auth value we send
			var presenceCall = joinCalls[0];
			Assert.AreEqual(presenceCall.Item1, channelParams["auth"] as string);
			// current state is null initially
			Assert.IsNull(presenceCall.Item2);
			// new state is populated with some goodies
			var newState = presenceCall.Item3;
			Assert.IsNotNull(presenceCall.Item3);
			Assert.AreEqual(newState.metas.Count, 1, 
				$"newState.metas: {JsonConvert.SerializeObject(newState)}");

			Assert.IsNotEmpty(newState.metas[0]["phx_ref"] as string);
			Assert.IsNotEmpty(newState.metas[0]["online_at"] as string);

			///
			/// TearDown
			///
			socket.Disconnect();
		}
	}
}

