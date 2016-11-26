using System;
using System.Collections.Generic;
using Phoenix;
using NUnit.Framework;
using NSubstitute;
using System.Net;
using Newtonsoft.Json.Linq;
using WebSocketSharp;


namespace PhoenixTests {

	public sealed class WebsocketSharpAdapter: IWebsocket {

		private WebSocket ws;

		public WebsocketSharpAdapter(WebSocket ws) {
			this.ws = ws;
		}

		public void Connect() {
			ws.Connect();
		}

		public void Send(string message) {
			ws.Send(message);
		}

		public void Close(ushort? code = null, string message = null) {
			ws.Close();
		}
	}

	public sealed class WebsocketSharpFactory: IWebsocketFactory {

		public IWebsocket Build(WebsocketConfiguration config) {

			var socket = new WebSocket(config.uri.AbsoluteUri);
			socket.OnOpen += (_, __) => config.onOpenCallback();
			socket.OnClose += (_, __) => config.onCloseCallback();
			socket.OnError += (_, __) => config.onErrorCallback();
			socket.OnMessage += (_, args) => config.onMessageCallback(args.Data);

			return new WebsocketSharpAdapter(socket);
		}
	}

	[TestFixture()]
	public class IntegrationTests {

		private const int networkDelay = 500;

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

			// connecting is synchronous as implemented above
			var socketFactory = new WebsocketSharpFactory();
			var socket = new Socket(socketFactory);
			socket.Connect(string.Format("ws://{0}/socket", host), null);
			Assert.IsTrue(socket.state == Socket.State.Open);

			/// 
			/// test channel joining and receiving a custom event
			/// 
			Reply? okReply = null;
			Reply? errorReply = null;

			Message? afterJoinMessage = null;
			Message? closeMessage = null;

			var param = new Dictionary<string, object>() {
				{ "auth", "doesn't matter" },
			};

			var roomChannel = socket.MakeChannel("tester:phoenix-sharp", param);
			roomChannel.On(Message.InBoundEvent.Close, m => closeMessage = m);
			roomChannel.On("after_join", m => afterJoinMessage = m);

			roomChannel.Join()
				.Receive(Reply.Status.Ok, r => okReply = r)
				.Receive(Reply.Status.Error, r => errorReply = r);
			
			Assert.That(() => okReply.HasValue, Is.True.After(networkDelay, 10));
			Assert.IsNull(errorReply);

			Assert.That(() => afterJoinMessage.HasValue, Is.True.After(networkDelay, 10));
			Assert.AreEqual("Welcome!", afterJoinMessage.Value.payload["message"].Value<string>());

			/// 
			/// test echo push/reply
			/// 
			var testOkMessage = new Message() {
				@event = "push_test",
				payload = JObject.FromObject(new Dictionary<string, object>() {
					{ "echo", "test" },
				})
			};

			Reply? testOkReply = null;

			roomChannel
				.Push(testOkMessage)
				.Receive(Reply.Status.Ok, r => testOkReply = r);

			Assert.That(() => testOkReply.HasValue, Is.True.After(networkDelay, 10));
			Assert.IsNotNull(testOkReply.Value.response);
			Assert.AreEqual(testOkReply.Value.response, testOkMessage.payload);


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
			/// test channel leave
			/// 
			Assert.IsFalse(closeMessage.HasValue);
			roomChannel.Leave();

			Assert.That(() => closeMessage.HasValue, Is.True.After(networkDelay, 10));
		}
	}
}

