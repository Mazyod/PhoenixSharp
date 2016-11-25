using System;
using System.Collections.Generic;
using Phoenix;
using NUnit.Framework;
using NSubstitute;
using System.Net;
using Newtonsoft.Json.Linq;


namespace PhoenixTests {

	[TestFixture()]
	public class IntegrationTests {

		[Test()]
		public void IntegrationTest() {

			/// 
			/// setup
			/// 
			var host = "localhost:4000";
			var address = string.Format("http://{0}/api/health-check", host);

			using (WebClient client = new WebClient()) {
				client.Headers.Add("Content-Type", "application/json");
				client.DownloadString(address);
			}

			// connecting is synchronous for now
			var socket = new Socket();
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
			
			System.Threading.Thread.Sleep(100);
			Assert.IsNull(errorReply);
			Assert.IsNotNull(okReply);
			Assert.IsTrue(afterJoinMessage.HasValue);
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

			System.Threading.Thread.Sleep(100);
			Assert.IsTrue(testOkReply.HasValue);
			Assert.IsNotNull(testOkReply.Value.response);
			Assert.AreEqual(testOkReply.Value.response, testOkMessage.payload);


			/// 
			/// test error reply
			/// 
			Reply? testErrorReply = null;

			roomChannel
				.Push("error_test")
				.Receive(Reply.Status.Error, r => testErrorReply = r);

			System.Threading.Thread.Sleep(100);
			Assert.IsTrue(testErrorReply.HasValue);
			Assert.AreEqual(testErrorReply.Value.status, Reply.Status.Error);

			/// 
			/// test channel leave
			/// 
			Assert.IsFalse(closeMessage.HasValue);
			roomChannel.Leave();
			System.Threading.Thread.Sleep(100);
			Assert.IsTrue(closeMessage.HasValue);
		}
	}
}

