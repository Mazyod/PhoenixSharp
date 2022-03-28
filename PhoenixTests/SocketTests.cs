using System;
using NUnit.Framework;
using Phoenix;


namespace PhoenixTests {

	[TestFixture()]
	public class SocketTests {

		[Test()]
		public void BuffersDataWhenNotConnectedTest() {

			var socket = new Socket("ws://localhost:1234", null, new MockWebsocketFactory());
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

		/**
    describe("flushSendBuffer", function(){
      beforeEach(function(){
        socket = new Socket("/socket")
        socket.connect()
      })

      it("calls callbacks in buffer when connected", function(){
        socket.conn.readyState = 1 // open
        const spy1 = sinon.spy()
        const spy2 = sinon.spy()
        const spy3 = sinon.spy()
        socket.sendBuffer.push(spy1)
        socket.sendBuffer.push(spy2)

        socket.flushSendBuffer()

        assert.ok(spy1.calledOnce)
        assert.ok(spy2.calledOnce)
        assert.equal(spy3.callCount, 0)
      })

      it("empties sendBuffer", function(){
        socket.conn.readyState = 1 // open
        socket.sendBuffer.push(() => {})

        socket.flushSendBuffer()

        assert.deepEqual(socket.sendBuffer.length, 0)
      })
    })
    */

		/** Test Github Issue #19:
		 *	phx_join never sent if socket is not open by the time Join is called.
		 */
		[Test()]
		public void FlushSendBufferTest() {

			var socket = new Socket("ws://localhost:1234", null, new MockWebsocketFactory());
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

			var joinEvent = Message.OutBoundEvent.phx_join.ToString();
			Assert.That(conn.callSend[0].Contains(joinEvent));
		}
	}
}

