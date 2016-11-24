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

			socket.Connect(string.Format("ws://{0}/socket", host), param);

			Reply? okReply = null;
			Reply? errorReply = null;
			Message? afterJoinMessage = null;

			var roomChannel = socket.MakeChannel("tester:phoenix-sharp");
			roomChannel.On("after_join", m => afterJoinMessage = m);

			roomChannel.Join()
				.Receive(Reply.Status.Ok, r => okReply = r)
				.Receive(Reply.Status.Error, r => errorReply = r);
			
			System.Threading.Thread.Sleep(200);
			Assert.IsNull(errorReply);
			Assert.IsNotNull(okReply);
			Assert.IsTrue(afterJoinMessage.HasValue);
			Assert.AreEqual("Welcome!", afterJoinMessage.Value.payload["message"]);

			var testMessage = new Message() {
				@event = "push_test",
				payload = new Dictionary<string, object>() {
					{ "echo", "test" },
				}
			};

			Reply? testReply = null;

			roomChannel
				.Push(testMessage)
				.Receive(Reply.Status.Ok, r => testReply = r);

			System.Threading.Thread.Sleep(200);
			Assert.IsNotNull(testReply);
			CollectionAssert.AreEquivalent(testReply.Value.payload, testMessage.payload);
		}
	}
}

