using NUnit.Framework;
using Phoenix;

namespace PhoenixTests {

	[TestFixture()]
	public class ChannelTests {

		public static Channel channel {
			get {
				return new Channel("phoenix-test", null, SocketTests.socket);
			}
		}

		[Test()]
		public void JoinChannelTest() {
			var channel = ChannelTests.channel;

			channel.socket.Connect();
			var websocket = channel.socket.conn as MockWebsocketAdapter;
			Assert.IsNotNull(websocket);

			// it "sets state to joining"
			channel.Join();
			Assert.AreEqual(channel.state, Channel.State.Joining);

			// it "throws if attempting to join multiple times"
			Assert.That(() => channel.Join(), Throws.InstanceOf<System.Exception>());

			// it "triggers socket push with channel params"
			Assert.AreEqual(websocket.callSend.Count, 1);
			Assert.IsTrue(websocket.callSend[0].Contains("phx_join"));
		}
	}
}