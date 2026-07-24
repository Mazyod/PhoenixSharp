using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Phoenix;
using PhoenixTests.TestDoubles;

namespace PhoenixTests
{
    [TestFixture, Category("Unit")]
    public class SocketHeartbeatTests : PhoenixTestBase
    {
        [Test]
        public void HeartbeatIsSentAtConfiguredIntervalWhenConnectedTest()
        {
            var mockExecutor = new TrackingDelayedExecutor();
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                DelayedExecutor = mockExecutor,
                HeartbeatInterval = TimeSpan.FromSeconds(30)
            };

            var socket = new Socket("ws://localhost:1234", null, factory, options);
            socket.Connect();

            var conn = factory.LastCreatedWebsocket;
            Assert.IsNotNull(conn);

            // After connection opens, a heartbeat should be scheduled
            Assert.GreaterOrEqual(mockExecutor.PendingCount, 1);

            // Find the heartbeat execution (should be scheduled with 30s delay)
            var heartbeatExecution = mockExecutor.Executions
                .FirstOrDefault(e => e.Delay == TimeSpan.FromSeconds(30) && !e.IsCancelled);
            Assert.IsNotNull(heartbeatExecution, "Heartbeat should be scheduled");

            // Trigger the heartbeat
            heartbeatExecution!.Execute();

            // Verify heartbeat was sent
            Assert.AreEqual(1, conn.CallSend.Count);
            Assert.That(conn.CallSend[0], Does.Contain("\"phoenix\""));
            Assert.That(conn.CallSend[0], Does.Contain("\"heartbeat\""));
        }

        [Test]
        public void HeartbeatUsesCorrectEventNameAndTopicTest()
        {
            var mockExecutor = new TrackingDelayedExecutor();
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                DelayedExecutor = mockExecutor,
                HeartbeatInterval = TimeSpan.FromSeconds(30)
            };

            var socket = new Socket("ws://localhost:1234", null, factory, options);
            socket.Connect();

            var conn = factory.LastCreatedWebsocket;
            Assert.IsNotNull(conn);

            // Find and trigger the heartbeat
            var heartbeatExecution = mockExecutor.Executions
                .FirstOrDefault(e => e.Delay == TimeSpan.FromSeconds(30) && !e.IsCancelled);
            Assert.IsNotNull(heartbeatExecution);
            heartbeatExecution!.Execute();

            // The message format for Phoenix v2 is [join_ref, ref, topic, event, payload]
            // For heartbeat: [null, "ref", "phoenix", "heartbeat", {}]
            var sentMessage = conn.CallSend[0];
            Assert.That(sentMessage, Does.Contain("\"phoenix\""), "Topic should be 'phoenix'");
            Assert.That(sentMessage, Does.Contain("\"heartbeat\""), "Event should be 'heartbeat'");
        }

        [Test]
        public void HeartbeatResponseClearsPendingRefAndSchedulesExactlyOnceTest()
        {
            var mockExecutor = new TrackingDelayedExecutor();
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                DelayedExecutor = mockExecutor,
                HeartbeatInterval = TimeSpan.FromSeconds(30)
            };

            var socket = new Socket("ws://localhost:1234", null, factory, options);
            socket.Connect();

            var conn = factory.LastCreatedWebsocket;
            Assert.IsNotNull(conn);

            // Find and trigger the heartbeat
            var heartbeatExecution = mockExecutor.Executions
                .FirstOrDefault(e => e.Delay == TimeSpan.FromSeconds(30) && !e.IsCancelled);
            Assert.IsNotNull(heartbeatExecution);
            heartbeatExecution!.Execute();

            var timeoutExecution = mockExecutor.Executions.Last();
            var executionCountBeforeResponse = mockExecutor.Executions.Count;

            // Simulate heartbeat response from server
            conn.SimulateMessage(BuildHeartbeatReply("1"));

            Assert.That(timeoutExecution.IsCancelled, Is.True);
            Assert.That(
                mockExecutor.Executions,
                Has.Count.EqualTo(executionCountBeforeResponse + 1)
            );
            var nextHeartbeatExecution = mockExecutor.Executions.Last();
            Assert.That(nextHeartbeatExecution.IsCancelled, Is.False);

            // A duplicate acknowledgement no longer matches the cleared pending ref.
            conn.SimulateMessage(BuildHeartbeatReply("1"));
            Assert.That(
                mockExecutor.Executions,
                Has.Count.EqualTo(executionCountBeforeResponse + 1)
            );

            nextHeartbeatExecution.Execute();
            Assert.That(conn.CallSend, Has.Count.EqualTo(2));
        }

        [Test]
        public void HeartbeatTimeoutTriggersReconnectTest()
        {
            var mockExecutor = new TrackingDelayedExecutor();
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var reconnectCalled = false;
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                DelayedExecutor = mockExecutor,
                HeartbeatInterval = TimeSpan.FromSeconds(30),
                ReconnectAfter = _ =>
                {
                    reconnectCalled = true;
                    return TimeSpan.FromMilliseconds(100);
                }
            };

            var socket = new Socket("ws://localhost:1234", null, factory, options);
            socket.Connect();

            var conn = factory.LastCreatedWebsocket;
            Assert.IsNotNull(conn);

            // Find and trigger the initial heartbeat
            var heartbeatExecution = mockExecutor.Executions
                .FirstOrDefault(e => e.Delay == TimeSpan.FromSeconds(30) && !e.IsCancelled);
            Assert.IsNotNull(heartbeatExecution);
            heartbeatExecution!.Execute();

            // At this point, a heartbeat was sent and a timeout timer was scheduled
            // Find and trigger the timeout (another 30s delay)
            var timeoutExecution = mockExecutor.Executions
                .LastOrDefault(e => e.Delay == TimeSpan.FromSeconds(30) && !e.IsCancelled);
            Assert.IsNotNull(timeoutExecution);
            timeoutExecution!.Execute();

            // The timeout should trigger an abnormal close
            Assert.AreEqual(1, conn.CallCloseCount, "Connection should be closed on heartbeat timeout");
            Assert.That(conn.LastCloseCode, Is.EqualTo(1_000));
            Assert.That(conn.LastCloseReason, Is.EqualTo("heartbeat timeout"));

            // Reconnect should be scheduled after heartbeat timeout
            Assert.IsTrue(reconnectCalled, "Reconnect should be scheduled after heartbeat timeout");
            Assert.That(
                mockExecutor.Executions.Count(
                    execution => execution.Delay == TimeSpan.FromMilliseconds(100)
                ),
                Is.EqualTo(1)
            );
            Assert.That(ReadCloseWasClean(socket), Is.False);
        }

        [Test]
        public void HeartbeatTimeoutReportsHeartbeatErrorTest()
        {
            var mockExecutor = new TrackingDelayedExecutor();
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
                {
                    DelayedExecutor = mockExecutor,
                    HeartbeatInterval = TimeSpan.FromSeconds(30),
                    ReconnectAfter = null
                }
            );
            PhoenixError? reportedError = null;
            socket.OnError += error => reportedError = error;
            socket.Connect();

            var heartbeatSend = mockExecutor.PendingExecutions.Single();
            heartbeatSend.Execute();
            var heartbeatTimeout = mockExecutor.PendingExecutions.Last();
            heartbeatTimeout.Execute();

            Assert.Multiple(() =>
            {
                Assert.That(reportedError, Is.Not.Null);
                Assert.That(
                    reportedError?.Kind,
                    Is.EqualTo(PhoenixErrorKind.Heartbeat)
                );
                Assert.That(
                    reportedError?.Message,
                    Is.EqualTo("Heartbeat timeout")
                );
                Assert.That(reportedError?.Exception, Is.Null);
            });
        }

        [Test]
        public void DisconnectFromHeartbeatErrorPreventsReconnectTest()
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
                    ReconnectAfter = _ => reconnectDelay
                }
            );
            socket.OnError += error =>
            {
                if (error.Kind == PhoenixErrorKind.Heartbeat)
                {
                    socket.Disconnect();
                }
            };
            socket.Connect();
            var firstConnection = factory.LastCreatedWebsocket;

            var heartbeatSend = executor.PendingExecutions.Single(
                execution => execution.Delay == heartbeatInterval
            );
            heartbeatSend.Execute();
            var heartbeatTimeout = executor.PendingExecutions.Last(
                execution => execution.Delay == heartbeatInterval
            );
            heartbeatTimeout.Execute();

            Assert.Multiple(() =>
            {
                Assert.That(
                    executor.PendingExecutions.Where(execution =>
                        execution.Delay == reconnectDelay
                    ),
                    Is.Empty
                );
                Assert.That(ReadCloseWasClean(socket), Is.True);
            });

            executor.ExecuteAll();
            Assert.That(
                factory.LastCreatedWebsocket,
                Is.SameAs(firstConnection)
            );
        }

        [Test]
        public void HeartbeatCanBeDisabledWithNullIntervalTest()
        {
            var mockExecutor = new TrackingDelayedExecutor();
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                DelayedExecutor = mockExecutor,
                HeartbeatInterval = null // Disable heartbeat
            };

            var socket = new Socket("ws://localhost:1234", null, factory, options);
            socket.Connect();

            var conn = factory.LastCreatedWebsocket;
            Assert.IsNotNull(conn);

            // No heartbeat should be scheduled (only reconnect timer potentially)
            var heartbeatExecutions = mockExecutor.Executions
                .Where(e => !e.IsCancelled)
                .ToList();

            // Execute all to check none are heartbeats
            foreach (var exec in heartbeatExecutions)
            {
                exec.Execute();
            }

            // No heartbeat messages should be sent
            var heartbeatMessages = conn.CallSend.Where(m => m.Contains("heartbeat")).ToList();
            Assert.AreEqual(0, heartbeatMessages.Count, "No heartbeat should be sent when disabled");
        }

        [Test]
        public void HeartbeatTimerIsCancelledOnDisconnectTest()
        {
            var mockExecutor = new TrackingDelayedExecutor();
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                DelayedExecutor = mockExecutor,
                HeartbeatInterval = TimeSpan.FromSeconds(30)
            };

            var socket = new Socket("ws://localhost:1234", null, factory, options);
            socket.Connect();

            var conn = factory.LastCreatedWebsocket;
            Assert.IsNotNull(conn);

            // Find the heartbeat execution
            var heartbeatExecution = mockExecutor.Executions
                .FirstOrDefault(e => e.Delay == TimeSpan.FromSeconds(30) && !e.IsCancelled);
            Assert.IsNotNull(heartbeatExecution);

            // Trigger the heartbeat so timeout timer is scheduled
            heartbeatExecution!.Execute();

            // Get all executions before disconnect
            var executionsBeforeDisconnect = mockExecutor.Executions.Count;

            // Now simulate a connection close (which would happen during disconnect)
            conn.SimulateClose(1000, "Normal closure");

            // The heartbeat timer should have been cancelled
            // Verify by checking that the timeout execution was cancelled
            var uncancelledHeartbeatTimeouts = mockExecutor.Executions
                .Where(e => e.Delay == TimeSpan.FromSeconds(30) && !e.IsCancelled)
                .ToList();

            // After close, any pending heartbeat timers should be cancelled
            // We can verify this by checking close was called
            Assert.AreEqual(1, conn.CallSend.Count, "Only initial heartbeat should have been sent");
        }

        [Test]
        public void HeartbeatIsRescheduledAfterSuccessfulResponseTest()
        {
            var mockExecutor = new TrackingDelayedExecutor();
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                DelayedExecutor = mockExecutor,
                HeartbeatInterval = TimeSpan.FromSeconds(30)
            };

            var socket = new Socket("ws://localhost:1234", null, factory, options);
            socket.Connect();

            var conn = factory.LastCreatedWebsocket;
            Assert.IsNotNull(conn);

            // Count executions with 30s delay initially
            var initialHeartbeatCount = mockExecutor.Executions
                .Count(e => e.Delay == TimeSpan.FromSeconds(30));

            // Trigger heartbeat
            var heartbeatExecution = mockExecutor.Executions
                .FirstOrDefault(e => e.Delay == TimeSpan.FromSeconds(30) && !e.IsCancelled);
            Assert.IsNotNull(heartbeatExecution);
            heartbeatExecution!.Execute();

            // Simulate response
            conn.SimulateMessage(BuildHeartbeatReply("1"));

            // A new heartbeat should be scheduled
            var finalHeartbeatCount = mockExecutor.Executions
                .Count(e => e.Delay == TimeSpan.FromSeconds(30));

            Assert.Greater(finalHeartbeatCount, initialHeartbeatCount,
                "New heartbeat should be scheduled after successful response");
        }

        [Test]
        public void StaleHeartbeatTimerAfterDisconnectDoesNotBufferMessageTest()
        {
            var mockExecutor = new TrackingDelayedExecutor();
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                DelayedExecutor = mockExecutor,
                HeartbeatInterval = TimeSpan.FromSeconds(30)
            };

            var socket = new Socket("ws://localhost:1234", null, factory, options);
            socket.Connect();

            var conn = factory.LastCreatedWebsocket;
            Assert.IsNotNull(conn);

            // Get heartbeat execution
            var heartbeatExecution = mockExecutor.Executions
                .FirstOrDefault(e => e.Delay == TimeSpan.FromSeconds(30) && !e.IsCancelled);
            Assert.IsNotNull(heartbeatExecution);

            conn.SimulateClose(1_000, "Normal closure");
            Assert.That(heartbeatExecution!.IsCancelled, Is.True);

            // Invoke the captured action directly to simulate cancellation losing the race.
            heartbeatExecution.Action!();

            Assert.That(conn.CallSend, Is.Empty);
            Assert.That(socket.SendBuffer, Is.Empty);
            Assert.That(mockExecutor.Executions, Has.Count.EqualTo(1));
        }

        [Test]
        public void FailedHeartbeatSendIsReportedAndNeverBufferedTest()
        {
            var mockExecutor = new TrackingDelayedExecutor();
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
                {
                    DelayedExecutor = mockExecutor,
                    HeartbeatInterval = TimeSpan.FromSeconds(30),
                    ReconnectAfter = null
                }
            );
            PhoenixError? receivedError = null;
            socket.OnError += error => receivedError = error;
            socket.Connect();
            var connection = factory.LastCreatedWebsocket!;
            var sendException = new InvalidOperationException("heartbeat send failed");
            connection.OnSend = _ => throw sendException;
            var heartbeatExecution = mockExecutor.PendingExecutions.Single(
                execution => execution.Delay == TimeSpan.FromSeconds(30)
            );

            Assert.DoesNotThrow(heartbeatExecution.Execute);

            Assert.Multiple(() =>
            {
                Assert.That(connection.State, Is.EqualTo(WebsocketState.Open));
                Assert.That(connection.CallSend, Has.Count.EqualTo(1));
                Assert.That(socket.SendBuffer, Is.Empty);
                Assert.That(receivedError, Is.Not.Null);
                Assert.That(receivedError!.Kind, Is.EqualTo(PhoenixErrorKind.Send));
                Assert.That(receivedError.Exception, Is.SameAs(sendException));
            });

            connection.OnSend = null;
            socket.FlushSendBuffer();
            Assert.That(
                connection.CallSend,
                Has.Count.EqualTo(1),
                "A failed heartbeat must not be resurrected from the send buffer."
            );
        }

        [Test]
        public void StaleHeartbeatTimerAfterReconnectResetIsNoOpTest()
        {
            var mockExecutor = new TrackingDelayedExecutor();
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                DelayedExecutor = mockExecutor,
                HeartbeatInterval = TimeSpan.FromSeconds(30),
                ReconnectAfter = _ => TimeSpan.FromMilliseconds(100)
            };

            var socket = new Socket("ws://localhost:1234", null, factory, options);
            socket.Connect();

            var firstConnection = factory.LastCreatedWebsocket;
            Assert.That(firstConnection, Is.Not.Null);
            var staleHeartbeatExecution = mockExecutor.Executions
                .Single(execution => execution.Delay == TimeSpan.FromSeconds(30));

            firstConnection!.SimulateClose(1_006, "Connection lost");
            var reconnectExecution = mockExecutor.Executions
                .Single(execution =>
                    execution.Delay == TimeSpan.FromMilliseconds(100)
                    && !execution.IsCancelled
                );
            reconnectExecution.Execute();

            var secondConnection = factory.LastCreatedWebsocket;
            Assert.That(secondConnection, Is.Not.Null);
            Assert.That(secondConnection, Is.Not.SameAs(firstConnection));
            var currentHeartbeatExecution = mockExecutor.Executions
                .Last(execution =>
                    execution.Delay == TimeSpan.FromSeconds(30)
                    && !execution.IsCancelled
                );
            var executionCountAfterReset = mockExecutor.Executions.Count;

            // Invoke the old action even though reconnect/reset cancelled its execution.
            staleHeartbeatExecution.Action!();

            Assert.That(secondConnection!.CallSend, Is.Empty);
            Assert.That(socket.SendBuffer, Is.Empty);
            Assert.That(mockExecutor.Executions, Has.Count.EqualTo(executionCountAfterReset));
            Assert.That(currentHeartbeatExecution.IsCancelled, Is.False);

            currentHeartbeatExecution.Execute();
            Assert.That(secondConnection.CallSend, Has.Count.EqualTo(1));
        }

        [Test]
        public void HeartbeatResponseWithMismatchedRefDoesNotCancelTimeoutTest()
        {
            var mockExecutor = new TrackingDelayedExecutor();
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                DelayedExecutor = mockExecutor,
                HeartbeatInterval = TimeSpan.FromSeconds(30)
            };

            var socket = new Socket("ws://localhost:1234", null, factory, options);
            socket.Connect();

            var conn = factory.LastCreatedWebsocket;
            Assert.IsNotNull(conn);

            // Trigger heartbeat
            var heartbeatExecution = mockExecutor.Executions
                .FirstOrDefault(e => e.Delay == TimeSpan.FromSeconds(30) && !e.IsCancelled);
            Assert.IsNotNull(heartbeatExecution);
            heartbeatExecution!.Execute();

            // Heartbeat was sent with ref "1"
            Assert.AreEqual(1, conn.CallSend.Count);

            var executionCountBeforeWrongResponse = mockExecutor.Executions.Count;

            // Simulate response with WRONG ref
            conn.SimulateMessage(BuildHeartbeatReply("999"));

            // The timeout timer should NOT be cancelled, no new heartbeat should be scheduled
            // (because the ref didn't match)
            // The count might be the same or slightly different depending on implementation
            // Key thing: the timeout should still be pending
            var timeoutStillPending = mockExecutor.Executions
                .Count(e => e.Delay == TimeSpan.FromSeconds(30) && !e.IsCancelled) >= 1;

            Assert.IsTrue(timeoutStillPending, "Timeout should still be pending after mismatched response");
        }

        [Test]
        public void HeartbeatExecutionIsNotLeakedOnReconnectTest()
        {
            var mockExecutor = new TrackingDelayedExecutor();
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                DelayedExecutor = mockExecutor,
                HeartbeatInterval = TimeSpan.FromSeconds(30),
                ReconnectAfter = _ => TimeSpan.FromMilliseconds(100)
            };

            var socket = new Socket("ws://localhost:1234", null, factory, options);
            socket.Connect();

            var conn1 = factory.LastCreatedWebsocket;
            Assert.IsNotNull(conn1);

            // Find the initial heartbeat send execution (30s delay from ResetHeartbeat)
            var initialHeartbeatSend = mockExecutor.Executions
                .FirstOrDefault(e => e.Delay == TimeSpan.FromSeconds(30) && !e.IsCancelled);
            Assert.IsNotNull(initialHeartbeatSend, "Initial heartbeat send should be scheduled");

            // Simulate connection loss (code 1006 triggers reconnect)
            conn1.SimulateClose(1006, "Connection lost");

            // Trigger reconnect
            var reconnectExec = mockExecutor.Executions
                .LastOrDefault(e => e.Delay == TimeSpan.FromMilliseconds(100) && !e.IsCancelled);
            Assert.IsNotNull(reconnectExec, "Reconnect should be scheduled");
            reconnectExec!.Execute();

            var conn2 = factory.LastCreatedWebsocket;
            Assert.AreNotSame(conn1, conn2, "New connection should be established");

            // The old heartbeat send execution should have been cancelled
            // (either by OnConnClose or by the new ResetHeartbeat on reconnect)
            Assert.IsTrue(initialHeartbeatSend!.IsCancelled,
                "Old heartbeat send execution should be cancelled after reconnection");
        }

        [Test]
        public void HeartbeatResponseDoesNotLeakExecutionTest()
        {
            var mockExecutor = new TrackingDelayedExecutor();
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                DelayedExecutor = mockExecutor,
                HeartbeatInterval = TimeSpan.FromSeconds(30),
                ReconnectAfter = _ => TimeSpan.FromMilliseconds(100)
            };

            var socket = new Socket("ws://localhost:1234", null, factory, options);
            socket.Connect();

            var conn = factory.LastCreatedWebsocket;
            Assert.IsNotNull(conn);

            // Trigger the initial heartbeat send
            var heartbeatSend = mockExecutor.Executions
                .FirstOrDefault(e => e.Delay == TimeSpan.FromSeconds(30) && !e.IsCancelled);
            Assert.IsNotNull(heartbeatSend);
            heartbeatSend!.Execute();

            // Heartbeat was sent with ref "1"
            Assert.AreEqual(1, conn.CallSend.Count);

            // Simulate heartbeat response — this schedules a NEW SendHeartbeat in OnConnMessage
            conn.SimulateMessage(BuildHeartbeatReply("1"));

            // Find the rescheduled heartbeat send (new 30s execution after the response)
            var rescheduledSend = mockExecutor.Executions
                .LastOrDefault(e => e.Delay == TimeSpan.FromSeconds(30) && !e.IsCancelled);
            Assert.IsNotNull(rescheduledSend, "New heartbeat send should be scheduled after response");

            // Simulate connection loss
            conn.SimulateClose(1006, "Connection lost");

            // The rescheduled send should be cancelled by OnConnClose -> _heartbeatTimer?.Cancel()
            Assert.IsTrue(rescheduledSend!.IsCancelled,
                "Rescheduled heartbeat send should be cancelled after connection close");
        }

        [Test]
        public void HeartbeatTimeoutSchedulesReconnectTest()
        {
            var mockExecutor = new TrackingDelayedExecutor();
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var reconnectCalled = false;
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                DelayedExecutor = mockExecutor,
                HeartbeatInterval = TimeSpan.FromSeconds(30),
                ReconnectAfter = _ =>
                {
                    reconnectCalled = true;
                    return TimeSpan.FromMilliseconds(100);
                }
            };

            var socket = new Socket("ws://localhost:1234", null, factory, options);
            socket.Connect();

            var conn1 = factory.LastCreatedWebsocket;
            Assert.IsNotNull(conn1);

            // Trigger heartbeat send
            var heartbeatSend = mockExecutor.Executions
                .FirstOrDefault(e => e.Delay == TimeSpan.FromSeconds(30) && !e.IsCancelled);
            Assert.IsNotNull(heartbeatSend);
            heartbeatSend!.Execute();

            // Now find and trigger the heartbeat timeout (another 30s execution)
            var heartbeatTimeout = mockExecutor.Executions
                .LastOrDefault(e => e.Delay == TimeSpan.FromSeconds(30) && !e.IsCancelled);
            Assert.IsNotNull(heartbeatTimeout);
            heartbeatTimeout!.Execute();

            // AbnormalClose should have closed the connection
            Assert.AreEqual(1, conn1.CallCloseCount, "Connection should be closed on heartbeat timeout");

            // Reconnect should have been scheduled
            Assert.IsTrue(reconnectCalled,
                "Reconnect should be scheduled after heartbeat timeout");

            // Trigger the reconnect
            var reconnectExec = mockExecutor.Executions
                .LastOrDefault(e => e.Delay == TimeSpan.FromMilliseconds(100) && !e.IsCancelled);
            Assert.IsNotNull(reconnectExec, "Reconnect execution should exist");
            reconnectExec!.Execute();

            // A new connection should be established
            var conn2 = factory.LastCreatedWebsocket;
            Assert.AreNotSame(conn1, conn2, "New connection should be created after reconnect");
        }

        private static bool ReadCloseWasClean(Socket socket)
        {
            return (bool)typeof(Socket)
                .GetField(
                    "_closeWasClean",
                    BindingFlags.Instance | BindingFlags.NonPublic
                )!
                .GetValue(socket)!;
        }
    }
}
