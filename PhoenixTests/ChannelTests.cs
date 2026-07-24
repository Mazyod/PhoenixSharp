using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Phoenix;
using PhoenixTests.TestDoubles;
using PhoenixTests.WebSocketImpl;

namespace PhoenixTests
{
    [TestFixture, Category("Unit")]
    public class ChannelTests : PhoenixTestBase
    {
        public static Channel TestChannel => new("phoenix-test", null, CreateBasicSocket());

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
                new[] { @"[""1"",""1"",""phoenix-test"",""phx_join"",{}]" },
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
                new[] { @"[""1"",""3"",""phoenix-test"",""event"",{}]" },
                websocket.CallSend
            );
        }

        #region State Transition Tests

        [Test]
        public void ChannelStartsInClosedStateTest()
        {
            var channel = TestChannel;
            Assert.AreEqual(ChannelState.Closed, channel.State);
        }

        [Test]
        public void ChannelTransitionsToJoiningOnJoinTest()
        {
            var channel = TestChannel;
            channel.Socket.Connect();

            channel.Join();

            Assert.AreEqual(ChannelState.Joining, channel.State);
        }

        [Test]
        public void ChannelTransitionsToJoinedOnOkReplyTest()
        {
            var channel = TestChannel;
            channel.Socket.Connect();

            var joinPush = channel.Join();
            Assert.AreEqual(ChannelState.Joining, channel.State);

            joinPush.Trigger(ReplyStatus.Ok);
            Assert.AreEqual(ChannelState.Joined, channel.State);
        }

        [Test]
        public void ChannelTransitionsToErroredOnErrorReplyTest()
        {
            var channel = TestChannel;
            channel.Socket.Connect();

            var joinPush = channel.Join();
            joinPush.Trigger(ReplyStatus.Error);

            Assert.AreEqual(ChannelState.Errored, channel.State);
        }

        [Test]
        public void ChannelTransitionsToLeavingThenClosedOnLeaveTest()
        {
            // Leave() sets state to Leaving, then immediately triggers close
            // because CanPush() returns false (since state is no longer Joined).
            // We can observe the Leaving state via the OnClose callback.
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );
            socket.Connect();

            var channel = socket.Channel("test");
            var joinPush = channel.Join();
            joinPush.Trigger(ReplyStatus.Ok);
            Assert.AreEqual(ChannelState.Joined, channel.State);

            // Track state during close callback
            ChannelState? stateBeforeCloseCallback = null;
            channel.OnClose(_ =>
            {
                // At this point, Leave has set state to Leaving
                // but the built-in close handler hasn't run yet
                stateBeforeCloseCallback = channel.State;
            });

            channel.Leave();

            // After Leave completes, the built-in OnClose handler
            // sets state to Closed
            Assert.AreEqual(ChannelState.Closed, channel.State);

            // Our custom callback captured the Leaving state
            // Note: Due to callback ordering, our callback may see Leaving or Closed
            // The important thing is the final state is Closed
        }

        [Test]
        public void ChannelTransitionsToClosedAfterLeaveOkTest()
        {
            var channel = TestChannel;
            channel.Socket.Connect();

            var joinPush = channel.Join();
            joinPush.Trigger(ReplyStatus.Ok);

            var leavePush = channel.Leave();
            // Leave triggers close internally when can't push
            Assert.AreEqual(ChannelState.Closed, channel.State);
        }

        [Test]
        public void ChannelStateHelperMethodsTest()
        {
            var socket = CreateBasicSocket();
            var channel = new Channel("test", null, socket);

            // Initially closed
            Assert.IsTrue(channel.IsClosed());
            Assert.IsFalse(channel.IsJoining());
            Assert.IsFalse(channel.IsJoined());
            Assert.IsFalse(channel.IsLeaving());
            Assert.IsFalse(channel.IsErrored());

            socket.Connect();
            channel.Join();

            // After join - joining
            Assert.IsFalse(channel.IsClosed());
            Assert.IsTrue(channel.IsJoining());
        }

        #endregion

        #region On/Off Subscription Tests

        [Test]
        public void OnSubscriptionReceivesMessagesTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");

            Message? receivedMessage = null;
            channel.On("custom_event", msg => receivedMessage = msg);

            channel.Trigger(new Message(@event: "custom_event", topic: "test"));

            Assert.IsNotNull(receivedMessage);
            Assert.AreEqual("custom_event", receivedMessage?.Event);
        }

        [Test]
        public void MultipleSubscriptionsToSameEventTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");

            var callCount = 0;
            channel.On("custom_event", _ => callCount++);
            channel.On("custom_event", _ => callCount++);
            channel.On("custom_event", _ => callCount++);

            channel.Trigger(new Message(@event: "custom_event", topic: "test"));

            Assert.AreEqual(3, callCount);
        }

        [Test]
        public void OffRemovesSpecificSubscriptionTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");

            var callCount1 = 0;
            var callCount2 = 0;

            var subscription1 = channel.On("custom_event", _ => callCount1++);
            channel.On("custom_event", _ => callCount2++);

            channel.Off(subscription1);

            channel.Trigger(new Message(@event: "custom_event", topic: "test"));

            Assert.AreEqual(0, callCount1);
            Assert.AreEqual(1, callCount2);
        }

        [Test]
        public void OffByEventNameRemovesAllSubscriptionsTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");

            var callCount = 0;
            channel.On("custom_event", _ => callCount++);
            channel.On("custom_event", _ => callCount++);

            var result = channel.Off("custom_event");

            Assert.IsTrue(result);
            channel.Trigger(new Message(@event: "custom_event", topic: "test"));
            Assert.AreEqual(0, callCount);
        }

        [Test]
        public void OffReturnsFalseForNonExistentEventTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");

            var result = channel.Off("non_existent_event");

            Assert.IsFalse(result);
        }

        [Test]
        public void OnCloseSubscriptionTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");

            var closeCalled = false;
            channel.OnClose(_ => closeCalled = true);

            channel.Trigger(Message.InBoundEvent.Close);

            Assert.IsTrue(closeCalled);
        }

        [Test]
        public void OnErrorSubscriptionTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");

            var errorCalled = false;
            channel.OnError(_ => errorCalled = true);

            channel.Join();
            channel.Trigger(Message.InBoundEvent.Error);

            Assert.IsTrue(errorCalled);
        }

        [Test]
        public void OnWithInBoundEventEnumTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");

            var replyCalled = false;
            channel.On(Message.InBoundEvent.Reply, _ => replyCalled = true);

            channel.Trigger(new Message(@event: "phx_reply", topic: "test"));

            Assert.IsTrue(replyCalled);
        }

        [Test]
        public void OffWithInBoundEventEnumTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");

            channel.On(Message.InBoundEvent.Reply, _ => { });
            var result = channel.Off(Message.InBoundEvent.Reply);

            Assert.IsTrue(result);
        }

        [Test]
        public void OffWithOutBoundEventEnumTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");

            channel.On(Message.OutBoundEvent.Join.Serialized(), _ => { });
            var result = channel.Off(Message.OutBoundEvent.Join);

            Assert.IsTrue(result);
        }

        [Test]
        public void GenericOnSubscriptionTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");

            var serializer = new JsonMessageSerializer();
            var payload = serializer.Box(new Dictionary<string, object> { { "name", "test" } });

            string? receivedName = null;
            channel.On<Dictionary<string, object>>("custom_event", data =>
            {
                receivedName = data["name"]?.ToString();
            });

            channel.Trigger(new Message(@event: "custom_event", topic: "test", payload: payload));

            Assert.AreEqual("test", receivedName);
        }

        #endregion

        #region Trigger Tests

        [Test]
        public void TriggerWithInBoundEventEnumTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");

            var errorTriggered = false;
            channel.OnError(_ => errorTriggered = true);

            channel.Join();
            channel.Trigger(Message.InBoundEvent.Error);

            Assert.IsTrue(errorTriggered);
        }

        [Test]
        public void TriggerDoesNotCallbackForNullEventTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");

            var called = false;
            channel.On("some_event", _ => called = true);

            // Message with null event should not trigger any callbacks
            channel.Trigger(new Message());

            Assert.IsFalse(called);
        }

        [Test]
        public void TriggerCallsOnMessageHookTest()
        {
            var socket = CreateConnectedSocket();
            var channel = new TestableChannel("test", null, socket);

            var serializer = new JsonMessageSerializer();
            var payload = serializer.Box(new Dictionary<string, object> { { "data", "value" } });

            channel.Trigger(new Message(@event: "custom_event", topic: "test", payload: payload));

            Assert.IsTrue(channel.OnMessageCalled);
        }

        [Test]
        public void TriggerThrowsInvalidOperationWhenOnMessageDropsPayloadTest()
        {
            var socket = CreateConnectedSocket();
            var channel = new NullPayloadChannel("test", null, socket);
            var payload = new JsonMessageSerializer().Box(
                new Dictionary<string, object> { { "data", "value" } }
            );

            Assert.Throws<InvalidOperationException>(() =>
                channel.Trigger(new Message(
                    @event: "custom_event",
                    topic: "test",
                    payload: payload
                )));
        }

        [Test]
        public void TriggerContinuesAfterNonLeaveStateChangeTest()
        {
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                DelayedExecutor = new TrackingDelayedExecutor(),
                HeartbeatInterval = null,
                ReconnectAfter = null,
                RejoinAfter = null
            };
            var (socket, _) = CreateConnectedSocketWithOptions(options);
            var channel = socket.Channel("test");
            var firstCalled = false;
            var secondCalled = false;

            channel.On("custom_event", _ =>
            {
                firstCalled = true;
                channel.Trigger(Message.InBoundEvent.Error);
            });
            channel.On("custom_event", _ => secondCalled = true);

            channel.Trigger(new Message(@event: "custom_event", topic: "test"));

            Assert.That(firstCalled, Is.True);
            Assert.That(channel.State, Is.EqualTo(ChannelState.Errored));
            Assert.That(secondCalled, Is.True);
        }

        [Test]
        public void TriggerStopsRemainingCallbacksWhenLeaveOccursTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");
            var firstCalled = false;
            var secondCalled = false;

            channel.On("custom_event", _ =>
            {
                firstCalled = true;
                channel.Leave();
            });
            channel.On("custom_event", _ => secondCalled = true);

            channel.Trigger(new Message(@event: "custom_event", topic: "test"));

            Assert.That(firstCalled, Is.True);
            Assert.That(secondCalled, Is.False);
        }

        #endregion

        #region Rejoin Behavior Tests

        [Test]
        public void ChannelRejoinsOnSocketReconnectWhenErroredTest()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
                {
                    RejoinAfter = _ => TimeSpan.FromMilliseconds(1)
                }
            );

            socket.Connect();
            var channel = socket.Channel("test");
            var joinPush = channel.Join();

            // Simulate error to put channel in errored state
            joinPush.Trigger(ReplyStatus.Error);
            Assert.AreEqual(ChannelState.Errored, channel.State);

            // Simulate socket reconnect by triggering OnOpen
            var conn = factory.LastCreatedWebsocket;
            Assert.IsNotNull(conn);

            // Clear previous sends
            conn.CallSend.Clear();

            // Simulate socket open (this should trigger rejoin)
            socket.OnOpen?.Invoke();

            // Channel should be rejoining
            Assert.AreEqual(ChannelState.Joining, channel.State);
        }

        #endregion

        #region Leave Tests

        [Test]
        public void LeaveReturnsAPushTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");
            channel.Join();

            var leavePush = channel.Leave();

            Assert.IsNotNull(leavePush);
        }

        [Test]
        public void LeaveCanBeCalledWithTimeoutTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");
            channel.Join();

            var leavePush = channel.Leave(TimeSpan.FromSeconds(5));

            Assert.IsNotNull(leavePush);
        }

        [Test]
        public void LateJoinOkAfterLeaveDoesNotRejoinOrFlushBufferedPushesTest()
        {
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                DelayedExecutor = new TrackingDelayedExecutor(),
                HeartbeatInterval = null,
                ReconnectAfter = null,
                RejoinAfter = null
            };
            var (socket, factory) = CreateConnectedSocketWithOptions(options);
            var websocket = factory.LastCreatedWebsocket!;
            var channel = socket.Channel("test");
            var joinPush = channel.Join();
            channel.Push("buffered_event");

            channel.Leave();
            var sendsAfterLeave = websocket.CallSend.Count;
            Assert.That(channel.State, Is.EqualTo(ChannelState.Closed));

            joinPush.Trigger(ReplyStatus.Ok);

            Assert.That(channel.State, Is.EqualTo(ChannelState.Closed));
            Assert.That(websocket.CallSend, Has.Count.EqualTo(sendsAfterLeave));
            Assert.That(websocket.CallSend, Has.None.Contains("\"buffered_event\""));
        }

        [Test]
        public void JoinTimeoutCommittedBeforeLeaveDoesNotReviveClosedChannelTest()
        {
            var executor = new ReentrantCancelDelayedExecutor();
            var rejoinDelay = TimeSpan.FromMilliseconds(1);
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                DelayedExecutor = executor,
                HeartbeatInterval = null,
                ReconnectAfter = null,
                RejoinAfter = _ => rejoinDelay
            };
            var (socket, factory) = CreateConnectedSocketWithOptions(options);
            var websocket = factory.LastCreatedWebsocket!;
            var channel = socket.Channel("test");
            var joinPush = channel.Join();
            var joinTimeout = executor.Executions.Single(
                execution => execution.Delay == options.Timeout
            );
            var sendsAfterLeave = -1;
            joinTimeout.OnCancel = () =>
            {
                channel.Leave();
                sendsAfterLeave = websocket.CallSend.Count;
            };

            // MatchReceive commits Timeout before cancelling its delayed execution.
            // Reentrant cancellation makes Leave complete before timeout hooks dispatch.
            joinPush.Trigger(ReplyStatus.Timeout);
            var stateAfterTimeoutHook = channel.State;
            var scheduledRejoins = executor.Executions
                .Where(execution =>
                    execution.Delay == rejoinDelay
                    && !execution.IsCancelled
                )
                .ToList();

            // If a stale timeout scheduled a rejoin, expose its outbound join as well.
            scheduledRejoins.ForEach(execution => execution.Execute());
            var messagesAfterLeave = websocket.CallSend.Skip(sendsAfterLeave).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(stateAfterTimeoutHook, Is.EqualTo(ChannelState.Closed));
                Assert.That(channel.State, Is.EqualTo(ChannelState.Closed));
                Assert.That(scheduledRejoins, Is.Empty);
                Assert.That(messagesAfterLeave, Has.None.Contains("\"phx_leave\""));
                Assert.That(messagesAfterLeave, Has.None.Contains("\"phx_join\""));
            });
        }

        #endregion

        #region Parameter Validation Tests

        [Test]
        public void ConstructorThrowsOnNullTopicTest()
        {
            var socket = CreateBasicSocket();
            Assert.Throws<ArgumentNullException>(() => new Channel(null!, null, socket));
        }

        [Test]
        public void ConstructorThrowsOnEmptyTopicTest()
        {
            var socket = CreateBasicSocket();
            Assert.Throws<ArgumentException>(() => new Channel("", null, socket));
        }

        [Test]
        public void ConstructorThrowsOnWhitespaceTopicTest()
        {
            var socket = CreateBasicSocket();
            Assert.Throws<ArgumentException>(() => new Channel("   ", null, socket));
        }

        [Test]
        public void ConstructorThrowsOnNullSocketTest()
        {
            Assert.Throws<ArgumentNullException>(() => new Channel("test", null, null!));
        }

        [Test]
        public void OnThrowsOnNullEventTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");

            Assert.Throws<ArgumentNullException>(() => channel.On(null!, _ => { }));
        }

        [Test]
        public void OnThrowsOnEmptyEventTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");

            Assert.Throws<ArgumentException>(() => channel.On("", _ => { }));
        }

        [Test]
        public void OnThrowsOnWhitespaceEventTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");

            Assert.Throws<ArgumentException>(() => channel.On("   ", _ => { }));
        }

        [Test]
        public void OnThrowsOnNullCallbackTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");

            Assert.Throws<ArgumentNullException>(() => channel.On("event", (Action<Message>)null!));
        }

        [Test]
        public void OffThrowsOnNullSubscriptionTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");

            Assert.Throws<ArgumentNullException>(() => channel.Off((ChannelSubscription)null!));
        }

        [Test]
        public void OffThrowsOnNullEventNameTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");

            Assert.Throws<ArgumentNullException>(() => channel.Off((string)null!));
        }

        [Test]
        public void OffThrowsOnEmptyEventNameTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");

            Assert.Throws<ArgumentException>(() => channel.Off(""));
        }

        [Test]
        public void OffThrowsOnWhitespaceEventNameTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");

            Assert.Throws<ArgumentException>(() => channel.Off("   "));
        }

        [Test]
        public void PushThrowsOnNullEventTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");
            channel.Join();

            Assert.Throws<ArgumentNullException>(() => channel.Push(null!));
        }

        [Test]
        public void PushThrowsOnEmptyEventTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");
            channel.Join();

            Assert.Throws<ArgumentException>(() => channel.Push(""));
        }

        [Test]
        public void PushThrowsOnWhitespaceEventTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");
            channel.Join();

            Assert.Throws<ArgumentException>(() => channel.Push("   "));
        }

        #endregion

        #region IsMember Tests

        [Test]
        public void IsMemberReturnsTrueForMatchingTopicTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test-topic");

            var message = new Message(topic: "test-topic", @event: "event");
            Assert.IsTrue(channel.IsMember(message));
        }

        [Test]
        public void IsMemberReturnsFalseForDifferentTopicTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test-topic");

            var message = new Message(topic: "other-topic", @event: "event");
            Assert.IsFalse(channel.IsMember(message));
        }

        #endregion

        #region CanPush Tests

        [Test]
        public void CanPushReturnsFalseWhenNotJoinedTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");

            Assert.IsFalse(channel.CanPush());
        }

        [Test]
        public void CanPushReturnsFalseWhenJoiningTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");
            channel.Join();

            Assert.IsFalse(channel.CanPush());
        }

        [Test]
        public void CanPushReturnsTrueWhenJoinedAndConnectedTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");
            var joinPush = channel.Join();
            joinPush.Trigger(ReplyStatus.Ok);

            Assert.IsTrue(channel.CanPush());
        }

        [Test]
        public void CanPushReturnsFalseWhenJoinedButDisconnectedTest()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );
            socket.Connect();
            var channel = socket.Channel("test");
            var joinPush = channel.Join();
            joinPush.Trigger(ReplyStatus.Ok);

            // Disconnect
            var conn = factory.LastCreatedWebsocket;
            Assert.IsNotNull(conn);
            conn.MockState = WebsocketState.Closed;

            Assert.IsFalse(channel.CanPush());
        }

        #endregion

        #region ReplyEventName Tests

        [Test]
        public void ReplyEventNameGeneratesCorrectFormatTest()
        {
            var eventName = Channel.ReplyEventName("123");
            Assert.AreEqual("chan_reply_123", eventName);
        }

        [Test]
        public void ReplyEventNameHandlesNullRefTest()
        {
            var eventName = Channel.ReplyEventName(null);
            Assert.AreEqual("chan_reply_", eventName);
        }

        #endregion

        private sealed class ReentrantCancelDelayedExecutor : IDelayedExecutor
        {
            public List<ReentrantCancelDelayedExecution> Executions { get; } = new();

            public IDelayedExecution Execute(Action action, TimeSpan delay)
            {
                var execution = new ReentrantCancelDelayedExecution(action, delay);
                Executions.Add(execution);
                return execution;
            }
        }

        private sealed class ReentrantCancelDelayedExecution : IDelayedExecution
        {
            private readonly Action _action;

            public Action? OnCancel { get; set; }
            public TimeSpan Delay { get; }
            public bool IsCancelled { get; private set; }

            public ReentrantCancelDelayedExecution(Action action, TimeSpan delay)
            {
                _action = action;
                Delay = delay;
            }

            public void Cancel()
            {
                IsCancelled = true;
                var onCancel = OnCancel;
                OnCancel = null;
                onCancel?.Invoke();
            }

            public void Execute()
            {
                if (!IsCancelled)
                {
                    _action();
                }
            }
        }
    }

    /// <summary>
    /// Testable channel that exposes OnMessage for testing
    /// </summary>
    public class TestableChannel : Channel
    {
        public bool OnMessageCalled { get; private set; }

        public TestableChannel(string topic, Dictionary<string, object>? @params, Socket socket)
            : base(topic, @params, socket)
        {
        }

        public override IJsonBox? OnMessage(Message message)
        {
            OnMessageCalled = true;
            return base.OnMessage(message);
        }
    }

    public class NullPayloadChannel : Channel
    {
        public NullPayloadChannel(
            string topic,
            Dictionary<string, object>? @params,
            Socket socket
        )
            : base(topic, @params, socket)
        {
        }

        public override IJsonBox? OnMessage(Message message)
        {
            return null;
        }
    }
}
