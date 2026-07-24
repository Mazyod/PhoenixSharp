using System;
using System.Collections.Generic;
using NUnit.Framework;
using Phoenix;
using PhoenixTests.TestDoubles;
using PhoenixTests.WebSocketImpl;

namespace PhoenixTests
{
    [TestFixture, Category("Unit")]
    public class SocketConnectionTests : PhoenixTestBase
    {
        private static Socket CreateSocket() =>
            new(
                "ws://localhost:1234",
                null,
                new MockWebsocketFactory(),
                new Socket.Options(new JsonMessageSerializer())
            );

        private static Socket CreateSocketWithParams(Dictionary<string, string>? @params = null) =>
            new(
                "ws://localhost:1234",
                @params,
                new MockWebsocketFactory(),
                new Socket.Options(new JsonMessageSerializer())
            );

        #region Socket Options Tests

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

        #endregion

        #region Send Buffer Tests

        [Test]
        public void BuffersDataWhenNotConnectedTest()
        {
            var socket = CreateSocket();
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

        /// <summary>
        /// Test Github Issue #19:
        /// phx_join never sent if socket is not open by the time Join is called.
        /// </summary>
        [Test]
        public void FlushSendBufferTest()
        {
            var socket = CreateSocket();
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

        [Test]
        public void FlushSendBufferRebuffersRemainingMessagesWhenConnectionDisappearsTest()
        {
            var factory = new ControllableWebsocketFactory();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );
            factory.DisconnectOnFirstSend = () =>
            {
                socket.Disconnect();
                socket.Push(new Message("topic", "fourth"));
            };

            socket.Push(new Message("topic", "first"));
            socket.Push(new Message("topic", "second"));
            socket.Push(new Message("topic", "third"));

            socket.Connect();
            var firstConnection = factory.Connections[0];

            Assert.DoesNotThrow(firstConnection.Open);
            Assert.That(firstConnection.CallSend, Has.Count.EqualTo(1));
            Assert.That(firstConnection.CallSend[0], Does.Contain("\"first\""));
            Assert.That(socket.SendBuffer, Has.Count.EqualTo(3));

            socket.Connect();
            var secondConnection = factory.Connections[1];
            secondConnection.Open();

            Assert.That(secondConnection.CallSend, Has.Count.EqualTo(3));
            Assert.That(secondConnection.CallSend[0], Does.Contain("\"second\""));
            Assert.That(secondConnection.CallSend[1], Does.Contain("\"third\""));
            Assert.That(secondConnection.CallSend[2], Does.Contain("\"fourth\""));
            Assert.That(socket.SendBuffer, Is.Empty);
        }

        [Test]
        public void FlushSendBufferRerunsWhenBufferingIsRequestedDuringFlushTest()
        {
            var factory = new ControllableWebsocketFactory();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );
            factory.DisconnectOnFirstSend = () =>
            {
                socket.Push(new Message("topic", "second"));
                factory.Connections[0].MockState = WebsocketState.Open;
            };

            socket.Push(new Message("topic", "first"));
            socket.Connect();
            var connection = factory.Connections[0];

            connection.Open();

            Assert.That(connection.CallSend, Has.Count.EqualTo(2));
            Assert.That(connection.CallSend[0], Does.Contain("\"first\""));
            Assert.That(connection.CallSend[1], Does.Contain("\"second\""));
            Assert.That(socket.SendBuffer, Is.Empty);
        }

        [Test]
        public void BufferSendFlushesWhenConnectionOpensBetweenStateChecksTest()
        {
            var factory = new ControllableWebsocketFactory();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );
            socket.Connect();
            var connection = factory.Connections[0];
            connection.Open();
            connection.ReportClosedOnNextStateRead();

            socket.Push(new Message("topic", "raced"));

            Assert.That(connection.CallSend, Has.Count.EqualTo(1));
            Assert.That(connection.CallSend[0], Does.Contain("\"raced\""));
            Assert.That(socket.SendBuffer, Is.Empty);
        }

        #endregion

        #region Connection State Tests

        [Test]
        public void StateIsNullBeforeConnectTest()
        {
            var socket = CreateSocket();
            Assert.IsNull(socket.State);
        }

        [Test]
        public void StateIsOpenAfterConnectTest()
        {
            var socket = CreateSocket();
            socket.Connect();
            Assert.AreEqual(WebsocketState.Open, socket.State);
        }

        [Test]
        public void ConnectDoesNothingWhenAlreadyConnectedTest()
        {
            var socket = CreateSocket();
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
            var socket = CreateSocket();
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

        #region Callback Tests

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
            var socket = CreateSocket();
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
            conn.SimulateMessage(BuildPhxMessage(null, "1", "test", "event"));

            Assert.IsNotNull(receivedMessage);
            Assert.AreEqual("test", receivedMessage?.Topic);
            Assert.AreEqual("event", receivedMessage?.Event);
        }

        [Test]
        public void OnErrorTriggersAppropriateCallbacksTest()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                HeartbeatInterval = null,
                ReconnectAfter = null
            };

            var socket = new Socket("ws://localhost:1234", null, factory, options);

            string? receivedError = null;
            socket.OnError += error => receivedError = error;

            socket.Connect();
            var conn = factory.LastCreatedWebsocket;
            Assert.IsNotNull(conn);

            conn.SimulateError("Test error");

            Assert.AreEqual("Test error", receivedError);
        }

        [Test]
        public void OnErrorTriggersChannelErrorTest()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var mockExecutor = new TrackingDelayedExecutor();
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                DelayedExecutor = mockExecutor,
                HeartbeatInterval = null,
                ReconnectAfter = null
            };

            var socket = new Socket("ws://localhost:1234", null, factory, options);
            socket.Connect();

            var conn = factory.LastCreatedWebsocket;
            Assert.IsNotNull(conn);

            var channel = socket.Channel("test:topic");
            channel.Join();

            // Simulate join success
            conn.SimulateMessage(BuildJoinOkReply("1", "test:topic"));
            Assert.AreEqual(ChannelState.Joined, channel.State);

            // Simulate error
            conn.SimulateError("Connection error");

            // Channel should be in errored state
            Assert.AreEqual(ChannelState.Errored, channel.State);
        }

        #endregion

        #region Multiple Channel Management Tests

        [Test]
        public void MultipleChannelsCanBeCreatedTest()
        {
            var socket = CreateSocket();

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
            var socket = CreateSocket();

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
            conn.SimulateMessage(BuildPhxMessage("1", "5", "topic1", "custom_event", "{\"data\":\"for topic1\"}"));

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
            var socket = CreateSocket();
            Assert.Throws<ArgumentNullException>(() => socket.Channel(null!));
        }

        [Test]
        public void ChannelThrowsOnEmptyTopicTest()
        {
            var socket = CreateSocket();
            Assert.Throws<ArgumentException>(() => socket.Channel(""));
        }

        [Test]
        public void ChannelThrowsOnWhitespaceTopicTest()
        {
            var socket = CreateSocket();
            Assert.Throws<ArgumentException>(() => socket.Channel("   "));
        }

        #endregion

        private sealed class ControllableWebsocketFactory : IWebsocketFactory
        {
            public readonly List<ControllableWebsocket> Connections =
                new List<ControllableWebsocket>();

            public Action? DisconnectOnFirstSend { get; set; }

            public IWebsocket Build(WebsocketConfiguration config)
            {
                var disconnectOnFirstSend = Connections.Count == 0
                    ? DisconnectOnFirstSend
                    : null;
                var websocket = new ControllableWebsocket(config, disconnectOnFirstSend);
                Connections.Add(websocket);
                return websocket;
            }
        }

        private sealed class ControllableWebsocket : IWebsocket
        {
            private readonly WebsocketConfiguration _config;
            private readonly Action? _disconnectOnFirstSend;
            private bool _didDisconnectOnSend;
            private bool _reportClosedOnNextStateRead;

            public readonly List<string> CallSend = new List<string>();
            public WebsocketState MockState = WebsocketState.Closed;

            public ControllableWebsocket(
                WebsocketConfiguration config,
                Action? disconnectOnFirstSend
            )
            {
                _config = config;
                _disconnectOnFirstSend = disconnectOnFirstSend;
            }

            public WebsocketState State
            {
                get
                {
                    if (_reportClosedOnNextStateRead)
                    {
                        _reportClosedOnNextStateRead = false;
                        return WebsocketState.Closed;
                    }

                    return MockState;
                }
            }

            public void ReportClosedOnNextStateRead()
            {
                _reportClosedOnNextStateRead = true;
            }

            public void Connect()
            {
                MockState = WebsocketState.Connecting;
            }

            public void Open()
            {
                MockState = WebsocketState.Open;
                _config.onOpenCallback(this);
            }

            public void Send(string message)
            {
                CallSend.Add(message);
                if (_disconnectOnFirstSend != null && !_didDisconnectOnSend)
                {
                    _didDisconnectOnSend = true;
                    MockState = WebsocketState.Closed;
                    _disconnectOnFirstSend();
                }
            }

            public void Close(ushort? code = null, string? reason = null)
            {
                MockState = WebsocketState.Closed;
                _config.onCloseCallback(this, code ?? 0, reason ?? "");
            }
        }
    }
}
