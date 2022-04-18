using System;
using NUnit.Framework;
using Phoenix;
using PhoenixTests.WebSocketImpl;

namespace PhoenixTests
{
    [TestFixture]
    public class SocketTests
    {
        public static Socket Socket =>
            new(
                "ws://localhost:1234",
                null,
                new MockWebsocketFactory(),
                new Socket.Options(new JsonMessageSerializer())
            );

        [Test]
        public void InitializeSocketOptionsTest()
        {
            // test initializing socket options fields
            // also helps rider analyzers understand they can't be readonly
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                DelayedExecutor = new TaskDelayedExecutor(),
                HeartbeatInterval = TimeSpan.FromSeconds(1),
                Logger = null,
                ReconnectAfter = _ => TimeSpan.FromSeconds(2),
                RejoinAfter = _ => TimeSpan.FromSeconds(3),
                Timeout = TimeSpan.FromSeconds(30),
                Vsn = "1.0.0"
            };

            Assert.AreEqual(TimeSpan.FromSeconds(30), options.Timeout);
            Assert.AreEqual(TimeSpan.FromSeconds(3), options.RejoinAfter(0));
        }

        [Test]
        public void BuffersDataWhenNotConnectedTest()
        {
            var socket = Socket;
            socket.Connect();
            var conn = socket.Conn as MockWebsocketAdapter;
            Assert.IsNotNull(conn);

            conn.MockState = WebsocketState.Connecting;
            Assert.AreEqual(0, socket.SendBuffer.Count);

            socket.Push(new Message());
            Assert.AreEqual(0, conn.CallSend.Count);
            Assert.AreEqual(1, socket.SendBuffer.Count);

            var callback = socket.SendBuffer[0];
            callback();
            Assert.AreEqual(1, conn.CallSend.Count);
        }

        /**
         * Test Github Issue #19:
         * phx_join never sent if socket is not open by the time Join is called.
         */
        [Test]
        public void FlushSendBufferTest()
        {
            var socket = Socket;
            socket.Connect();
            var conn = socket.Conn as MockWebsocketAdapter;
            Assert.IsNotNull(conn);

            conn.MockState = WebsocketState.Connecting;
            var channel = socket.Channel("test");
            channel.Join();
            Assert.AreEqual(1, socket.SendBuffer.Count);

            conn.MockState = WebsocketState.Open;
            socket.FlushSendBuffer();
            Assert.AreEqual(0, socket.SendBuffer.Count);
            Assert.AreEqual(1, conn.CallSend.Count);

            var joinEvent = Message.OutBoundEvent.Join.Serialized();
            Assert.That(conn.CallSend[0].Contains(joinEvent));
        }
    }
}