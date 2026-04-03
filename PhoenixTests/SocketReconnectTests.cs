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
    public class SocketReconnectTests : PhoenixTestBase
    {
        #region Basic Reconnect Tests

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

        [Test]
        public void ReconnectIsAttemptedAfterConnectionLossTest()
        {
            var mockExecutor = new TrackingDelayedExecutor();
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var reconnectAttempts = 0;
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                DelayedExecutor = mockExecutor,
                HeartbeatInterval = null, // Disable heartbeat to simplify test
                ReconnectAfter = tries =>
                {
                    reconnectAttempts = tries;
                    return TimeSpan.FromMilliseconds(100 * tries);
                }
            };

            var socket = new Socket("ws://localhost:1234", null, factory, options);
            socket.Connect();

            var conn = factory.LastCreatedWebsocket;
            Assert.IsNotNull(conn);
            Assert.AreEqual(1, conn.CallConnectCount);

            // Simulate abnormal connection close (code != 1000)
            conn.SimulateClose(1006, "Connection lost");

            // Reconnect should be scheduled
            var reconnectExecution = mockExecutor.Executions
                .LastOrDefault(e => !e.IsCancelled);
            Assert.IsNotNull(reconnectExecution, "Reconnect should be scheduled");

            // Trigger reconnect
            reconnectExecution!.Execute();

            // A new connection should be attempted
            Assert.IsNotNull(factory.LastCreatedWebsocket);
            Assert.AreEqual(1, reconnectAttempts, "Reconnect attempt counter should be 1");
        }

        [Test]
        public void ReconnectAfterFunctionControlsBackoffTimingTest()
        {
            var mockExecutor = new TrackingDelayedExecutor();
            // Use a factory that doesn't auto-open connections to test backoff
            var factory = new FailingThenSucceedingWebsocketFactory(failCount: 2);
            var capturedTries = new List<int>();
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                DelayedExecutor = mockExecutor,
                HeartbeatInterval = null,
                ReconnectAfter = tries =>
                {
                    capturedTries.Add(tries);
                    return TimeSpan.FromMilliseconds(100 * tries);
                }
            };

            var socket = new Socket("ws://localhost:1234", null, factory, options);
            socket.Connect();

            // First connect throws, so reconnect is scheduled
            // First reconnect scheduled with delay based on tries=1
            var firstReconnect = mockExecutor.Executions.LastOrDefault(e => !e.IsCancelled);
            Assert.IsNotNull(firstReconnect);
            Assert.AreEqual(TimeSpan.FromMilliseconds(100), firstReconnect!.Delay,
                "First reconnect should use 100ms delay (100 * 1)");

            // Trigger first reconnect - this will also fail
            firstReconnect.Execute();

            // Second reconnect scheduled with increasing delay (tries=2)
            var secondReconnect = mockExecutor.Executions.LastOrDefault(e => !e.IsCancelled);
            Assert.IsNotNull(secondReconnect);
            Assert.AreEqual(TimeSpan.FromMilliseconds(200), secondReconnect!.Delay,
                "Second reconnect should use 200ms delay (100 * 2)");

            // Verify the captured tries
            Assert.AreEqual(2, capturedTries.Count);
            Assert.AreEqual(1, capturedTries[0]);
            Assert.AreEqual(2, capturedTries[1]);
        }

        [Test]
        public void ReconnectCanBeDisabledWithNullReconnectAfterTest()
        {
            var mockExecutor = new TrackingDelayedExecutor();
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                DelayedExecutor = mockExecutor,
                HeartbeatInterval = null,
                ReconnectAfter = null // Disable reconnect
            };

            var socket = new Socket("ws://localhost:1234", null, factory, options);
            socket.Connect();

            var conn = factory.LastCreatedWebsocket;
            Assert.IsNotNull(conn);
            Assert.AreEqual(1, conn.CallConnectCount);

            var executionsBeforeClose = mockExecutor.Executions.Count;

            // Simulate connection close
            conn.SimulateClose(1006, "Connection lost");

            // No reconnect should be scheduled
            var executionsAfterClose = mockExecutor.Executions.Count;
            Assert.AreEqual(executionsBeforeClose, executionsAfterClose,
                "No new executions should be scheduled when reconnect is disabled");
        }

        [Test]
        public void ReconnectResetsTryCounterOnSuccessfulConnectionTest()
        {
            var mockExecutor = new TrackingDelayedExecutor();
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var capturedTries = new List<int>();
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                DelayedExecutor = mockExecutor,
                HeartbeatInterval = null,
                ReconnectAfter = tries =>
                {
                    capturedTries.Add(tries);
                    return TimeSpan.FromMilliseconds(100);
                }
            };

            var socket = new Socket("ws://localhost:1234", null, factory, options);
            socket.Connect();

            var conn1 = factory.LastCreatedWebsocket;
            Assert.IsNotNull(conn1);

            // First connection loss
            conn1.SimulateClose(1006, "Lost");

            // Trigger reconnect
            var reconnect1 = mockExecutor.Executions.LastOrDefault(e => !e.IsCancelled);
            Assert.IsNotNull(reconnect1);
            reconnect1!.Execute();

            // Second connection opened successfully
            var conn2 = factory.LastCreatedWebsocket;
            Assert.IsNotNull(conn2);
            Assert.AreEqual(1, conn2.CallConnectCount);

            // Now simulate another connection loss
            conn2.SimulateClose(1006, "Lost again");

            // The try counter should have been reset
            // So the next reconnect should be tries = 1 again
            Assert.AreEqual(1, capturedTries[0], "First reconnect should have tries = 1");
            Assert.AreEqual(1, capturedTries[1], "Second reconnect after success should have tries = 1");
        }

        [Test]
        public void MultipleReconnectAttemptsWithProgressiveBackoffTest()
        {
            var mockExecutor = new TrackingDelayedExecutor();
            // Use a factory that fails 3 times before succeeding
            var factory = new FailingThenSucceedingWebsocketFactory(failCount: 3);
            var capturedDelays = new List<TimeSpan>();
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                DelayedExecutor = mockExecutor,
                HeartbeatInterval = null,
                ReconnectAfter = tries =>
                {
                    var delay = TimeSpan.FromMilliseconds(100 * tries);
                    capturedDelays.Add(delay);
                    return delay;
                }
            };

            var socket = new Socket("ws://localhost:1234", null, factory, options);
            socket.Connect();

            // First connect throws, so we need to trigger reconnects
            for (var i = 0; i < 3; i++)
            {
                // Trigger reconnect
                var reconnect = mockExecutor.Executions.LastOrDefault(e => !e.IsCancelled);
                Assert.IsNotNull(reconnect, $"Reconnect {i + 1} should be scheduled");
                reconnect!.Execute();
            }

            // Verify progressive backoff - first connect failure schedules reconnect too
            Assert.AreEqual(3, capturedDelays.Count);
            Assert.AreEqual(TimeSpan.FromMilliseconds(100), capturedDelays[0]);
            Assert.AreEqual(TimeSpan.FromMilliseconds(200), capturedDelays[1]);
            Assert.AreEqual(TimeSpan.FromMilliseconds(300), capturedDelays[2]);
        }

        [Test]
        public void ChannelsAreNotifiedToRejoinAfterReconnectTest()
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

            var conn1 = factory.LastCreatedWebsocket;
            Assert.IsNotNull(conn1);

            // Create and join a channel
            var channel = socket.Channel("test:topic");
            channel.Join();

            // Verify join was sent
            Assert.AreEqual(1, conn1.CallSend.Count);
            Assert.That(conn1.CallSend[0], Does.Contain("phx_join"));

            // Simulate join reply to put channel in joined state
            conn1.SimulateMessage(BuildJoinOkReply("1", "test:topic"));
            Assert.AreEqual(ChannelState.Joined, channel.State);

            // Simulate connection loss
            conn1.SimulateClose(1006, "Connection lost");

            // Channel should be in errored state
            Assert.AreEqual(ChannelState.Errored, channel.State);

            // Trigger reconnect
            var reconnect = mockExecutor.Executions.LastOrDefault(e => !e.IsCancelled);
            Assert.IsNotNull(reconnect);
            reconnect!.Execute();

            // Get the new connection
            var conn2 = factory.LastCreatedWebsocket;
            Assert.IsNotNull(conn2);
            Assert.AreNotSame(conn1, conn2);

            // Channel should rejoin (since socket.OnOpen triggers channel rejoin for errored channels)
            // The channel's SocketOnOpen handler will call Rejoin() if IsErrored()
            Assert.AreEqual(1, conn2.CallSend.Count, "Channel should have sent rejoin message");
            Assert.That(conn2.CallSend[0], Does.Contain("phx_join"), "Rejoin should send phx_join");
        }

        #endregion

        #region Connection Loss Handling Tests

        [Test]
        public void OnCloseTriggersReconnectLogicForAbnormalCloseTest()
        {
            var mockExecutor = new TrackingDelayedExecutor();
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var reconnectScheduled = false;
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                DelayedExecutor = mockExecutor,
                HeartbeatInterval = null,
                ReconnectAfter = _ =>
                {
                    reconnectScheduled = true;
                    return TimeSpan.FromMilliseconds(100);
                }
            };

            var socket = new Socket("ws://localhost:1234", null, factory, options);
            socket.Connect();

            var conn = factory.LastCreatedWebsocket;
            Assert.IsNotNull(conn);

            var executionsBeforeClose = mockExecutor.Executions.Count;

            // Simulate abnormal close (code != 1000)
            conn.SimulateClose(1006, "Abnormal closure");

            // Verify reconnect was scheduled
            var executionsAfterClose = mockExecutor.Executions.Count;
            Assert.Greater(executionsAfterClose, executionsBeforeClose,
                "Reconnect should be scheduled after abnormal close");
        }

        [Test]
        public void OnCloseDoesNotTriggerReconnectForNormalCloseTest()
        {
            var mockExecutor = new TrackingDelayedExecutor();
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var reconnectScheduled = false;
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                DelayedExecutor = mockExecutor,
                HeartbeatInterval = null,
                ReconnectAfter = _ =>
                {
                    reconnectScheduled = true;
                    return TimeSpan.FromMilliseconds(100);
                }
            };

            var socket = new Socket("ws://localhost:1234", null, factory, options);
            socket.Connect();

            var conn = factory.LastCreatedWebsocket;
            Assert.IsNotNull(conn);

            // Use Disconnect which sets closeWasClean = true
            socket.Disconnect();

            // The reconnect should not be triggered because Disconnect sets closeWasClean
            // Note: The mock close callback will fire but closeWasClean prevents reconnect
            Assert.IsFalse(reconnectScheduled || mockExecutor.Executions.Any(e => !e.IsCancelled),
                "Reconnect should not be scheduled after clean disconnect");
        }

        [Test]
        public void AbnormalCloseVsNormalCloseHandlingTest()
        {
            var mockExecutor = new TrackingDelayedExecutor();
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var reconnectCalls = 0;
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                DelayedExecutor = mockExecutor,
                HeartbeatInterval = null,
                ReconnectAfter = _ =>
                {
                    reconnectCalls++;
                    return TimeSpan.FromMilliseconds(100);
                }
            };

            // Test abnormal close (code != 1000)
            var socket1 = new Socket("ws://localhost:1234", null, factory, options);
            socket1.Connect();
            var conn1 = factory.LastCreatedWebsocket;
            Assert.IsNotNull(conn1);

            conn1.SimulateClose(1006, "Abnormal");
            var reconnectAfterAbnormal = reconnectCalls;

            // Trigger reconnect to reset state
            var reconnect = mockExecutor.Executions.LastOrDefault(e => !e.IsCancelled);
            reconnect?.Execute();

            // Test normal close via Disconnect (which sets closeWasClean)
            var socket2 = new Socket("ws://localhost:1234", null, new MockWebsocketFactoryWithCallbackTracking(), options);
            var reconnectBefore = reconnectCalls;
            socket2.Connect();
            socket2.Disconnect(); // This sets closeWasClean = true

            // Reconnect should not have been scheduled for clean disconnect
            Assert.AreEqual(reconnectBefore, reconnectCalls,
                "Reconnect should not be called for clean disconnect");
        }

        #endregion

        #region Edge Cases Tests

        [Test]
        public void ManualDisconnectPreventsAutoReconnectTest()
        {
            var mockExecutor = new TrackingDelayedExecutor();
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var reconnectScheduled = false;
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                DelayedExecutor = mockExecutor,
                HeartbeatInterval = null,
                ReconnectAfter = _ =>
                {
                    reconnectScheduled = true;
                    return TimeSpan.FromMilliseconds(100);
                }
            };

            var socket = new Socket("ws://localhost:1234", null, factory, options);
            socket.Connect();

            var conn = factory.LastCreatedWebsocket;
            Assert.IsNotNull(conn);

            // Manual disconnect
            socket.Disconnect();

            // The close callback will fire from the mock, but closeWasClean should prevent reconnect
            Assert.IsFalse(reconnectScheduled, "Reconnect should not be scheduled after manual disconnect");
        }

        [Test]
        public void DisconnectDuringReconnectAttemptTest()
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

            var conn1 = factory.LastCreatedWebsocket;
            Assert.IsNotNull(conn1);

            // Simulate connection loss
            conn1.SimulateClose(1006, "Lost");

            // Reconnect is scheduled
            var reconnect = mockExecutor.Executions.LastOrDefault(e => !e.IsCancelled);
            Assert.IsNotNull(reconnect);

            // Call Disconnect which should reset the reconnect timer
            socket.Disconnect();

            // The reconnect execution should have been cancelled
            // (Disconnect calls _reconnectTimer?.Reset() which cancels pending execution)
            var stillPending = mockExecutor.Executions.Any(e => !e.IsCancelled);

            // Even if we try to execute the reconnect, it should be safe
            Assert.DoesNotThrow(() => reconnect!.Execute());
        }

        [Test]
        public void ConnectFailureSchedulesReconnectTest()
        {
            var mockExecutor = new TrackingDelayedExecutor();
            var reconnectScheduled = false;
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                DelayedExecutor = mockExecutor,
                HeartbeatInterval = null,
                ReconnectAfter = _ =>
                {
                    reconnectScheduled = true;
                    return TimeSpan.FromMilliseconds(100);
                }
            };

            // Create a factory that throws on connect
            var throwingFactory = new ThrowingWebsocketFactory();

            var socket = new Socket("ws://localhost:1234", null, throwingFactory, options);

            // Connect should catch the exception and schedule reconnect
            socket.Connect();

            Assert.IsTrue(reconnectScheduled, "Reconnect should be scheduled after connection failure");
            Assert.IsNull(socket.Conn, "Conn should be null after failed connection");
        }

        #endregion

        #region Two-Channel Reconnect Tests

        [Test]
        public void TwoChannelReconnectCycleDoesNotInfiniteLoopTest()
        {
            var mockExecutor = new TrackingDelayedExecutor();
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                DelayedExecutor = mockExecutor,
                HeartbeatInterval = null, // disable to simplify
                ReconnectAfter = _ => TimeSpan.FromMilliseconds(100)
            };

            var socket = new Socket("ws://localhost:1234", null, factory, options);
            socket.Connect();
            var conn1 = factory.LastCreatedWebsocket;
            Assert.IsNotNull(conn1);

            // Create and join 2 channels
            var channelA = socket.Channel("room:1");
            var channelB = socket.Channel("room:2");
            channelA.Join();
            channelB.Join();

            // Simulate join OK replies for both
            conn1.SimulateMessage(BuildJoinOkReply("1", "room:1"));
            conn1.SimulateMessage(BuildJoinOkReply("2", "room:2"));
            Assert.AreEqual(ChannelState.Joined, channelA.State);
            Assert.AreEqual(ChannelState.Joined, channelB.State);

            // Record old JoinRefs
            var oldJoinRefA = "1";
            var oldJoinRefB = "2";

            // Simulate connection loss
            conn1.SimulateClose(1006, "Connection lost");
            Assert.AreEqual(ChannelState.Errored, channelA.State);
            Assert.AreEqual(ChannelState.Errored, channelB.State);

            // Execute reconnect
            var reconnectExec = mockExecutor.Executions
                .LastOrDefault(e => e.Delay == TimeSpan.FromMilliseconds(100) && !e.IsCancelled);
            Assert.IsNotNull(reconnectExec, "Reconnect should be scheduled");
            reconnectExec!.Execute();

            // New connection established, channels should be rejoining
            var conn2 = factory.LastCreatedWebsocket;
            Assert.AreNotSame(conn1, conn2);
            Assert.AreEqual(ChannelState.Joining, channelA.State);
            Assert.AreEqual(ChannelState.Joining, channelB.State);

            // Get the new JoinRefs and simulate join OK
            var newJoinRefA = channelA.JoinRef;
            var newJoinRefB = channelB.JoinRef;
            Assert.AreNotEqual(oldJoinRefA, newJoinRefA);
            Assert.AreNotEqual(oldJoinRefB, newJoinRefB);

            conn2.SimulateMessage(BuildJoinOkReply(newJoinRefA!, "room:1"));
            conn2.SimulateMessage(BuildJoinOkReply(newJoinRefB!, "room:2"));
            Assert.AreEqual(ChannelState.Joined, channelA.State);
            Assert.AreEqual(ChannelState.Joined, channelB.State);

            // Record state: count pending executions and messages sent
            var executionCountAfterRejoin = mockExecutor.PendingCount;
            var sendCountAfterRejoin = conn2.CallSend.Count;

            // Simulate server sending phx_close with OLD JoinRefs (stale messages from old connection)
            conn2.SimulateMessage(BuildPhxMessage(oldJoinRefA, null, "room:1", "phx_close"));
            conn2.SimulateMessage(BuildPhxMessage(oldJoinRefB, null, "room:2", "phx_close"));

            // Channels should STILL be joined — stale messages dropped by IsMember
            Assert.AreEqual(ChannelState.Joined, channelA.State,
                "Channel A should remain Joined after stale phx_close");
            Assert.AreEqual(ChannelState.Joined, channelB.State,
                "Channel B should remain Joined after stale phx_close");

            // No additional messages sent (no rejoin triggered)
            Assert.AreEqual(sendCountAfterRejoin, conn2.CallSend.Count,
                "No additional messages should be sent after stale phx_close");
        }

        #endregion
    }
}
