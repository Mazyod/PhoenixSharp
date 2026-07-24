using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
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
        public void DisposeDuringBuildClosesTransportClaimedAfterDisposeTest()
        {
            using var buildEntered = new ManualResetEventSlim(false);
            using var releaseBuild = new ManualResetEventSlim(false);
            var factory = new DisposeDuringBuildWebsocketFactory(
                buildEntered,
                releaseBuild
            );
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
                {
                    HeartbeatInterval = null,
                    ReconnectAfter = null
                }
            );
            var connectTask = Task.Run(socket.Connect);

            try
            {
                Assert.That(
                    buildEntered.Wait(TimeSpan.FromSeconds(2)),
                    Is.True,
                    "Connect did not reach the deterministic Build window."
                );
                socket.Dispose();
            }
            finally
            {
                releaseBuild.Set();
            }

            Assert.That(
                connectTask.Wait(TimeSpan.FromSeconds(2)),
                Is.True,
                "Connect did not return after Build was released."
            );
            Assert.Multiple(() =>
            {
                Assert.That(socket.Conn, Is.Null);
                Assert.That(factory.Connection, Is.Not.Null);
                Assert.That(factory.Connection!.ConnectCalled, Is.False);
                Assert.That(factory.Connection.CloseCalled, Is.True);
            });
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
        public void ChannelCreationLosingRaceWithDisposeThrowsTest()
        {
            var socket = CreateSocket();
            var channelsLock = typeof(Socket)
                .GetField("_channelsLock", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(socket)!;
            Task<Channel> channelCreationTask;

            lock (channelsLock)
            {
                channelCreationTask = Task.Run(() => socket.Channel("test"));
                Assert.That(
                    SpinWait.SpinUntil(
                        () => socket.OnOpen != null,
                        TimeSpan.FromSeconds(1)
                    ),
                    Is.True,
                    "Channel construction did not reach its socket delegate registration."
                );

                // Monitor locks are reentrant, so Dispose copies and clears the channel
                // list while Channel() is blocked at its own lock acquisition.
                socket.Dispose();
            }

            Assert.ThrowsAsync<ObjectDisposedException>(async () =>
                await channelCreationTask);
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

        [Test]
        public void DisposeInvalidatesCapturedHeartbeatTimeoutTest()
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
            var connection = factory.LastCreatedWebsocket!;
            var heartbeat = mockExecutor.Executions
                .Single(execution => execution.Delay == TimeSpan.FromSeconds(30));
            heartbeat.Execute();
            var heartbeatTimeout = mockExecutor.Executions.Last();

            // Avoid an on-close callback so Dispose itself must invalidate the generation.
            connection.MockState = WebsocketState.Closed;
            socket.Dispose();
            var executionCountAfterDispose = mockExecutor.Executions.Count;

            // Invoke the action directly to model cancellation losing the timer race.
            heartbeatTimeout.Action!();

            Assert.That(heartbeatTimeout.IsCancelled, Is.True);
            Assert.That(
                mockExecutor.Executions,
                Has.Count.EqualTo(executionCountAfterDispose)
            );
        }

        private sealed class DisposeDuringBuildWebsocketFactory : IWebsocketFactory
        {
            private readonly ManualResetEventSlim _buildEntered;
            private readonly ManualResetEventSlim _releaseBuild;

            public DisposeDuringBuildWebsocketFactory(
                ManualResetEventSlim buildEntered,
                ManualResetEventSlim releaseBuild
            )
            {
                _buildEntered = buildEntered;
                _releaseBuild = releaseBuild;
            }

            public DisposeDuringBuildWebsocket? Connection { get; private set; }

            public IWebsocket Build(WebsocketConfiguration config)
            {
                _buildEntered.Set();
                _releaseBuild.Wait(TimeSpan.FromSeconds(5));
                Connection = new DisposeDuringBuildWebsocket(config);
                return Connection;
            }
        }

        private sealed class DisposeDuringBuildWebsocket : IWebsocket
        {
            private readonly WebsocketConfiguration _config;

            public DisposeDuringBuildWebsocket(WebsocketConfiguration config)
            {
                _config = config;
            }

            public bool CloseCalled { get; private set; }
            public bool ConnectCalled { get; private set; }
            public WebsocketState State { get; private set; } = WebsocketState.Closed;

            public void Connect()
            {
                ConnectCalled = true;
                State = WebsocketState.Connecting;
            }

            public void Send(string message)
            {
            }

            public void Close(ushort? code = null, string? message = null)
            {
                CloseCalled = true;
                State = WebsocketState.Closed;
                _config.onCloseCallback(this, code ?? 1_000, message ?? "closed");
            }
        }
    }
}
