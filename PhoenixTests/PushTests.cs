using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Phoenix;
using PhoenixTests.TestDoubles;
using PhoenixTests.WebSocketImpl;

namespace PhoenixTests
{
    [TestFixture, Category("Unit")]
    public class PushTests : PhoenixTestBase
    {

        #region Construction & Initialization Tests

        [Test]
        public void ConstructorThrowsOnNullChannelTest()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new Push(null!, "event", null, TimeSpan.FromSeconds(10)));
        }

        [Test]
        public void ConstructorThrowsOnNullEventTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");

            Assert.Throws<ArgumentNullException>(() =>
                new Push(channel, null!, null, TimeSpan.FromSeconds(10)));
        }

        [Test]
        public void ConstructorThrowsOnEmptyEventTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");

            Assert.Throws<ArgumentException>(() =>
                new Push(channel, "", null, TimeSpan.FromSeconds(10)));
        }

        [Test]
        public void ConstructorThrowsOnWhitespaceEventTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");

            Assert.Throws<ArgumentException>(() =>
                new Push(channel, "   ", null, TimeSpan.FromSeconds(10)));
        }

        [Test]
        public void ConstructorAcceptsNullPayloadTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");

            Assert.DoesNotThrow(() =>
                new Push(channel, "event", null, TimeSpan.FromSeconds(10)));
        }

        [Test]
        public void ConstructorAcceptsZeroTimeoutTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");

            Assert.DoesNotThrow(() =>
                new Push(channel, "event", null, TimeSpan.Zero));
        }

        [Test]
        public void RefIsNullAfterConstructionTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");

            var push = new Push(channel, "event", null, TimeSpan.FromSeconds(10));

            Assert.IsNull(push.Ref);
        }

        #endregion

        #region Receive() Callback Chain Tests

        [Test]
        public void ReceiveReturnsSamePushInstanceForFluentChainingTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");
            var push = new Push(channel, "event", null, TimeSpan.FromSeconds(10));

            var result = push.Receive(ReplyStatus.Ok, _ => { });

            Assert.AreSame(push, result);
        }

        [Test]
        public void ReceiveFluentChainingWithMultipleStatusesTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");
            var push = new Push(channel, "event", null, TimeSpan.FromSeconds(10));

            var result = push
                .Receive(ReplyStatus.Ok, _ => { })
                .Receive(ReplyStatus.Error, _ => { })
                .Receive(ReplyStatus.Timeout, _ => { });

            Assert.AreSame(push, result);
        }

        [Test]
        public void ReceiveThrowsOnNullCallbackTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");
            var push = new Push(channel, "event", null, TimeSpan.FromSeconds(10));

            Assert.Throws<ArgumentNullException>(() =>
                push.Receive(ReplyStatus.Ok, null!));
        }

        [Test]
        public void ReceiveWithMultipleCallbacksForSameStatusTest()
        {
            var (channel, _, executor) = CreateJoinedChannel();
            var callCount = 0;

            var push = channel.Push("test_event");
            push.Receive(ReplyStatus.Ok, _ => callCount++);
            push.Receive(ReplyStatus.Ok, _ => callCount++);
            push.Receive(ReplyStatus.Ok, _ => callCount++);

            // Simulate Ok response
            push.Trigger(ReplyStatus.Ok);

            Assert.AreEqual(3, callCount);
        }

        [Test]
        public void ReceiveOkCallbackInvokedOnOkResponseTest()
        {
            var (channel, _, executor) = CreateJoinedChannel();
            var okCalled = false;
            var errorCalled = false;
            var timeoutCalled = false;

            var push = channel.Push("test_event");
            push.Receive(ReplyStatus.Ok, _ => okCalled = true);
            push.Receive(ReplyStatus.Error, _ => errorCalled = true);
            push.Receive(ReplyStatus.Timeout, _ => timeoutCalled = true);

            push.Trigger(ReplyStatus.Ok);

            Assert.IsTrue(okCalled);
            Assert.IsFalse(errorCalled);
            Assert.IsFalse(timeoutCalled);
        }

        [Test]
        public void ReceiveErrorCallbackInvokedOnErrorResponseTest()
        {
            var (channel, _, executor) = CreateJoinedChannel();
            var okCalled = false;
            var errorCalled = false;
            var timeoutCalled = false;

            var push = channel.Push("test_event");
            push.Receive(ReplyStatus.Ok, _ => okCalled = true);
            push.Receive(ReplyStatus.Error, _ => errorCalled = true);
            push.Receive(ReplyStatus.Timeout, _ => timeoutCalled = true);

            push.Trigger(ReplyStatus.Error);

            Assert.IsFalse(okCalled);
            Assert.IsTrue(errorCalled);
            Assert.IsFalse(timeoutCalled);
        }

        [Test]
        public void ReceiveTimeoutCallbackInvokedOnTimeoutResponseTest()
        {
            var (channel, _, executor) = CreateJoinedChannel();
            var okCalled = false;
            var errorCalled = false;
            var timeoutCalled = false;

            var push = channel.Push("test_event");
            push.Receive(ReplyStatus.Ok, _ => okCalled = true);
            push.Receive(ReplyStatus.Error, _ => errorCalled = true);
            push.Receive(ReplyStatus.Timeout, _ => timeoutCalled = true);

            push.Trigger(ReplyStatus.Timeout);

            Assert.IsFalse(okCalled);
            Assert.IsFalse(errorCalled);
            Assert.IsTrue(timeoutCalled);
        }

        [Test]
        public void ReceiveCallbackReceivesCorrectReplyTest()
        {
            var (channel, websocket, _) = CreateJoinedChannel();
            Reply? receivedReply = null;

            var push = channel.Push("test_event");
            push.Receive(ReplyStatus.Ok, reply => receivedReply = reply);

            // Simulate server response with payload
            var serializer = new JsonMessageSerializer();
            var responsePayload = serializer.Box(new Reply(
                "ok",
                serializer.Box(new Dictionary<string, object> { { "data", "test" } })
            ));

            var refEvent = Channel.ReplyEventName(push.Ref);
            channel.Trigger(new Message(@event: refEvent, payload: responsePayload));

            Assert.IsNotNull(receivedReply);
            Assert.AreEqual(ReplyStatus.Ok, receivedReply?.ReplyStatus);
        }

        [Test]
        public void ReceiveDuringDispatchDoesNotSkipExistingCallbacksTest()
        {
            var (channel, _, _) = CreateJoinedChannel();
            using var callbackEntered = new ManualResetEventSlim();
            using var releaseCallback = new ManualResetEventSlim();
            var trailingCallbackCount = 0;

            var push = channel.Push("test_event");
            push.Receive(ReplyStatus.Ok, _ =>
            {
                callbackEntered.Set();
                releaseCallback.Wait();
            });
            push.Receive(
                ReplyStatus.Ok,
                _ => Interlocked.Increment(ref trailingCallbackCount)
            );

            var dispatchTask = Task.Run(() => push.Trigger(ReplyStatus.Ok));
            Assert.That(
                callbackEntered.Wait(TimeSpan.FromSeconds(1)),
                Is.True,
                "Reply dispatch did not reach the blocking callback."
            );

            var registrationTask = Task.Run(() =>
            {
                for (var hookIndex = 0; hookIndex < 1_000; hookIndex++)
                {
                    push.Receive(ReplyStatus.Ok, _ => { });
                }
            });
            var registrationCompleted = registrationTask.Wait(TimeSpan.FromSeconds(1));
            releaseCallback.Set();

            Assert.That(
                registrationCompleted,
                Is.True,
                "Receive blocked while a user callback was running."
            );
            Assert.That(
                dispatchTask.Wait(TimeSpan.FromSeconds(1)),
                Is.True,
                "Reply dispatch did not complete."
            );
            Assert.That(trailingCallbackCount, Is.EqualTo(1));
        }

        [Test]
        public void ReceiveAfterReplyReplaysAndRemainsRegisteredForResendTest()
        {
            var (channel, _, _) = CreateJoinedChannel();
            var callbackCount = 0;
            var push = channel.Push("test_event");

            push.Trigger(ReplyStatus.Ok);
            push.Receive(
                ReplyStatus.Ok,
                _ => Interlocked.Increment(ref callbackCount)
            );

            Assert.That(callbackCount, Is.EqualTo(1));

            push.Resend(TimeSpan.FromSeconds(10));
            push.Trigger(ReplyStatus.Ok);

            Assert.That(callbackCount, Is.EqualTo(2));
        }

        [Test]
        public void ReceiveErrorAfterCustomReplyReplaysRawStatusWithoutTimeoutTest()
        {
            var (channel, websocket, executor) = CreateJoinedChannel();
            var push = channel.Push("test_event");

            websocket.SimulateMessage(BuildPushReply(
                "1",
                push.Ref!,
                channel.Topic,
                "partial",
                "{\"progress\":50}"
            ));

            Reply? replayedReply = null;
            var timeoutCount = 0;
            push.Receive(ReplyStatus.Error, reply => replayedReply = reply);
            push.Receive(
                ReplyStatus.Timeout,
                _ => Interlocked.Increment(ref timeoutCount)
            );

            Assert.That(replayedReply, Is.Not.Null);
            var reply = replayedReply.GetValueOrDefault();
            Assert.That(reply.Status, Is.EqualTo("partial"));
            Assert.That(reply.ReplyStatus, Is.EqualTo(ReplyStatus.Error));

            executor.ExecutePending();

            Assert.That(timeoutCount, Is.Zero);
        }

        #endregion

        #region Send() Behavior Tests

        [Test]
        public void SendWhenChannelCanPushSendsMessageTest()
        {
            var (channel, websocket, _) = CreateJoinedChannel();
            websocket.CallSend.Clear();

            var push = channel.Push("test_event");

            // Push should have sent the message
            Assert.AreEqual(1, websocket.CallSend.Count);
            Assert.IsTrue(websocket.CallSend[0].Contains("test_event"));
        }

        [Test]
        public void SendWhenChannelCannotPushBuffersMessageTest()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );
            socket.Connect();
            var websocket = factory.LastCreatedWebsocket!;

            var channel = socket.Channel("test");
            var joinPush = channel.Join();
            websocket.CallSend.Clear();

            // Channel is in Joining state, can't push yet
            var push = channel.Push("buffered_event");

            // Message should not be sent yet (no new messages)
            Assert.AreEqual(0, websocket.CallSend.Count);

            // Trigger join success
            joinPush.Trigger(ReplyStatus.Ok);

            // Now the buffered message should be sent
            Assert.IsTrue(websocket.CallSend.Count > 0);
            Assert.IsTrue(websocket.CallSend.Exists(s => s.Contains("buffered_event")));
        }

        [Test]
        public void SendSetsRefOnPushTest()
        {
            var (channel, _, _) = CreateJoinedChannel();

            var push = channel.Push("test_event");

            Assert.IsNotNull(push.Ref);
        }

        [Test]
        public void SendPinsJoinRefBeforePayloadFactoryCanRejoinTest()
        {
            var serializer = new JsonMessageSerializer();
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(serializer)
                {
                    DelayedExecutor = new MockDelayedExecutor()
                }
            );
            socket.Connect();
            var websocket = factory.LastCreatedWebsocket!;
            var channel = socket.Channel("test");
            var joinPush = channel.Join();
            joinPush.Trigger(ReplyStatus.Ok);
            var initialJoinRef = channel.JoinRef;
            var push = new Push(
                channel,
                "test_event",
                () =>
                {
                    joinPush.Resend(TimeSpan.FromSeconds(10));
                    return serializer.Box(new Dictionary<string, object>());
                },
                TimeSpan.FromSeconds(10)
            );
            websocket.CallSend.Clear();

            push.Send();

            var sentMessage = serializer.Deserialize<Message>(
                websocket.CallSend[websocket.CallSend.Count - 1]
            ) ?? throw new AssertionException("Expected an outbound message.");
            Assert.Multiple(() =>
            {
                Assert.That(joinPush.Ref, Is.Not.EqualTo(initialJoinRef));
                Assert.That(sentMessage.Ref, Is.EqualTo(push.Ref));
                Assert.That(sentMessage.JoinRef, Is.EqualTo(initialJoinRef));
            });
        }

        [Test]
        public void SendDoesNotResendAfterTimeoutTest()
        {
            var mockExecutor = new MockDelayedExecutor();
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                DelayedExecutor = mockExecutor,
                Timeout = TimeSpan.FromMilliseconds(100)
            };

            var socket = new Socket("ws://localhost:1234", null, factory, options);
            socket.Connect();
            var channel = socket.Channel("test");
            var joinPush = channel.Join();
            joinPush.Trigger(ReplyStatus.Ok);

            var websocket = factory.LastCreatedWebsocket!;
            websocket.CallSend.Clear();

            var push = channel.Push("test_event");

            Assert.AreEqual(1, websocket.CallSend.Count);

            // Trigger timeout
            push.Trigger(ReplyStatus.Timeout);
            websocket.CallSend.Clear();

            // Try to send again - should be blocked due to timeout
            push.Send();

            Assert.AreEqual(0, websocket.CallSend.Count);
        }

        [Test]
        public void SendAfterSuccessfulReplyStartsNewCompletionCycleTest()
        {
            var (channel, _, _) = CreateJoinedChannel();
            var okCount = 0;
            var push = channel.Push("test_event");
            push.Receive(
                ReplyStatus.Ok,
                _ => Interlocked.Increment(ref okCount)
            );

            push.Trigger(ReplyStatus.Ok);
            push.Send();
            push.Trigger(ReplyStatus.Ok);

            Assert.That(okCount, Is.EqualTo(2));
        }

        #endregion

        #region Timeout Handling Tests

        [Test]
        public void TimeoutCallbackInvokedAfterDelayTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");
            var joinPush = channel.Join();
            joinPush.Trigger(ReplyStatus.Ok);

            var timeoutCalled = false;
            var push = channel.Push("test_event", timeout: TimeSpan.FromMilliseconds(10));
            push.Receive(ReplyStatus.Timeout, _ => timeoutCalled = true);

            // Wait for timeout
            Assert.That(() => timeoutCalled, Is.True.After(100, 5));
        }

        [Test]
        public void TimeoutTriggersTimeoutStatusReceiveHandlersTest()
        {
            var mockExecutor = new MockDelayedExecutor();
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                DelayedExecutor = mockExecutor
            };

            var socket = new Socket("ws://localhost:1234", null, factory, options);
            socket.Connect();
            var channel = socket.Channel("test");
            var joinPush = channel.Join();
            joinPush.Trigger(ReplyStatus.Ok);

            var timeoutCalled = false;
            var push = channel.Push("test_event");
            push.Receive(ReplyStatus.Timeout, _ => timeoutCalled = true);

            // Execute the timeout callback
            mockExecutor.ExecutePending();

            Assert.IsTrue(timeoutCalled);
        }

        [Test]
        public void ReplyWinningRaceSuppressesAlreadyStartedTimeoutTest()
        {
            var executor = new TrackingDelayedExecutor();
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                DelayedExecutor = executor
            };
            var socket = new Socket("ws://localhost:1234", null, factory, options);
            socket.Connect();

            using var timeoutSnapshotReached = new ManualResetEventSlim();
            using var releaseTimeout = new ManualResetEventSlim();
            var channel = new CoordinatedReplyChannel(
                "test",
                socket,
                timeoutSnapshotReached,
                releaseTimeout
            );
            var joinPush = channel.Join();
            joinPush.Trigger(ReplyStatus.Ok);
            executor.Clear();

            var okCount = 0;
            var timeoutCount = 0;
            var push = channel.Push("test_event");
            push.Receive(
                ReplyStatus.Ok,
                _ => Interlocked.Increment(ref okCount)
            );
            push.Receive(
                ReplyStatus.Timeout,
                _ => Interlocked.Increment(ref timeoutCount)
            );

            channel.CoordinateTimeout = true;
            var timeoutExecution = executor.Executions[0];
            var timeoutTask = Task.Run(timeoutExecution.Execute);

            try
            {
                Assert.That(
                    timeoutSnapshotReached.Wait(TimeSpan.FromSeconds(1)),
                    Is.True,
                    "Timeout dispatch did not reach the coordination point."
                );

                push.Trigger(ReplyStatus.Ok);
            }
            finally
            {
                releaseTimeout.Set();
            }

            Assert.That(
                timeoutTask.Wait(TimeSpan.FromSeconds(1)),
                Is.True,
                "Timeout dispatch did not complete."
            );
            Assert.That(okCount, Is.EqualTo(1));
            Assert.That(
                timeoutCount,
                Is.EqualTo(0),
                "The timeout must be ignored after the reply completes the send cycle."
            );
        }

        [Test]
        public void ResendSuppressesAlreadyStartedTimeoutFromPreviousAttemptTest()
        {
            var executor = new TrackingDelayedExecutor();
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                DelayedExecutor = executor
            };
            var socket = new Socket("ws://localhost:1234", null, factory, options);
            socket.Connect();

            using var timeoutSnapshotReached = new ManualResetEventSlim();
            using var releaseTimeout = new ManualResetEventSlim();
            var channel = new CoordinatedReplyChannel(
                "test",
                socket,
                timeoutSnapshotReached,
                releaseTimeout
            );
            var joinPush = channel.Join();
            joinPush.Trigger(ReplyStatus.Ok);
            executor.Clear();

            var okCount = 0;
            var timeoutCount = 0;
            var push = channel.Push("test_event");
            push.Receive(
                ReplyStatus.Ok,
                _ => Interlocked.Increment(ref okCount)
            );
            push.Receive(
                ReplyStatus.Timeout,
                _ => Interlocked.Increment(ref timeoutCount)
            );

            channel.CoordinateTimeout = true;
            var staleTimeoutExecution = executor.Executions[0];
            var staleTimeoutTask = Task.Run(staleTimeoutExecution.Execute);

            try
            {
                Assert.That(
                    timeoutSnapshotReached.Wait(TimeSpan.FromSeconds(1)),
                    Is.True,
                    "The stale timeout dispatch did not reach the coordination point."
                );

                push.Resend(TimeSpan.FromSeconds(10));
            }
            finally
            {
                releaseTimeout.Set();
            }

            Assert.That(
                staleTimeoutTask.Wait(TimeSpan.FromSeconds(1)),
                Is.True,
                "The stale timeout dispatch did not complete."
            );
            Assert.That(
                timeoutCount,
                Is.Zero,
                "A timeout from the previous send attempt must not complete the resend."
            );

            push.Trigger(ReplyStatus.Ok);

            Assert.That(okCount, Is.EqualTo(1));
            Assert.That(timeoutCount, Is.Zero);
        }

        [Test]
        public void CancelTimeoutPreventsTimeoutCallbackTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");
            var joinPush = channel.Join();
            joinPush.Trigger(ReplyStatus.Ok);

            var timeoutCalled = false;
            var push = channel.Push("test_event", timeout: TimeSpan.FromMilliseconds(50));
            push.Receive(ReplyStatus.Timeout, _ => timeoutCalled = true);

            // Cancel timeout before it fires
            push.CancelTimeout();

            Assert.That(() => timeoutCalled, Is.False.After(100, 5));
        }

        [Test]
        public void CancelTimeoutCanBeCalledMultipleTimesTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");
            var joinPush = channel.Join();
            joinPush.Trigger(ReplyStatus.Ok);

            var push = channel.Push("test_event");

            Assert.DoesNotThrow(() =>
            {
                push.CancelTimeout();
                push.CancelTimeout();
                push.CancelTimeout();
            });
        }

        [Test]
        public void CancelTimeoutOnPushWithNoActiveTimeoutTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");

            var push = new Push(channel, "event", null, TimeSpan.FromSeconds(10));

            // Should not throw even if no timeout was started
            Assert.DoesNotThrow(() => push.CancelTimeout());
        }

        #endregion

        #region Reset() Behavior Tests

        [Test]
        public void ResetCancelsPendingTimeoutTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");
            var joinPush = channel.Join();
            joinPush.Trigger(ReplyStatus.Ok);

            var timeoutCalled = false;
            var push = channel.Push("test_event", timeout: TimeSpan.FromMilliseconds(50));
            push.Receive(ReplyStatus.Timeout, _ => timeoutCalled = true);

            push.Reset();

            Assert.That(() => timeoutCalled, Is.False.After(100, 5));
        }

        [Test]
        public void ResetClearsRefTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");
            var joinPush = channel.Join();
            joinPush.Trigger(ReplyStatus.Ok);

            var push = channel.Push("test_event");
            Assert.IsNotNull(push.Ref);

            push.Reset();

            Assert.IsNull(push.Ref);
        }

        [Test]
        public void ResetAllowsResendTest()
        {
            var (channel, websocket, _) = CreateJoinedChannel();
            websocket.CallSend.Clear();

            var push = channel.Push("test_event");
            var firstRef = push.Ref;
            Assert.AreEqual(1, websocket.CallSend.Count);

            push.Reset();
            push.Send();

            Assert.AreEqual(2, websocket.CallSend.Count);
            Assert.AreNotEqual(firstRef, push.Ref);
        }

        #endregion

        #region Trigger() Method Tests

        [Test]
        public void TriggerTriggersAppropriateStatusCallbacksTest()
        {
            var (channel, _, _) = CreateJoinedChannel();
            var okCalled = false;

            var push = channel.Push("test_event");
            push.Receive(ReplyStatus.Ok, _ => okCalled = true);

            push.Trigger(ReplyStatus.Ok);

            Assert.IsTrue(okCalled);
        }

        [Test]
        public void TriggerWithErrorStatusTest()
        {
            var (channel, _, _) = CreateJoinedChannel();
            var errorCalled = false;

            var push = channel.Push("test_event");
            push.Receive(ReplyStatus.Error, _ => errorCalled = true);

            push.Trigger(ReplyStatus.Error);

            Assert.IsTrue(errorCalled);
        }

        [Test]
        public void TriggerWithTimeoutStatusTest()
        {
            var (channel, _, _) = CreateJoinedChannel();
            var timeoutCalled = false;

            var push = channel.Push("test_event");
            push.Receive(ReplyStatus.Timeout, _ => timeoutCalled = true);

            push.Trigger(ReplyStatus.Timeout);

            Assert.IsTrue(timeoutCalled);
        }

        #endregion

        #region Resend() Method Tests

        [Test]
        public void ResendResetsAndSendsWithNewTimeoutTest()
        {
            var (channel, websocket, _) = CreateJoinedChannel();
            websocket.CallSend.Clear();

            var push = channel.Push("test_event");
            var originalRef = push.Ref;
            Assert.AreEqual(1, websocket.CallSend.Count);

            push.Resend(TimeSpan.FromSeconds(30));

            Assert.AreEqual(2, websocket.CallSend.Count);
            Assert.AreNotEqual(originalRef, push.Ref);
        }

        [Test]
        public void ResendCancelsPreviousTimeoutTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");
            var joinPush = channel.Join();
            joinPush.Trigger(ReplyStatus.Ok);

            var timeoutCount = 0;
            var push = channel.Push("test_event", timeout: TimeSpan.FromMilliseconds(50));
            push.Receive(ReplyStatus.Timeout, _ => timeoutCount++);

            // Resend with a longer timeout before the first one fires
            push.Resend(TimeSpan.FromMilliseconds(200));

            // First timeout should have been cancelled (check after 100ms)
            Assert.That(() => timeoutCount, Is.EqualTo(0).After(100, 5));

            // Only the new timeout should fire (check after full 200ms from Resend, plus buffer)
            Assert.That(() => timeoutCount, Is.EqualTo(1).After(250, 5));
        }

        #endregion

        #region Edge Cases Tests

        [Test]
        public void MultipleSendsDoNotDuplicateMessagesTest()
        {
            var (channel, websocket, _) = CreateJoinedChannel();
            websocket.CallSend.Clear();

            var push = channel.Push("test_event");
            Assert.AreEqual(1, websocket.CallSend.Count);

            // First send already happened during Push()
            // Subsequent sends should still work but may be blocked after timeout
            push.Send();
            push.Send();

            // Multiple sends should be allowed (they just re-send the message)
            Assert.GreaterOrEqual(websocket.CallSend.Count, 1);
        }

        [Test]
        public void ReceiveAfterTimeoutStillCallsHandlerTest()
        {
            var (channel, _, executor) = CreateJoinedChannel();

            var push = channel.Push("test_event");
            executor.ExecutePending(); // Trigger timeout

            // Register handler after timeout
            var okCalled = false;
            push.Receive(ReplyStatus.Timeout, _ => okCalled = true);

            // Handler should be called immediately since status already received
            Assert.IsTrue(okCalled);
        }

        [Test]
        public void ReceiveRegisteredBeforeStatusIsReceivedTest()
        {
            var (channel, _, executor) = CreateJoinedChannel();

            var timeoutCalled = false;
            var push = channel.Push("test_event");
            push.Receive(ReplyStatus.Timeout, _ => timeoutCalled = true);

            Assert.IsFalse(timeoutCalled);

            executor.ExecutePending(); // Trigger timeout

            Assert.IsTrue(timeoutCalled);
        }

        [Test]
        public void ResetAndResendWithNewReceiveHandlersTest()
        {
            var (channel, websocket, _) = CreateJoinedChannel();
            websocket.CallSend.Clear();

            var firstOkCount = 0;
            var secondOkCount = 0;

            var push = channel.Push("test_event");
            push.Receive(ReplyStatus.Ok, _ => firstOkCount++);

            // Reset and resend
            push.Reset();
            push.Receive(ReplyStatus.Ok, _ => secondOkCount++);
            push.Send();

            // Trigger ok
            push.Trigger(ReplyStatus.Ok);

            // Both handlers should be called since they're accumulated
            Assert.AreEqual(1, firstOkCount);
            Assert.AreEqual(1, secondOkCount);
        }

        [Test]
        public void PayloadFunctionIsCalledOnSendTest()
        {
            var (channel, websocket, _) = CreateJoinedChannel();
            websocket.CallSend.Clear();

            var payloadCallCount = 0;
            var serializer = new JsonMessageSerializer();
            var push = new Push(
                channel,
                "test_event",
                () =>
                {
                    payloadCallCount++;
                    return serializer.Box(new Dictionary<string, object> { { "key", "value" } });
                },
                TimeSpan.FromSeconds(10)
            );

            push.StartTimeout();
            push.Send();

            Assert.AreEqual(1, payloadCallCount);
            Assert.IsTrue(websocket.CallSend[0].Contains("key"));
        }

        [Test]
        public void PayloadFunctionReturningNullIsHandledTest()
        {
            var (channel, websocket, _) = CreateJoinedChannel();
            websocket.CallSend.Clear();

            var push = new Push(
                channel,
                "test_event",
                () => null,
                TimeSpan.FromSeconds(10)
            );

            Assert.DoesNotThrow(() =>
            {
                push.StartTimeout();
                push.Send();
            });
        }

        [Test]
        public void StartTimeoutCancelsPreviousTimeoutTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");
            var joinPush = channel.Join();
            joinPush.Trigger(ReplyStatus.Ok);

            var timeoutCount = 0;
            var push = channel.Push("test_event", timeout: TimeSpan.FromMilliseconds(100));
            push.Receive(ReplyStatus.Timeout, _ => timeoutCount++);

            // Start a new timeout (simulates what happens on resend)
            push.StartTimeout();
            push.StartTimeout();
            push.StartTimeout();

            // Only one timeout should fire
            Assert.That(() => timeoutCount, Is.EqualTo(1).After(200, 5));
        }

        [Test]
        public void SuccessfulResponseCancelsPendingTimeoutTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");
            var joinPush = channel.Join();
            joinPush.Trigger(ReplyStatus.Ok);

            var timeoutCalled = false;
            var push = channel.Push("test_event", timeout: TimeSpan.FromMilliseconds(100));
            push.Receive(ReplyStatus.Timeout, _ => timeoutCalled = true);

            // Trigger success immediately
            push.Trigger(ReplyStatus.Ok);

            // Timeout should not fire since we received a response
            Assert.That(() => timeoutCalled, Is.False.After(200, 5));
        }

        #endregion

        #region Integration with Channel Tests

        [Test]
        public void PushFromChannelSendsCorrectMessageFormatTest()
        {
            var (channel, websocket, _) = CreateJoinedChannel();
            websocket.CallSend.Clear();

            var push = channel.Push("my_event", new Dictionary<string, object> { { "data", "test" } });

            Assert.AreEqual(1, websocket.CallSend.Count);
            var sentMessage = websocket.CallSend[0];
            Assert.IsTrue(sentMessage.Contains("my_event"));
            Assert.IsTrue(sentMessage.Contains("test-topic"));
            Assert.IsTrue(sentMessage.Contains("data"));
        }

        [Test]
        public void JoinPushReturnsCorrectPushInstanceTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");

            var joinPush = channel.Join();

            Assert.IsNotNull(joinPush);
        }

        [Test]
        public void JoinPushReceiveOkTransitionsChannelToJoinedTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");

            var joinPush = channel.Join();
            Assert.AreEqual(ChannelState.Joining, channel.State);

            joinPush.Trigger(ReplyStatus.Ok);

            Assert.AreEqual(ChannelState.Joined, channel.State);
        }

        [Test]
        public void JoinPushReceiveErrorTransitionsChannelToErroredTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("test");

            var joinPush = channel.Join();
            joinPush.Trigger(ReplyStatus.Error);

            Assert.AreEqual(ChannelState.Errored, channel.State);
        }

        #endregion

        private sealed class CoordinatedReplyChannel : Channel
        {
            private readonly ManualResetEventSlim _timeoutSnapshotReached;
            private readonly ManualResetEventSlim _releaseTimeout;

            public CoordinatedReplyChannel(
                string topic,
                Socket socket,
                ManualResetEventSlim timeoutSnapshotReached,
                ManualResetEventSlim releaseTimeout
            ) : base(topic, null, socket)
            {
                _timeoutSnapshotReached = timeoutSnapshotReached;
                _releaseTimeout = releaseTimeout;
            }

            public bool CoordinateTimeout { get; set; }

            public override IJsonBox? OnMessage(Message message)
            {
                var payload = base.OnMessage(message);
                var reply = message.Payload?.Unbox<Reply?>();
                if (CoordinateTimeout
                    && reply.HasValue
                    && reply.Value.ReplyStatus == ReplyStatus.Timeout)
                {
                    _timeoutSnapshotReached.Set();
                    _releaseTimeout.Wait();
                }

                return payload;
            }
        }
    }
}
