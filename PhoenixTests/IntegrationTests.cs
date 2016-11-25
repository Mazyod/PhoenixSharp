using System;
using System.Collections.Generic;
using Phoenix;
using NUnit.Framework;
using NSubstitute;
using System.Net;


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

			var socket = new Socket();
			var param = new Dictionary<string, string>() {
				{ "bot", "616d5d09-ac10-4449-88af-7062f4cf1b86" },
			};

			// connecting is synchronous for now
			socket.Connect(string.Format("ws://{0}/socket", host), param);
			Assert.IsTrue(socket.state == Socket.State.Open);

			/// 
			/// test channel joining and receiving a custom event
			/// 
			Reply? okReply = null;
			Reply? errorReply = null;

			Message? afterJoinMessage = null;
			Message? closeMessage = null;

			var roomChannel = socket.MakeChannel("tester:phoenix-sharp");
			roomChannel.On(Message.InBoundEvent.Close, m => closeMessage = m);
			roomChannel.On("after_join", m => afterJoinMessage = m);

			roomChannel.Join()
				.Receive(Reply.Status.Ok, r => okReply = r)
				.Receive(Reply.Status.Error, r => errorReply = r);
			
			System.Threading.Thread.Sleep(100);
			Assert.IsNull(errorReply);
			Assert.IsNotNull(okReply);
			Assert.IsTrue(afterJoinMessage.HasValue);
			Assert.AreEqual("Welcome!", afterJoinMessage.Value.payload["message"]);

			/// 
			/// test echo push/reply
			/// 
			var testOkMessage = new Message() {
				@event = "push_test",
				payload = new Dictionary<string, object>() {
					{ "echo", "test" },
				}
			};

			Reply? testOkReply = null;

			roomChannel
				.Push(testOkMessage)
				.Receive(Reply.Status.Ok, r => testOkReply = r);

			System.Threading.Thread.Sleep(100);
			Assert.IsTrue(testOkReply.HasValue);
			Assert.IsNotNull(testOkReply.Value.response);
			CollectionAssert.AreEquivalent(testOkReply.Value.response, testOkMessage.payload);


			/// 
			/// test error reply
			/// 
			var testErrorMessage = new Message() {
				@event = "error_test"
			};

			Reply? testErrorReply = null;

			roomChannel
				.Push(testErrorMessage)
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

