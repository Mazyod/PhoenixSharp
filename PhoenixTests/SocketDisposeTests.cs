using System;
using NUnit.Framework;
using Phoenix;
using PhoenixTests.TestDoubles;
using PhoenixTests.WebSocketImpl;

namespace PhoenixTests
{
    [TestFixture, Category("Unit")]
    public class SocketDisposeTests : PhoenixTestBase
    {
        private static Socket CreateSocket() =>
            new(
                "ws://localhost:1234",
                null,
                new MockWebsocketFactory(),
                new Socket.Options(new JsonMessageSerializer())
            );

        [Test]
        public void DisposeClosesConnectionTest()
        {
            var socket = CreateSocket();
            socket.Connect();
            var conn = socket.Conn as MockWebsocketAdapter;
            Assert.IsNotNull(conn);

            socket.Dispose();

            Assert.AreEqual(1, conn.CallCloseCount);
            Assert.IsNull(socket.Conn);
        }

        [Test]
        public void DisposeCanBeCalledMultipleTimesTest()
        {
            var socket = CreateSocket();
            socket.Connect();
            var conn = socket.Conn as MockWebsocketAdapter;
            Assert.IsNotNull(conn);

            socket.Dispose();
            socket.Dispose();
            socket.Dispose();

            // Should only close once
            Assert.AreEqual(1, conn.CallCloseCount);
        }

        [Test]
        public void DisposeClearsSendBufferTest()
        {
            var socket = CreateSocket();
            socket.Connect();
            var conn = socket.Conn as MockWebsocketAdapter;
            Assert.IsNotNull(conn);

            conn.MockState = WebsocketState.Connecting;
            socket.Push(new Message());
            Assert.AreEqual(1, socket.SendBuffer.Count);

            socket.Dispose();
            Assert.AreEqual(0, socket.SendBuffer.Count);
        }

        [Test]
        public void DisposeClearsDelegatesTest()
        {
            var socket = CreateSocket();
            var openCalled = false;
            var closeCalled = false;
            var errorCalled = false;
            var messageCalled = false;

            socket.OnOpen += () => openCalled = true;
            socket.OnClose += (_, _) => closeCalled = true;
            socket.OnError += _ => errorCalled = true;
            socket.OnMessage += _ => messageCalled = true;

            socket.Dispose();

            Assert.IsNull(socket.OnOpen);
            Assert.IsNull(socket.OnClose);
            Assert.IsNull(socket.OnError);
            Assert.IsNull(socket.OnMessage);
            Assert.IsFalse(openCalled);
            Assert.IsFalse(closeCalled);
            Assert.IsFalse(errorCalled);
            Assert.IsFalse(messageCalled);
        }

        [Test]
        public void DisposePreventsFurtherConnectCallsTest()
        {
            var socket = CreateSocket();
            socket.Dispose();

            socket.Connect();

            Assert.IsNull(socket.Conn);
        }

        [Test]
        public void DisposeOnDisconnectedSocketTest()
        {
            var socket = CreateSocket();
            // Never connected, should not throw
            Assert.DoesNotThrow(() => socket.Dispose());
        }

        [Test]
        public void ChannelCreationOnDisposedSocketThrowsTest()
        {
            var socket = CreateSocket();
            socket.Dispose();

            Assert.Throws<ObjectDisposedException>(() => socket.Channel("test"));
        }

        [Test]
        public void DisposeDuringReconnectAttemptTest()
        {
            var mockExecutor = new TrackingDelayedExecutor();
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                DelayedExecutor = mockExecutor,
                HeartbeatInterval = null,
                ReconnectAfter = _ => TimeSpan.FromMilliseconds(100)
            };

            var socket = new Socket("ws://localhost:1234", null, factory, options);
            socket.Connect();

            var conn = factory.LastCreatedWebsocket;
            Assert.IsNotNull(conn);

            // Simulate connection loss
            conn.SimulateClose(1006, "Lost");

            // Reconnect is scheduled
            var reconnect = mockExecutor.Executions.FindLast(e => !e.IsCancelled);
            Assert.IsNotNull(reconnect);

            // Dispose before reconnect fires
            socket.Dispose();

            // Trigger reconnect - should not throw and should not create new connection
            Assert.DoesNotThrow(() => reconnect!.Execute());

            // Verify no connection was made (Connect checks _disposed first)
            Assert.IsNull(socket.Conn);
        }
    }
}
