using System;
using System.Collections.Generic;
using System.Reflection;
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

        #region Endpoint URL Tests

        [Test]
        public void ConnectEscapesConnectionParameterKeysAndValuesTest()
        {
            const string key = "auth token";
            const string token = "abc+def==&x#y";
            var factory = new CapturingWebsocketFactory();
            var socket = new Socket(
                "ws://localhost:1234",
                new Dictionary<string, string>
                {
                    { key, token }
                },
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );

            socket.Connect();

            var endpoint = factory.LastUri
                ?? throw new AssertionException("Expected the websocket factory to capture a URI.");
            var query = ParseQuery(endpoint);
            var expectedPair =
                $"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(token)}";

            Assert.Multiple(() =>
            {
                Assert.That(query, Does.ContainKey(key));
                Assert.That(query.GetValueOrDefault(key), Is.EqualTo(token));
                Assert.That(endpoint.Query, Does.Contain(expectedPair));
                Assert.That(endpoint.AbsoluteUri, Does.Contain(expectedPair));
                Assert.That(endpoint.Fragment, Is.Empty);
            });
        }

        [Test]
        public void ConnectDoesNotAddVsnToCallerParametersTest()
        {
            var callerParams = new Dictionary<string, string>
            {
                { "token", "secret" }
            };
            var socket = new Socket(
                "ws://localhost:1234",
                callerParams,
                new MockWebsocketFactory(),
                new Socket.Options(new JsonMessageSerializer())
            );

            socket.Connect();

            Assert.That(callerParams, Does.Not.ContainKey("vsn"));
        }

        [Test]
        public void ConstructorSnapshotsConnectionParametersTest()
        {
            var callerParams = new Dictionary<string, string>
            {
                { "token", "initial" }
            };
            var factory = new CapturingWebsocketFactory();
            var socket = new Socket(
                "ws://localhost:1234",
                callerParams,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );

            callerParams["token"] = "changed";
            callerParams["added-later"] = "unexpected";
            socket.Connect();

            var endpoint = factory.LastUri
                ?? throw new AssertionException("Expected the websocket factory to capture a URI.");
            var query = ParseQuery(endpoint);

            Assert.Multiple(() =>
            {
                Assert.That(query.GetValueOrDefault("token"), Is.EqualTo("initial"));
                Assert.That(query, Does.Not.ContainKey("added-later"));
            });
        }

        [Test]
        public void ConnectTreatsNullConnectionParameterValueAsEmptyTest()
        {
            var factory = new CapturingWebsocketFactory();
            var socket = new Socket(
                "ws://localhost:1234",
                new Dictionary<string, string>
                {
                    { "token", null! }
                },
                factory,
                new Socket.Options(new JsonMessageSerializer())
                {
                    ReconnectAfter = null
                }
            );

            socket.Connect();

            var endpoint = factory.LastUri;
            Assert.That(
                endpoint,
                Is.Not.Null,
                "A null parameter value must not prevent the websocket factory from being invoked."
            );
            if (endpoint == null)
            {
                return;
            }

            var query = ParseQuery(endpoint);
            Assert.Multiple(() =>
            {
                Assert.That(query.GetValueOrDefault("token"), Is.Empty);
                Assert.That(endpoint.Query, Does.Contain("token="));
            });
        }

        [Test]
        public void OptionsVsnOverridesCallerSuppliedVsnTest()
        {
            var callerParams = new Dictionary<string, string>
            {
                { "vsn", "caller-version" }
            };
            var factory = new CapturingWebsocketFactory();
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                Vsn = "options-version"
            };
            var socket = new Socket(
                "ws://localhost:1234",
                callerParams,
                factory,
                options
            );

            socket.Connect();

            var endpoint = factory.LastUri
                ?? throw new AssertionException("Expected the websocket factory to capture a URI.");
            var query = ParseQuery(endpoint);

            Assert.Multiple(() =>
            {
                Assert.That(query.GetValueOrDefault("vsn"), Is.EqualTo("options-version"));
                Assert.That(callerParams["vsn"], Is.EqualTo("caller-version"));
            });
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
        public void ChannelDispatchExceptionDoesNotStopOtherChannelsOrLaterMessagesTest()
        {
            var logger = new CapturingLogger();
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
                {
                    HeartbeatInterval = null,
                    Logger = logger,
                    ReconnectAfter = null,
                    RejoinAfter = null
                }
            );
            socket.Connect();
            var connection = factory.LastCreatedWebsocket!;
            var healthyChannel = socket.Channel("shared:topic");
            var throwingChannel = new ThrowingOnMessageChannel(
                "shared:topic",
                null,
                socket
            );
            AddChannelFirst(socket, throwingChannel);
            var channelMessageCount = 0;
            var socketMessageCount = 0;
            healthyChannel.On("custom_event", _ => channelMessageCount++);
            socket.OnMessage += _ => socketMessageCount++;
            var rawMessage = BuildPhxMessage(
                null,
                "wire-ref",
                "shared:topic",
                "custom_event"
            );

            Assert.DoesNotThrow(() =>
            {
                connection.SimulateMessage(rawMessage);
                connection.SimulateMessage(rawMessage);
            });

            Assert.Multiple(() =>
            {
                Assert.That(channelMessageCount, Is.EqualTo(2));
                Assert.That(socketMessageCount, Is.EqualTo(2));
                Assert.That(
                    logger.Messages.FindAll(message =>
                        message.Contains(
                            "Channel dispatch failed for topic 'shared:topic', "
                            + "event 'custom_event', ref 'wire-ref'"
                        )),
                    Has.Count.EqualTo(2)
                );
            });
        }

        [Test]
        public void ChannelErrorDispatchExceptionDoesNotEscapeCloseOrBlockReconnectTest()
        {
            var reconnectDelay = TimeSpan.FromMilliseconds(25);
            var executor = new TrackingDelayedExecutor();
            var logger = new CapturingLogger();
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
                {
                    DelayedExecutor = executor,
                    HeartbeatInterval = null,
                    Logger = logger,
                    ReconnectAfter = _ => reconnectDelay,
                    RejoinAfter = null
                }
            );
            socket.Connect();
            var healthyChannel = socket.Channel("healthy:topic");
            var payloadAssumingChannel = new PayloadAssumingOnMessageChannel(
                "fragile:topic",
                null,
                socket
            );
            AddChannelFirst(socket, payloadAssumingChannel);
            healthyChannel.Join().Trigger(ReplyStatus.Ok);
            payloadAssumingChannel.Join().Trigger(ReplyStatus.Ok);
            var closeCalled = false;
            socket.OnClose += (_, _) => closeCalled = true;

            Assert.DoesNotThrow(() =>
                factory.LastCreatedWebsocket!.SimulateClose(
                    1_006,
                    "connection lost"
                ));

            Assert.Multiple(() =>
            {
                Assert.That(healthyChannel.State, Is.EqualTo(ChannelState.Errored));
                Assert.That(closeCalled, Is.True);
                Assert.That(
                    executor.Executions.FindAll(execution =>
                        execution.Delay == reconnectDelay && !execution.IsCancelled),
                    Has.Count.EqualTo(1)
                );
                Assert.That(
                    logger.Messages,
                    Has.Some.Contains(
                        "Channel dispatch failed for topic 'fragile:topic', "
                        + "event 'phx_error', ref 'null'"
                    )
                );
            });
        }

        [Test]
        public void ChannelErrorDispatchExceptionDoesNotEscapeHeartbeatTimeoutTest()
        {
            var heartbeatInterval = TimeSpan.FromSeconds(30);
            var reconnectDelay = TimeSpan.FromMilliseconds(25);
            var executor = new TrackingDelayedExecutor();
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
                {
                    DelayedExecutor = executor,
                    HeartbeatInterval = heartbeatInterval,
                    ReconnectAfter = _ => reconnectDelay,
                    RejoinAfter = null
                }
            );
            socket.Connect();
            var connection = factory.LastCreatedWebsocket!;
            var healthyChannel = socket.Channel("healthy:topic");
            var payloadAssumingChannel = new PayloadAssumingOnMessageChannel(
                "fragile:topic",
                null,
                socket
            );
            AddChannelFirst(socket, payloadAssumingChannel);
            healthyChannel.Join().Trigger(ReplyStatus.Ok);
            payloadAssumingChannel.Join().Trigger(ReplyStatus.Ok);
            var heartbeatSend = executor.Executions.Find(execution =>
                execution.Delay == heartbeatInterval && !execution.IsCancelled);
            Assert.That(heartbeatSend, Is.Not.Null);
            heartbeatSend!.Execute();
            var heartbeatTimeout = executor.Executions.FindLast(execution =>
                execution.Delay == heartbeatInterval && !execution.IsCancelled);
            Assert.That(heartbeatTimeout, Is.Not.Null);

            Assert.DoesNotThrow(heartbeatTimeout!.Execute);

            Assert.Multiple(() =>
            {
                Assert.That(healthyChannel.State, Is.EqualTo(ChannelState.Errored));
                Assert.That(connection.CallCloseCount, Is.EqualTo(1));
                Assert.That(
                    executor.Executions.FindAll(execution =>
                        execution.Delay == reconnectDelay && !execution.IsCancelled),
                    Has.Count.EqualTo(1)
                );
            });
        }

        [Test]
        public void MalformedV2FrameLogsDescriptiveElementCountTest()
        {
            var logger = new CapturingLogger();
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
                {
                    HeartbeatInterval = null,
                    Logger = logger,
                    ReconnectAfter = null
                }
            );
            socket.Connect();

            factory.LastCreatedWebsocket!.SimulateMessage(
                @"[null,""1"",""test"",""custom_event""]"
            );

            Assert.That(
                logger.Messages,
                Has.Some.Contains(
                    "Failed to deserialize message: expected 5-element Phoenix V2 frame, got 4"
                )
            );
        }

        [Test]
        public void StaleOpenDoesNotInvokeSocketDelegateTest()
        {
            var (socket, _, staleConnection, _) = CreateSocketWithReplacement();
            var openCalled = false;
            socket.OnOpen += () => openCalled = true;

            staleConnection.Connect();

            Assert.That(openCalled, Is.False);
        }

        [Test]
        public void StaleErrorDoesNotAffectCurrentChannelsOrSocketDelegateTest()
        {
            var (socket, _, staleConnection, _) = CreateSocketWithReplacement();
            var channel = socket.Channel("current:topic");
            channel.Join().Trigger(ReplyStatus.Ok);
            var errorCalled = false;
            socket.OnError += _ => errorCalled = true;

            staleConnection.SimulateError("late error");

            Assert.Multiple(() =>
            {
                Assert.That(errorCalled, Is.False);
                Assert.That(channel.State, Is.EqualTo(ChannelState.Joined));
            });
        }

        [Test]
        public void StaleCloseDoesNotAffectCurrentChannelsOrScheduleReconnectTest()
        {
            var executor = new TrackingDelayedExecutor();
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                DelayedExecutor = executor,
                HeartbeatInterval = null,
                ReconnectAfter = _ => TimeSpan.FromMilliseconds(10),
                RejoinAfter = null
            };
            var (socket, _, staleConnection, _) = CreateSocketWithReplacement(options);
            var channel = socket.Channel("current:topic");
            channel.Join().Trigger(ReplyStatus.Ok);
            var closeCalled = false;
            socket.OnClose += (_, _) => closeCalled = true;

            staleConnection.SimulateClose(1_006, "late close");

            Assert.Multiple(() =>
            {
                Assert.That(closeCalled, Is.False);
                Assert.That(channel.State, Is.EqualTo(ChannelState.Joined));
                Assert.That(executor.PendingCount, Is.Zero);
            });
        }

        [Test]
        public void StaleMessageDoesNotReachCurrentChannelsOrSocketDelegateTest()
        {
            var (socket, _, staleConnection, currentConnection) =
                CreateSocketWithReplacement();
            var channel = socket.Channel("current:topic");
            var channelMessageCount = 0;
            var socketMessageCount = 0;
            channel.On("custom_event", _ => channelMessageCount++);
            socket.OnMessage += _ => socketMessageCount++;
            var rawMessage = BuildPhxMessage(
                null,
                null,
                "current:topic",
                "custom_event"
            );

            staleConnection.SimulateMessage(rawMessage);
            currentConnection.SimulateMessage(rawMessage);

            Assert.Multiple(() =>
            {
                Assert.That(channelMessageCount, Is.EqualTo(1));
                Assert.That(socketMessageCount, Is.EqualTo(1));
            });
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

        private static Dictionary<string, string> ParseQuery(Uri uri)
        {
            var parsed = new Dictionary<string, string>();
            var query = uri.Query.TrimStart('?');
            if (query.Length == 0)
            {
                return parsed;
            }

            foreach (var pair in query.Split('&'))
            {
                var separatorIndex = pair.IndexOf('=');
                var key = separatorIndex >= 0
                    ? pair.Substring(0, separatorIndex)
                    : pair;
                var value = separatorIndex >= 0
                    ? pair.Substring(separatorIndex + 1)
                    : string.Empty;
                parsed[DecodeQueryComponent(key)] = DecodeQueryComponent(value);
            }

            return parsed;
        }

        private static string DecodeQueryComponent(string value)
        {
            return Uri.UnescapeDataString(value.Replace("+", " "));
        }

        private static void AddChannelFirst(Socket socket, Channel channel)
        {
            var channels = (List<Channel>)typeof(Socket)
                .GetField("_channels", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(socket)!;
            channels.Insert(0, channel);
        }

        private static (
            Socket Socket,
            MockWebsocketFactoryWithCallbackTracking Factory,
            MockWebsocketAdapterWithCallbacks StaleConnection,
            MockWebsocketAdapterWithCallbacks CurrentConnection
        ) CreateSocketWithReplacement(Socket.Options? options = null)
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                options ?? new Socket.Options(new JsonMessageSerializer())
                {
                    HeartbeatInterval = null,
                    ReconnectAfter = null,
                    RejoinAfter = null
                }
            );
            socket.Connect();
            var staleConnection = factory.LastCreatedWebsocket!;
            staleConnection.SimulateClose(1_000, "replaced by test");
            socket.Connect();
            var currentConnection = factory.LastCreatedWebsocket!;
            Assert.That(currentConnection, Is.Not.SameAs(staleConnection));

            return (socket, factory, staleConnection, currentConnection);
        }

        private sealed class CapturingLogger : ILogger
        {
            public List<string> Messages { get; } = new List<string>();

            public void Log(LogLevel level, string source, string message)
            {
                Messages.Add($"{source}: {message}");
            }
        }

        private sealed class ThrowingOnMessageChannel : Channel
        {
            public ThrowingOnMessageChannel(
                string topic,
                Dictionary<string, object>? @params,
                Socket socket
            )
                : base(topic, @params, socket)
            {
            }

            public override IJsonBox? OnMessage(Message message)
            {
                throw new ApplicationException("throwing channel hook");
            }
        }

        private sealed class PayloadAssumingOnMessageChannel : Channel
        {
            public PayloadAssumingOnMessageChannel(
                string topic,
                Dictionary<string, object>? @params,
                Socket socket
            )
                : base(topic, @params, socket)
            {
            }

            public override IJsonBox? OnMessage(Message message)
            {
                message.Payload!.Unbox<object>();
                return message.Payload;
            }
        }

        private sealed class CapturingWebsocketFactory : IWebsocketFactory
        {
            public Uri? LastUri { get; private set; }

            public IWebsocket Build(WebsocketConfiguration config)
            {
                LastUri = config.uri;
                return new MockWebsocketAdapter(config);
            }
        }

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
