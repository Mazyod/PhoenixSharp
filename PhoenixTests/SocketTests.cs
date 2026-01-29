using System;
using System.Collections.Generic;
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

        public static Socket SocketWithParams(Dictionary<string, string>? @params = null) =>
            new(
                "ws://localhost:1234",
                @params,
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

        #region Dispose Tests

        [Test]
        public void DisposeClosesConnectionTest()
        {
            var socket = Socket;
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
            var socket = Socket;
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
            var socket = Socket;
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
            var socket = Socket;
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
            var socket = Socket;
            socket.Dispose();

            socket.Connect();

            Assert.IsNull(socket.Conn);
        }

        [Test]
        public void DisposeOnDisconnectedSocketTest()
        {
            var socket = Socket;
            // Never connected, should not throw
            Assert.DoesNotThrow(() => socket.Dispose());
        }

        [Test]
        public void ChannelCreationOnDisposedSocketThrowsTest()
        {
            var socket = Socket;
            socket.Dispose();

            Assert.Throws<ObjectDisposedException>(() => socket.Channel("test"));
        }

        #endregion

        #region Connection State Tests

        [Test]
        public void StateIsNullBeforeConnectTest()
        {
            var socket = Socket;
            Assert.IsNull(socket.State);
        }

        [Test]
        public void StateIsOpenAfterConnectTest()
        {
            var socket = Socket;
            socket.Connect();
            Assert.AreEqual(WebsocketState.Open, socket.State);
        }

        [Test]
        public void ConnectDoesNothingWhenAlreadyConnectedTest()
        {
            var socket = Socket;
            socket.Connect();
            var conn = socket.Conn as MockWebsocketAdapter;
            Assert.IsNotNull(conn);
            Assert.AreEqual(1, conn.CallConnectCount);

            socket.Connect();
            Assert.AreEqual(1, conn.CallConnectCount);
        }

        [Test]
        public void DisconnectClosesWebsocketTest()
        {
            var socket = Socket;
            socket.Connect();
            var conn = socket.Conn as MockWebsocketAdapter;
            Assert.IsNotNull(conn);

            socket.Disconnect();

            Assert.AreEqual(1, conn.CallCloseCount);
        }

        [Test]
        public void DisconnectWithCodeAndReasonTest()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );
            socket.Connect();
            var conn = factory.LastCreatedWebsocket;
            Assert.IsNotNull(conn);

            socket.Disconnect(code: 1001, reason: "Going away");

            Assert.AreEqual(1, conn.CallCloseCount);
            Assert.AreEqual((ushort)1001, conn.LastCloseCode);
            Assert.AreEqual("Going away", conn.LastCloseReason);
        }

        #endregion

        #region Error Callback Tests

        [Test]
        public void OnErrorDelegateIsInvokedOnWebsocketErrorTest()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );

            string? receivedError = null;
            socket.OnError += error => receivedError = error;

            socket.Connect();
            var conn = factory.LastCreatedWebsocket;
            Assert.IsNotNull(conn);

            conn.SimulateError("Test error message");

            Assert.AreEqual("Test error message", receivedError);
        }

        [Test]
        public void OnCloseDelegateIsInvokedOnWebsocketCloseTest()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );

            ushort? receivedCode = null;
            string? receivedReason = null;
            socket.OnClose += (code, reason) =>
            {
                receivedCode = code;
                receivedReason = reason;
            };

            socket.Connect();
            var conn = factory.LastCreatedWebsocket;
            Assert.IsNotNull(conn);

            conn.SimulateClose(1000, "Normal closure");

            Assert.AreEqual((ushort)1000, receivedCode);
            Assert.AreEqual("Normal closure", receivedReason);
        }

        [Test]
        public void OnOpenDelegateIsInvokedOnWebsocketOpenTest()
        {
            var socket = Socket;
            var openCalled = false;
            socket.OnOpen += () => openCalled = true;

            socket.Connect();

            Assert.IsTrue(openCalled);
        }

        [Test]
        public void OnMessageDelegateIsInvokedOnWebsocketMessageTest()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );

            Message? receivedMessage = null;
            socket.OnMessage += msg => receivedMessage = msg;

            socket.Connect();
            var conn = factory.LastCreatedWebsocket;
            Assert.IsNotNull(conn);

            // Simulate receiving a valid Phoenix message
            conn.SimulateMessage(@"[null,""1"",""test"",""event"",{}]");

            Assert.IsNotNull(receivedMessage);
            Assert.AreEqual("test", receivedMessage?.Topic);
            Assert.AreEqual("event", receivedMessage?.Event);
        }

        #endregion

        #region Multiple Channel Management Tests

        [Test]
        public void MultipleChannelsCanBeCreatedTest()
        {
            var socket = Socket;

            var channel1 = socket.Channel("topic1");
            var channel2 = socket.Channel("topic2");
            var channel3 = socket.Channel("topic3");

            Assert.AreEqual("topic1", channel1.Topic);
            Assert.AreEqual("topic2", channel2.Topic);
            Assert.AreEqual("topic3", channel3.Topic);
        }

        [Test]
        public void ChannelWithParamsTest()
        {
            var socket = Socket;

            var channelParams = new Dictionary<string, object>
            {
                { "token", "secret123" },
                { "user_id", 42 }
            };

            var channel = socket.Channel("topic", channelParams);

            Assert.AreEqual("topic", channel.Topic);
        }

        [Test]
        public void MessageRoutingToCorrectChannelTest()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );

            socket.Connect();
            var conn = factory.LastCreatedWebsocket;
            Assert.IsNotNull(conn);

            var channel1 = socket.Channel("topic1");
            var channel2 = socket.Channel("topic2");

            Message? channel1Message = null;
            Message? channel2Message = null;

            channel1.On("custom_event", msg => channel1Message = msg);
            channel2.On("custom_event", msg => channel2Message = msg);

            channel1.Join();
            channel2.Join();

            // Simulate message for topic1
            conn.SimulateMessage(@"[""1"",""5"",""topic1"",""custom_event"",{""data"":""for topic1""}]");

            Assert.IsNotNull(channel1Message);
            Assert.IsNull(channel2Message);
            Assert.AreEqual("topic1", channel1Message?.Topic);
        }

        #endregion

        #region Parameter Validation Tests

        [Test]
        public void ConstructorThrowsOnNullEndpointTest()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new Socket(
                    null!,
                    null,
                    new MockWebsocketFactory(),
                    new Socket.Options(new JsonMessageSerializer())
                ));
        }

        [Test]
        public void ConstructorThrowsOnEmptyEndpointTest()
        {
            Assert.Throws<ArgumentException>(() =>
                new Socket(
                    "",
                    null,
                    new MockWebsocketFactory(),
                    new Socket.Options(new JsonMessageSerializer())
                ));
        }

        [Test]
        public void ConstructorThrowsOnWhitespaceEndpointTest()
        {
            Assert.Throws<ArgumentException>(() =>
                new Socket(
                    "   ",
                    null,
                    new MockWebsocketFactory(),
                    new Socket.Options(new JsonMessageSerializer())
                ));
        }

        [Test]
        public void ConstructorThrowsOnNullWebsocketFactoryTest()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new Socket(
                    "ws://localhost:1234",
                    null,
                    null!,
                    new Socket.Options(new JsonMessageSerializer())
                ));
        }

        [Test]
        public void ConstructorThrowsOnNullOptionsTest()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new Socket(
                    "ws://localhost:1234",
                    null,
                    new MockWebsocketFactory(),
                    null!
                ));
        }

        [Test]
        public void OptionsConstructorThrowsOnNullSerializerTest()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new Socket.Options(null!));
        }

        [Test]
        public void ChannelThrowsOnNullTopicTest()
        {
            var socket = Socket;
            Assert.Throws<ArgumentNullException>(() => socket.Channel(null!));
        }

        [Test]
        public void ChannelThrowsOnEmptyTopicTest()
        {
            var socket = Socket;
            Assert.Throws<ArgumentException>(() => socket.Channel(""));
        }

        [Test]
        public void ChannelThrowsOnWhitespaceTopicTest()
        {
            var socket = Socket;
            Assert.Throws<ArgumentException>(() => socket.Channel("   "));
        }

        #endregion

        #region Reconnect Behavior Tests

        [Test]
        public void SocketWithNullReconnectAfterDoesNotAutoReconnectTest()
        {
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                ReconnectAfter = null
            };
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                new MockWebsocketFactory(),
                options
            );

            // Should not throw, reconnect timer is not created
            Assert.DoesNotThrow(() => socket.Disconnect());
        }

        #endregion
    }

    #region Extended Mock Classes for Testing

    /// <summary>
    /// Extended mock websocket that allows simulating callbacks
    /// </summary>
    public sealed class MockWebsocketAdapterWithCallbacks : IWebsocket
    {
        private readonly WebsocketConfiguration _config;

        public readonly List<string> CallSend = new();
        public int CallCloseCount;
        public int CallConnectCount;
        public ushort? LastCloseCode;
        public string? LastCloseReason;
        public WebsocketState MockState = WebsocketState.Closed;

        public MockWebsocketAdapterWithCallbacks(WebsocketConfiguration config)
        {
            _config = config;
        }

        public WebsocketState State => MockState;

        public void Connect()
        {
            CallConnectCount += 1;
            MockState = WebsocketState.Open;
            _config.onOpenCallback?.Invoke(this);
        }

        public void Send(string message)
        {
            CallSend.Add(message);
        }

        public void Close(ushort? code = null, string? message = null)
        {
            CallCloseCount += 1;
            LastCloseCode = code;
            LastCloseReason = message;
            MockState = WebsocketState.Closed;
            _config.onCloseCallback?.Invoke(this, code ?? 0, message ?? "");
        }

        public void SimulateError(string error)
        {
            _config.onErrorCallback?.Invoke(this, error);
        }

        public void SimulateClose(ushort code, string reason)
        {
            MockState = WebsocketState.Closed;
            _config.onCloseCallback?.Invoke(this, code, reason);
        }

        public void SimulateMessage(string message)
        {
            _config.onMessageCallback?.Invoke(this, message);
        }
    }

    public sealed class MockWebsocketFactoryWithCallbackTracking : IWebsocketFactory
    {
        public MockWebsocketAdapterWithCallbacks? LastCreatedWebsocket { get; private set; }

        public IWebsocket Build(WebsocketConfiguration config)
        {
            LastCreatedWebsocket = new MockWebsocketAdapterWithCallbacks(config);
            return LastCreatedWebsocket;
        }
    }

    #endregion
}
