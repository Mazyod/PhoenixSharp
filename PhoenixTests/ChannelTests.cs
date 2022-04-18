using System;
using NUnit.Framework;
using Phoenix;
using PhoenixTests.WebSocketImpl;

namespace PhoenixTests
{
    [TestFixture]
    public class ChannelTests
    {
        public static Channel TestChannel => new("phoenix-test", null, SocketTests.Socket);

        [Test]
        public void JoinChannelTest()
        {
            var channel = TestChannel;

            channel.Socket.Connect();
            var websocket = channel.Socket.Conn as MockWebsocketAdapter;
            Assert.IsNotNull(websocket);

            // it "sets state to joining"
            channel.Join();
            Assert.AreEqual(ChannelState.Joining, channel.State);

            // it "throws if attempting to join multiple times"
            Assert.That(() => channel.Join(), Throws.InstanceOf<Exception>());

            // it "triggers socket push with channel params"
            CollectionAssert.AreEqual(
                new[] {@"[""1"",""1"",""phoenix-test"",""phx_join"",{}]"},
                websocket.CallSend
            );
        }

        [Test]
        public void ChannelPushTest()
        {
            var channel = TestChannel;
            var socket = channel.Socket;
            socket.Connect();

            var websocket = socket.Conn as MockWebsocketAdapter;
            Assert.IsNotNull(websocket);
            // close the socket for now
            websocket.MockState = WebsocketState.Closed;

            // pushing before joining should throw
            Assert.That(
                () => channel.Push("event"),
                Throws.InstanceOf<Exception>()
            );

            // now, join before the socket is connected
            var joinPush = channel.Join();
            channel.Push("event");
            // it should cache both the join and the event push
            CollectionAssert.IsEmpty(websocket.CallSend);

            // now, connect the socket
            websocket.Connect();
            // it should first send only the join push
            Assert.AreEqual(1, websocket.CallSend.Count);
            Assert.IsTrue(websocket.CallSend[0].Contains("phx_join"));
            websocket.CallSend.Clear();

            // once we get join acknowledgement, it should send the event
            joinPush.Trigger(ReplyStatus.Ok);
            CollectionAssert.AreEqual(
                new[] {@"[""1"",""3"",""phoenix-test"",""event"",{}]"},
                websocket.CallSend
            );
        }
    }
}