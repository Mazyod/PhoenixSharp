using System;
using System.Collections.Generic;
using Phoenix;
using NUnit.Framework;
using System.Net;
using Newtonsoft.Json.Linq;
using WebSocketSharp;


namespace PhoenixTests {

	public sealed class WebsocketSharpAdapter: IWebsocket {

		private readonly WebSocket ws;
		private readonly WebsocketConfiguration config;


		public WebsocketSharpAdapter(WebSocket ws, WebsocketConfiguration config) {
			
			this.ws = ws;
			this.config = config;

			ws.OnOpen += OnWebsocketOpen;
			ws.OnClose += OnWebsocketClose;
			ws.OnError += OnWebsocketError;
			ws.OnMessage += OnWebsocketMessage;
		}


		#region IWebsocket methods

		public void Connect() {
			ws.Connect();
		}

		public void Send(string message) {
			ws.Send(message);
		}

		public void Close(ushort? code = null, string message = null) {
			ws.Close();
		}

		#endregion


		#region websocketsharp callbacks

		public void OnWebsocketOpen(object sender, EventArgs args) {
			config.onOpenCallback(this);
		}

		public void OnWebsocketClose(object sender, CloseEventArgs args) {
			config.onCloseCallback(this, args.Code, args.Reason);
		}

		public void OnWebsocketError(object sender, ErrorEventArgs args) {
			config.onErrorCallback(this, args.Message);
		}

		public void OnWebsocketMessage(object sender, MessageEventArgs args) {
			config.onMessageCallback(this, args.Data);
		}

		#endregion
	}

	public sealed class WebsocketSharpFactory: IWebsocketFactory {

		public IWebsocket Build(WebsocketConfiguration config) {

			var socket = new WebSocket(config.uri.AbsoluteUri);
			return new WebsocketSharpAdapter(socket, config);
		}
	}


	[TestFixture()]
	public class IntegrationTests {

		private const int networkDelay = 500 /* ms */;

		[Test()]
		public void IntegrationTest() {

			/// 
			/// setup
			/// 
			var host = "localhost:4000";
			var address = string.Format("http://{0}/api/health-check", host);

			// heroku health check
			using (WebClient client = new WebClient()) {
				client.Headers.Add("Content-Type", "application/json");
				client.DownloadString(address);
			}

			var onOpenCount = 0;
			Socket.OnOpenDelegate onOpenCallback = () => onOpenCount++;

			List<String> onMessageData = new List<string>();
			Socket.OnMessageDelegate onMessageCallback = m => onMessageData.Add(m);

			// connecting is synchronous as implemented above
			var socketFactory = new WebsocketSharpFactory();
			var socket = new Socket(socketFactory);
			socket.OnOpen += onOpenCallback;
			socket.OnMessage += onMessageCallback;

			socket.Connect(string.Format("ws://{0}/socket", host), null);
			Assert.IsTrue(socket.state == Socket.State.Open);
			Assert.AreEqual(1, onOpenCount);

			/// 
			/// test channel error on join
			/// 
			Reply? okReply = null;
			Reply? errorReply = null;

			var errorChannel = socket.MakeChannel("tester:phoenix-sharp");

			errorChannel.Join()
				.Receive(Reply.Status.Ok, r => okReply = r)
				.Receive(Reply.Status.Error, r => errorReply = r);

			Assert.That(() => errorReply.HasValue, Is.True.After(networkDelay, 10));
			Assert.IsNull(okReply);
			Assert.AreEqual(Channel.State.Closed, errorChannel.state);

			/// 
			/// test channel joining and receiving a custom event
			/// 
			okReply = null;
			errorReply = null;

			Message? afterJoinMessage = null;
			Message? closeMessage = null;

			var param = new Dictionary<string, object>() {
				{ "auth", "doesn't matter" },
			};

			var roomChannel = socket.MakeChannel("tester:phoenix-sharp");
			roomChannel.On(Message.InBoundEvent.Close, m => closeMessage = m);
			roomChannel.On("after_join", m => afterJoinMessage = m);

			roomChannel.Join(param)
				.Receive(Reply.Status.Ok, r => okReply = r)
				.Receive(Reply.Status.Error, r => errorReply = r);
			
			Assert.That(() => okReply.HasValue, Is.True.After(networkDelay, 10));
			Assert.IsNull(errorReply);

			Assert.That(() => afterJoinMessage.HasValue, Is.True.After(networkDelay, 10));
			Assert.AreEqual("Welcome!", afterJoinMessage.Value.payload["message"].Value<string>());

			// 1. heartbeat, 2. error, 3. join, 4. after_join
			Assert.AreEqual(4, onMessageData.Count, "Unexpected message count: " + string.Join("; ", onMessageData));

			/// 
			/// test echo push/reply
			/// 
			var payload = new Dictionary<string, object>() {
					{ "echo", "test" }
			};

			Reply? testOkReply = null;

			roomChannel
				.Push("push_test", payload)
				.Receive(Reply.Status.Ok, r => testOkReply = r);

			Assert.That(() => testOkReply.HasValue, Is.True.After(networkDelay, 10));
			Assert.IsNotNull(testOkReply.Value.response);
			CollectionAssert.AreEquivalent(testOkReply.Value.response.ToObject<Dictionary<string, object>>(), payload);


			/// 
			/// test error reply
			/// 
			Reply? testErrorReply = null;

			roomChannel
				.Push("error_test")
				.Receive(Reply.Status.Error, r => testErrorReply = r);

			Assert.That(() => testErrorReply.HasValue, Is.True.After(networkDelay, 10));
			Assert.AreEqual(testErrorReply.Value.status, Reply.Status.Error);

			/// 
			/// test timeout reply
			/// 
			Reply? testTimeoutReply = null;

			roomChannel
				.Push("no_reply", null, TimeSpan.FromMilliseconds(50))
				.Receive(Reply.Status.Timeout, r => testTimeoutReply = r);

			Assert.That(() => testTimeoutReply.HasValue, Is.False.After(20));
			Assert.That(() => testTimeoutReply.HasValue, Is.True.After(40));

			/// 
			/// test channel leave
			/// 
			Assert.IsFalse(closeMessage.HasValue);
			roomChannel.Leave();

			Assert.That(() => closeMessage.HasValue, Is.True.After(networkDelay, 10));
		}
	}
}

