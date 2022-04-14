using NUnit.Framework;
using Phoenix;


namespace PhoenixTests {

	[TestFixture()]
	public class SocketTests {

		public static Socket socket {
			get {
				return new Socket(
					"ws://localhost:1234",
					null,
					new MockWebsocketFactory(),
					new(new JSONMessageSerializer())
				);
			}
		}

		[Test()]
		public void BuffersDataWhenNotConnectedTest() {

			var socket = SocketTests.socket;
			socket.Connect();
			var conn = socket.conn as MockWebsocketAdapter;

			conn.mockState = WebsocketState.Connecting;
			Assert.AreEqual(socket.sendBuffer.Count, 0);

			socket.Push(new Message());
			Assert.AreEqual(conn.callSend.Count, 0);
			Assert.AreEqual(socket.sendBuffer.Count, 1);

			var callback = socket.sendBuffer[0];
			callback();
			Assert.AreEqual(conn.callSend.Count, 1);
		}

		/** Test Github Issue #19:
		 *	phx_join never sent if socket is not open by the time Join is called.
		 */
		[Test()]
		public void FlushSendBufferTest() {

			var socket = SocketTests.socket;
			socket.Connect();
			var conn = socket.conn as MockWebsocketAdapter;

			conn.mockState = WebsocketState.Connecting;
			var channel = socket.Channel("test");
			channel.Join();
			Assert.AreEqual(socket.sendBuffer.Count, 1);

			conn.mockState = WebsocketState.Open;
			socket.FlushSendBuffer();
			Assert.AreEqual(socket.sendBuffer.Count, 0);
			Assert.AreEqual(conn.callSend.Count, 1);

			var joinEvent = Message.OutBoundEvent.Join.Serialized();
			Assert.That(conn.callSend[0].Contains(joinEvent));
		}
	}
}

