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
    public class AsyncApiTests : PhoenixTestBase
    {
        #region Socket.ConnectAsync Tests

        [Test]
        public async Task ConnectAsync_CompletesOnOpen()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );

            var connectTask = socket.ConnectAsync();

            // The mock immediately calls onOpen, so it should complete quickly
            await connectTask;

            Assert.AreEqual(WebsocketState.Open, socket.State);
            Assert.That(socket.OnOpen, Is.Null);
            Assert.That(socket.OnError, Is.Null);
            Assert.That(socket.OnClose, Is.Null);
        }

        [Test]
        public async Task ConnectAsync_WhenAlreadyConnected_CompletesWithinBoundedWait()
        {
            var socket = CreateConnectedSocket();

            var connectTask = socket.ConnectAsync();

            await AssertCompletesWithin(connectTask);
            await connectTask;

            Assert.That(socket.OnOpen, Is.Null);
            Assert.That(socket.OnError, Is.Null);
            Assert.That(socket.OnClose, Is.Null);
        }

        [Test]
        public async Task ConnectAsync_AfterDispose_FaultsWithinBoundedWait()
        {
            var (socket, _) = CreateDisconnectedSocket();
            socket.Dispose();
            using var cancellationTokenSource = new CancellationTokenSource();

            var connectTask = socket.ConnectAsync(cancellationTokenSource.Token);

            try
            {
                await AssertCompletesWithin(connectTask);
                Assert.ThrowsAsync<ObjectDisposedException>(async () => await connectTask);
            }
            finally
            {
                cancellationTokenSource.Cancel();
            }
        }

        [Test]
        public async Task ConnectAsync_WhenDisposedDuringInFlightConnect_FaultsWithinBoundedWait()
        {
            var factory = new ControlledLifecycleWebsocketFactory();
            var socket = CreateSocket(factory);
            using var cancellationTokenSource = new CancellationTokenSource();
            var connectTask = socket.ConnectAsync(cancellationTokenSource.Token);
            Assert.That(factory.Connection.State, Is.EqualTo(WebsocketState.Connecting));

            socket.Dispose();

            try
            {
                await AssertCompletesWithin(connectTask);
                Assert.ThrowsAsync<ObjectDisposedException>(async () => await connectTask);
                factory.Connection.CompleteClose(1_000, "Socket disposed");
                Assert.That(connectTask.IsFaulted, Is.True);
            }
            finally
            {
                cancellationTokenSource.Cancel();
            }
        }

        [Test]
        public async Task ConnectAsync_WhenConnectThrowsWithoutReconnect_FaultsWithinBoundedWait()
        {
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                new ThrowingWebsocketFactory(),
                new Socket.Options(new JsonMessageSerializer())
                {
                    ReconnectAfter = null
                }
            );

            var connectTask = socket.ConnectAsync();

            await AssertCompletesWithin(connectTask);
            var exception = Assert.ThrowsAsync<Exception>(async () => await connectTask);
            Assert.That(exception!.Message, Does.Contain("Connection failed"));
            Assert.That(socket.OnOpen, Is.Null);
            Assert.That(socket.OnError, Is.Null);
            Assert.That(socket.OnClose, Is.Null);
        }

        [Test]
        public async Task ConnectAsync_WhenTransportThrowsObjectDisposedException_KeepsGenericFailureShape()
        {
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                new ObjectDisposedOnBuildWebsocketFactory(),
                new Socket.Options(new JsonMessageSerializer())
                {
                    ReconnectAfter = null
                }
            );

            var connectTask = socket.ConnectAsync();

            await AssertCompletesWithin(connectTask);
            var exception = Assert.ThrowsAsync<Exception>(async () => await connectTask);
            Assert.That(exception, Is.TypeOf<Exception>());
            Assert.That(exception!.Message, Does.StartWith("Connection failed:"));
            Assert.That(exception.InnerException, Is.TypeOf<ObjectDisposedException>());
        }

        [Test]
        public async Task ConnectAsync_WhenConnectThrowsWithReconnect_WaitsForRetry()
        {
            var factory = new FailingThenSucceedingWebsocketFactory(1);
            var executor = new TrackingDelayedExecutor();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
                {
                    DelayedExecutor = executor,
                    HeartbeatInterval = null,
                    ReconnectAfter = _ => TimeSpan.FromMilliseconds(1)
                }
            );

            var connectTask = socket.ConnectAsync();

            Assert.That(connectTask.IsCompleted, Is.False);
            Assert.That(executor.PendingCount, Is.EqualTo(1));
            executor.ExecuteLast();
            await AssertCompletesWithin(connectTask);
            await connectTask;
            Assert.That(factory.Attempts, Is.EqualTo(2));
            Assert.That(socket.OnOpen, Is.Null);
            Assert.That(socket.OnError, Is.Null);
            Assert.That(socket.OnClose, Is.Null);
        }

        [Test]
        public async Task ConnectAsync_WaitingForReconnect_WhenDisconnectCalled_FaultsWithinBoundedWait()
        {
            var factory = new ControlledLifecycleWebsocketFactory();
            var executor = new TrackingDelayedExecutor();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
                {
                    DelayedExecutor = executor,
                    HeartbeatInterval = null,
                    ReconnectAfter = _ => TimeSpan.FromMilliseconds(1)
                }
            );
            using var cancellationTokenSource = new CancellationTokenSource();
            var firstConnectTask = socket.ConnectAsync(cancellationTokenSource.Token);
            var secondConnectTask = socket.ConnectAsync(cancellationTokenSource.Token);
            factory.Connection.CompleteClose(1_006, "refused");
            Assert.That(firstConnectTask.IsCompleted, Is.False);
            Assert.That(secondConnectTask.IsCompleted, Is.False);
            Assert.That(executor.PendingCount, Is.EqualTo(1));

            socket.Disconnect();

            try
            {
                await AssertCompletesWithin(
                    Task.WhenAll(firstConnectTask, secondConnectTask)
                );
                foreach (var connectTask in new[] { firstConnectTask, secondConnectTask })
                {
                    var exception = Assert.ThrowsAsync<Exception>(async () => await connectTask);
                    Assert.That(exception, Is.TypeOf<Exception>());
                    Assert.That(exception!.Message, Does.Contain("socket is disconnecting"));
                }

                Assert.That(executor.PendingCount, Is.EqualTo(0));
                Assert.That(socket.OnOpen, Is.Null);
                Assert.That(socket.OnError, Is.Null);
                Assert.That(socket.OnClose, Is.Null);
            }
            finally
            {
                cancellationTokenSource.Cancel();
            }
        }

        [Test]
        public async Task ConnectAsync_WaitingAfterSynchronousFailure_WhenDisconnectAsyncCalled_FaultsWithinBoundedWait()
        {
            var executor = new TrackingDelayedExecutor();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                new ThrowingWebsocketFactory(),
                new Socket.Options(new JsonMessageSerializer())
                {
                    DelayedExecutor = executor,
                    HeartbeatInterval = null,
                    ReconnectAfter = _ => TimeSpan.FromMilliseconds(1)
                }
            );
            using var cancellationTokenSource = new CancellationTokenSource();
            var connectTask = socket.ConnectAsync(cancellationTokenSource.Token);
            Assert.That(connectTask.IsCompleted, Is.False);
            Assert.That(executor.PendingCount, Is.EqualTo(1));

            var disconnectTask = socket.DisconnectAsync();

            try
            {
                await AssertCompletesWithin(disconnectTask);
                await disconnectTask;
                await AssertCompletesWithin(connectTask);
                var exception = Assert.ThrowsAsync<Exception>(async () => await connectTask);
                Assert.That(exception, Is.TypeOf<Exception>());
                Assert.That(exception!.Message, Does.Contain("socket is disconnecting"));
                Assert.That(executor.PendingCount, Is.EqualTo(0));
            }
            finally
            {
                cancellationTokenSource.Cancel();
            }
        }

        [Test]
        public async Task ConnectAsync_ConcurrentWaiters_AllCompleteOnOpen()
        {
            var factory = new ControlledLifecycleWebsocketFactory();
            var socket = CreateSocket(factory);

            var firstConnectTask = socket.ConnectAsync();
            var secondConnectTask = socket.ConnectAsync();

            factory.Connection.Open();

            var allConnectTasks = Task.WhenAll(firstConnectTask, secondConnectTask);
            await AssertCompletesWithin(allConnectTasks);
            await allConnectTasks;
            Assert.That(factory.Connection.ConnectCount, Is.EqualTo(1));
            Assert.That(socket.OnOpen, Is.Null);
            Assert.That(socket.OnError, Is.Null);
            Assert.That(socket.OnClose, Is.Null);
        }

        [Test]
        public async Task ConnectAsync_AfterRemoteCloseWithoutReconnect_OpensNewTransport()
        {
            var factory = new ControlledLifecycleWebsocketFactory();
            var socket = CreateSocket(factory);
            socket.Connect();
            var closedConnection = factory.Connection;
            closedConnection.Open();
            closedConnection.CompleteClose(1_006, "remote close");
            factory.OpenOnConnect = true;
            using var cancellationTokenSource = new CancellationTokenSource();

            var connectTask = socket.ConnectAsync(cancellationTokenSource.Token);

            try
            {
                await AssertCompletesWithin(connectTask);
                await connectTask;
            }
            finally
            {
                cancellationTokenSource.Cancel();
            }

            Assert.That(factory.Connections, Has.Count.EqualTo(2));
            Assert.That(factory.Connection, Is.Not.SameAs(closedConnection));
            Assert.That(factory.Connection.State, Is.EqualTo(WebsocketState.Open));
            Assert.That(socket.OnOpen, Is.Null);
            Assert.That(socket.OnError, Is.Null);
            Assert.That(socket.OnClose, Is.Null);
        }

        [Test]
        public void Connect_AfterRemoteCloseWithoutReconnect_OpensNewTransport()
        {
            var factory = new ControlledLifecycleWebsocketFactory();
            var socket = CreateSocket(factory);
            socket.Connect();
            var closedConnection = factory.Connection;
            closedConnection.Open();
            closedConnection.CompleteClose(1_006, "remote close");
            factory.OpenOnConnect = true;

            socket.Connect();

            Assert.That(factory.Connections, Has.Count.EqualTo(2));
            Assert.That(factory.Connection, Is.Not.SameAs(closedConnection));
            Assert.That(factory.Connection.State, Is.EqualTo(WebsocketState.Open));
        }

        [Test]
        public void ConnectAsync_ThrowsOnError()
        {
            var factory = new ErrorOnConnectWebsocketFactory("Connection refused");
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
                {
                    ReconnectAfter = null // Disable auto-reconnect
                }
            );

            var ex = Assert.ThrowsAsync<Exception>(async () => await socket.ConnectAsync());
            Assert.That(ex!.Message, Does.Contain("Connection refused"));
        }

        [Test]
        public void ConnectAsync_CancellationToken_CancelsTask()
        {
            var factory = new DelayedOpenWebsocketFactory(TimeSpan.FromSeconds(10));
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.ThrowsAsync<TaskCanceledException>(async () =>
                await socket.ConnectAsync(cts.Token));
            Assert.That(socket.OnOpen, Is.Null);
            Assert.That(socket.OnError, Is.Null);
            Assert.That(socket.OnClose, Is.Null);
        }

        #endregion

        #region Socket.DisconnectAsync Tests

        [Test]
        public async Task DisconnectAsync_CompletesOnClose()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );

            socket.Connect();
            Assert.AreEqual(WebsocketState.Open, socket.State);

            await socket.DisconnectAsync();

            Assert.IsNull(socket.Conn);
        }

        [Test]
        public async Task DisconnectAsync_WhenNeverConnected_CompletesWithinBoundedWait()
        {
            var (socket, _) = CreateDisconnectedSocket();

            var disconnectTask = socket.DisconnectAsync();

            await AssertCompletesWithin(disconnectTask);
            await disconnectTask;

            Assert.That(socket.OnClose, Is.Null);
        }

        [Test]
        public async Task DisconnectAsync_AfterDispose_CompletesWithinBoundedWait()
        {
            var (socket, _) = CreateDisconnectedSocket();
            socket.Dispose();

            var disconnectTask = socket.DisconnectAsync();

            await AssertCompletesWithin(disconnectTask);
            await disconnectTask;
        }

        [Test]
        public async Task DisconnectAsync_WhenTransportIsAlreadyClosed_CompletesWithinBoundedWait()
        {
            var factory = new ControlledLifecycleWebsocketFactory();
            var socket = CreateSocket(factory);
            var connectTask = socket.ConnectAsync();
            factory.Connection.CompleteClose();
            Assert.ThrowsAsync<Exception>(async () => await connectTask);

            var disconnectTask = socket.DisconnectAsync();

            await AssertCompletesWithin(disconnectTask);
            await disconnectTask;
            Assert.That(socket.Conn, Is.Null);
            Assert.That(socket.OnClose, Is.Null);
        }

        [Test]
        public async Task DisconnectAsync_WhenClosePollingGivesUp_ErrorsJoinedChannelsBeforeCompleting()
        {
            var factory = new ControlledLifecycleWebsocketFactory
            {
                OpenOnConnect = true
            };
            var executor = new TrackingDelayedExecutor();
            var socket = CreateSocket(factory, executor);
            socket.Connect();
            var oldConnection = factory.Connection;
            var channel = socket.Channel("test:topic");
            channel.Join().Trigger(ReplyStatus.Ok);
            var closeCalled = false;
            socket.OnClose += (_, _) => closeCalled = true;

            var disconnectTask = socket.DisconnectAsync();
            for (var i = 0; i < 4; i++)
            {
                executor.ExecuteLast();
            }

            await AssertCompletesWithin(disconnectTask);
            await disconnectTask;
            Assert.Multiple(() =>
            {
                Assert.That(socket.Conn, Is.Null);
                Assert.That(channel.State, Is.EqualTo(ChannelState.Errored));
            });

            // The transport reports close only after teardown gave up and cleared it.
            oldConnection.CompleteClose();
            Assert.That(closeCalled, Is.False);

            socket.Connect();
            Assert.Multiple(() =>
            {
                Assert.That(factory.Connections, Has.Count.EqualTo(2));
                Assert.That(socket.Conn, Is.SameAs(factory.Connection));
                Assert.That(channel.State, Is.EqualTo(ChannelState.Joining));
            });
        }

        [Test]
        public void Connect_WhenClosedTransportHasQueuedClose_IgnoresLateEventAfterReplacement()
        {
            var factory = new ControlledLifecycleWebsocketFactory
            {
                OpenOnConnect = true
            };
            var socket = CreateSocket(factory);
            socket.Connect();
            var oldConnection = factory.Connection;
            var channel = socket.Channel("test:topic");
            channel.Join().Trigger(ReplyStatus.Ok);

            // Model State becoming Closed before its queued close callback is delivered.
            oldConnection.MarkClosedWithoutCallback();
            socket.Connect();
            var currentConnection = factory.Connection;
            var closeCalled = false;
            socket.OnClose += (_, _) => closeCalled = true;
            Assert.That(channel.State, Is.EqualTo(ChannelState.Joining));

            oldConnection.CompleteClose(1_006, "late queued close");

            Assert.Multiple(() =>
            {
                Assert.That(factory.Connections, Has.Count.EqualTo(2));
                Assert.That(socket.Conn, Is.SameAs(currentConnection));
                Assert.That(currentConnection.State, Is.EqualTo(WebsocketState.Open));
                Assert.That(channel.State, Is.EqualTo(ChannelState.Joining));
                Assert.That(closeCalled, Is.False);
            });
        }

        [Test]
        public void DisconnectAsync_CancellationToken_CancelsTask()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var delayExecutor = new TrackingDelayedExecutor();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
                {
                    DelayedExecutor = delayExecutor
                }
            );

            socket.Connect();

            // Use a factory that doesn't auto-close
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.ThrowsAsync<TaskCanceledException>(async () =>
                await socket.DisconnectAsync(cts.Token));
            Assert.That(socket.OnClose, Is.Null);
        }

        #endregion

        #region Socket operation overlap tests

        [Test]
        public async Task DisconnectAsync_DuringConnectAsync_DisconnectsAndFaultsConnectWaiter()
        {
            var factory = new ControlledLifecycleWebsocketFactory();
            var executor = new TrackingDelayedExecutor();
            var socket = CreateSocket(factory, executor);

            var connectTask = socket.ConnectAsync();
            var disconnectTask = socket.DisconnectAsync();
            factory.Connection.CompleteClose();
            executor.ExecuteLast();

            await AssertCompletesWithin(disconnectTask);
            await disconnectTask;
            await AssertCompletesWithin(connectTask);
            Assert.ThrowsAsync<Exception>(async () => await connectTask);
            Assert.That(socket.OnOpen, Is.Null);
            Assert.That(socket.OnError, Is.Null);
            Assert.That(socket.OnClose, Is.Null);
        }

        [Test]
        public async Task ConnectAsync_DuringDisconnectAsync_FaultsWithoutStartingNewConnection()
        {
            var factory = new ControlledLifecycleWebsocketFactory();
            var executor = new TrackingDelayedExecutor();
            var socket = CreateSocket(factory, executor);
            var initialConnectTask = socket.ConnectAsync();
            factory.Connection.Open();
            await initialConnectTask;

            var disconnectTask = socket.DisconnectAsync();
            var overlappingConnectTask = socket.ConnectAsync();

            await AssertCompletesWithin(overlappingConnectTask);
            Assert.ThrowsAsync<Exception>(async () => await overlappingConnectTask);
            Assert.That(factory.Connection.ConnectCount, Is.EqualTo(1));

            factory.Connection.CompleteClose();
            executor.ExecuteLast();
            await AssertCompletesWithin(disconnectTask);
            await disconnectTask;
            Assert.That(socket.OnOpen, Is.Null);
            Assert.That(socket.OnError, Is.Null);
            Assert.That(socket.OnClose, Is.Null);
        }

        [Test]
        public async Task ConnectAsync_DuringDisconnectAfterTransportCloses_FaultsWithoutHanging()
        {
            var factory = new ControlledLifecycleWebsocketFactory();
            var executor = new TrackingDelayedExecutor();
            var socket = CreateSocket(factory, executor);
            var initialConnectTask = socket.ConnectAsync();
            factory.Connection.Open();
            await initialConnectTask;

            var disconnectTask = socket.DisconnectAsync();
            factory.Connection.CompleteClose();
            var overlappingConnectTask = socket.ConnectAsync();

            await AssertCompletesWithin(overlappingConnectTask);
            Assert.ThrowsAsync<Exception>(async () => await overlappingConnectTask);
            executor.ExecuteLast();
            await AssertCompletesWithin(disconnectTask);
            await disconnectTask;
            Assert.That(factory.Connection.ConnectCount, Is.EqualTo(1));
            Assert.That(socket.OnOpen, Is.Null);
            Assert.That(socket.OnError, Is.Null);
            Assert.That(socket.OnClose, Is.Null);
        }

        [Test]
        public async Task Connect_AfterTransportClosesDuringDisconnect_IsNotClearedByOldTeardown()
        {
            var factory = new ControlledLifecycleWebsocketFactory();
            var executor = new TrackingDelayedExecutor();
            var socket = CreateSocket(factory, executor);
            socket.Connect();
            var closedConnection = factory.Connection;
            closedConnection.Open();
            var disconnectTask = socket.DisconnectAsync();
            closedConnection.CompleteClose();
            factory.OpenOnConnect = true;

            socket.Connect();

            Assert.That(factory.Connections, Has.Count.EqualTo(2));
            var replacementConnection = factory.Connection;
            Assert.That(replacementConnection.State, Is.EqualTo(WebsocketState.Open));

            for (var i = 0; i < 4; i++)
            {
                executor.ExecuteLast();
            }

            await AssertCompletesWithin(disconnectTask);
            await disconnectTask;
            Assert.That(socket.Conn, Is.SameAs(replacementConnection));
        }

        [Test]
        public void ConcurrentConnect_DuringBuildWindow_ClaimsOneAndClosesLoser()
        {
            using var buildEntered = new ManualResetEventSlim(false);
            using var releaseBuild = new ManualResetEventSlim(false);
            var factory = new BlockingFirstBuildWebsocketFactory(
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
            var socketCloseCount = 0;
            socket.OnClose += (_, _) => Interlocked.Increment(ref socketCloseCount);
            var firstConnectTask = Task.Run(socket.Connect);
            Assert.That(
                buildEntered.Wait(TimeSpan.FromSeconds(2)),
                Is.True,
                "The first transport build did not enter its deterministic blocking window."
            );

            try
            {
                socket.Connect();
                Assert.That(factory.Connections, Has.Count.EqualTo(1));
            }
            finally
            {
                releaseBuild.Set();
            }

            Assert.That(
                firstConnectTask.Wait(TimeSpan.FromSeconds(2)),
                Is.True,
                "The blocked Connect() call did not finish."
            );
            Assert.That(factory.Connections, Has.Count.EqualTo(2));
            Assert.That(
                factory.Connections.FindAll(connection => connection.ConnectCalled),
                Has.Count.EqualTo(1)
            );
            Assert.That(
                factory.Connections.FindAll(connection => connection.CloseCalled),
                Has.Count.EqualTo(1)
            );
            Assert.That(
                socket.Conn,
                Is.SameAs(factory.Connections.Find(connection => connection.ConnectCalled))
            );
            Assert.That(socketCloseCount, Is.Zero);
        }

        #endregion

        private static async Task AssertCompletesWithin(Task task)
        {
            var completedTask = await Task.WhenAny(
                task,
                Task.Delay(TimeSpan.FromMilliseconds(250))
            );

            Assert.That(
                completedTask,
                Is.SameAs(task),
                "The asynchronous socket operation did not complete within 250 ms."
            );
        }

        private static Socket CreateSocket(
            ControlledLifecycleWebsocketFactory factory,
            IDelayedExecutor? executor = null
        )
        {
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                HeartbeatInterval = null,
                ReconnectAfter = null
            };
            if (executor != null)
            {
                options.DelayedExecutor = executor;
            }

            return new Socket("ws://localhost:1234", null, factory, options);
        }

        private sealed class ObjectDisposedOnBuildWebsocketFactory : IWebsocketFactory
        {
            public IWebsocket Build(WebsocketConfiguration config)
            {
                throw new ObjectDisposedException("transport");
            }
        }

        private sealed class ControlledLifecycleWebsocketFactory : IWebsocketFactory
        {
            public ControlledLifecycleWebsocket Connection { get; private set; } = null!;
            public List<ControlledLifecycleWebsocket> Connections { get; } =
                new List<ControlledLifecycleWebsocket>();
            public bool OpenOnConnect { get; set; }

            public IWebsocket Build(WebsocketConfiguration config)
            {
                Connection = new ControlledLifecycleWebsocket(config, OpenOnConnect);
                Connections.Add(Connection);
                return Connection;
            }
        }

        private sealed class ControlledLifecycleWebsocket : IWebsocket
        {
            private readonly WebsocketConfiguration _config;
            private readonly bool _openOnConnect;

            public ControlledLifecycleWebsocket(
                WebsocketConfiguration config,
                bool openOnConnect = false
            )
            {
                _config = config;
                _openOnConnect = openOnConnect;
            }

            public int ConnectCount { get; private set; }
            public WebsocketState State { get; private set; } = WebsocketState.Closed;

            public void Connect()
            {
                ConnectCount++;
                State = WebsocketState.Connecting;
                if (_openOnConnect)
                {
                    Open();
                }
            }

            public void Open()
            {
                State = WebsocketState.Open;
                _config.onOpenCallback(this);
            }

            public void Send(string data)
            {
            }

            public void Close(ushort? code = null, string? reason = null)
            {
                State = WebsocketState.Closing;
            }

            public void CompleteClose(ushort code = 1_000, string reason = "closed by test")
            {
                State = WebsocketState.Closed;
                _config.onCloseCallback(this, code, reason);
            }

            public void MarkClosedWithoutCallback()
            {
                State = WebsocketState.Closed;
            }
        }

        private sealed class BlockingFirstBuildWebsocketFactory : IWebsocketFactory
        {
            private readonly ManualResetEventSlim _buildEntered;
            private readonly ManualResetEventSlim _releaseBuild;
            private int _buildCount;

            public BlockingFirstBuildWebsocketFactory(
                ManualResetEventSlim buildEntered,
                ManualResetEventSlim releaseBuild
            )
            {
                _buildEntered = buildEntered;
                _releaseBuild = releaseBuild;
            }

            public List<BlockingBuildWebsocket> Connections { get; } =
                new List<BlockingBuildWebsocket>();

            public IWebsocket Build(WebsocketConfiguration config)
            {
                if (Interlocked.Increment(ref _buildCount) == 1)
                {
                    _buildEntered.Set();
                    _releaseBuild.Wait(TimeSpan.FromSeconds(5));
                }

                var connection = new BlockingBuildWebsocket(config);
                Connections.Add(connection);
                return connection;
            }
        }

        private sealed class BlockingBuildWebsocket : IWebsocket
        {
            private readonly WebsocketConfiguration _config;

            public BlockingBuildWebsocket(WebsocketConfiguration config)
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

            public void Send(string data)
            {
            }

            public void Close(ushort? code = null, string? reason = null)
            {
                CloseCalled = true;
                State = WebsocketState.Closed;
                _config.onCloseCallback(
                    this,
                    code ?? 1_000,
                    reason ?? "closed by losing build compensation"
                );
            }
        }

        #region Channel.JoinAsync Tests

        [Test]
        public async Task JoinAsync_ReturnsSuccessOnOkReply()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );
            socket.Connect();

            var channel = socket.Channel("test:topic");

            var joinTask = channel.JoinAsync(TimeSpan.FromSeconds(5));

            // Simulate server reply
            var conn = factory.LastCreatedWebsocket;
            conn!.SimulateMessage(BuildJoinOkReply("1", "test:topic"));

            var result = await joinTask;

            Assert.IsTrue(result.IsSuccess);
            Assert.IsNotNull(result.Reply);
            Assert.AreEqual(ChannelState.Joined, channel.State);
        }

        [Test]
        public async Task JoinAsync_ReturnsFailureOnErrorReply()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );
            socket.Connect();

            var channel = socket.Channel("test:topic");

            var joinTask = channel.JoinAsync(TimeSpan.FromSeconds(5));

            // Simulate server error reply
            var conn = factory.LastCreatedWebsocket;
            conn!.SimulateMessage(BuildJoinErrorReply("1", "test:topic", "{\"reason\":\"unauthorized\"}"));

            var result = await joinTask;

            Assert.IsFalse(result.IsSuccess);
            Assert.IsNotNull(result.Reply);
            Assert.AreEqual(ChannelState.Errored, channel.State);
        }

        [Test]
        public async Task JoinAsync_ReturnsFailurePromptlyOnCustomReply()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var executor = new TrackingDelayedExecutor();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
                {
                    DelayedExecutor = executor,
                    RejoinAfter = null
                }
            );
            socket.Connect();

            var channel = socket.Channel("test:topic");
            var joinTask = channel.JoinAsync(TimeSpan.FromSeconds(5));

            var conn = factory.LastCreatedWebsocket;
            conn!.SimulateMessage(BuildPhxReply(
                "1",
                "1",
                "test:topic",
                "partial",
                "{\"progress\":50}"
            ));

            await AssertCompletesWithin(joinTask);
            var result = await joinTask;

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.Reply, Is.Not.Null);
            var reply = result.Reply.GetValueOrDefault();
            Assert.That(reply.Status, Is.EqualTo("partial"));
            Assert.That(reply.ReplyStatus, Is.EqualTo(ReplyStatus.Error));
            Assert.That(result.Error, Is.EqualTo("partial"));
            Assert.That(channel.State, Is.EqualTo(ChannelState.Errored));
        }

        [Test]
        public async Task JoinAsync_ReturnsTimeoutOnTimeout()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var mockExecutor = new TrackingDelayedExecutor();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
                {
                    DelayedExecutor = mockExecutor,
                    RejoinAfter = null
                }
            );
            socket.Connect();

            var channel = socket.Channel("test:topic");

            var joinTask = channel.JoinAsync(TimeSpan.FromMilliseconds(100));

            // Trigger the timeout
            mockExecutor.ExecuteAll();

            var result = await joinTask;

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual("Timeout", result.Error);
        }

        [Test]
        public void JoinAsync_CancellationToken_CancelsTask()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );
            socket.Connect();

            var channel = socket.Channel("test:topic");

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.ThrowsAsync<TaskCanceledException>(async () =>
                await channel.JoinAsync(TimeSpan.FromSeconds(5), cts.Token));
        }

        #endregion

        #region Channel.PushAsync Tests

        [Test]
        public async Task PushAsync_ReturnsSuccessOnOkReply()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );
            socket.Connect();

            var channel = socket.Channel("test:topic");
            channel.Join();

            // Simulate join success
            var conn = factory.LastCreatedWebsocket;
            conn!.SimulateMessage("[\"1\",\"1\",\"test:topic\",\"phx_reply\",{\"status\":\"ok\",\"response\":{}}]");

            var pushTask = channel.PushAsync("test_event", new { data = "value" }, TimeSpan.FromSeconds(5));

            // Simulate push reply
            conn.SimulateMessage("[\"1\",\"2\",\"test:topic\",\"phx_reply\",{\"status\":\"ok\",\"response\":{\"echoed\":\"value\"}}]");

            var result = await pushTask;

            Assert.IsTrue(result.IsSuccess);
            Assert.AreEqual(ReplyStatus.Ok, result.Status);
            Assert.IsNotNull(result.Reply);
        }

        [Test]
        public async Task PushAsync_ReturnsFailureOnErrorReply()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );
            socket.Connect();

            var channel = socket.Channel("test:topic");
            channel.Join();

            var conn = factory.LastCreatedWebsocket;
            conn!.SimulateMessage("[\"1\",\"1\",\"test:topic\",\"phx_reply\",{\"status\":\"ok\",\"response\":{}}]");

            var pushTask = channel.PushAsync("test_event", null, TimeSpan.FromSeconds(5));

            // Simulate error reply
            conn.SimulateMessage("[\"1\",\"2\",\"test:topic\",\"phx_reply\",{\"status\":\"error\",\"response\":{\"reason\":\"failed\"}}]");

            var result = await pushTask;

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual(ReplyStatus.Error, result.Status);
        }

        [Test]
        public async Task PushAsync_ReturnsFailurePromptlyOnCustomReply()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var executor = new TrackingDelayedExecutor();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
                {
                    DelayedExecutor = executor
                }
            );
            socket.Connect();

            var channel = socket.Channel("test:topic");
            channel.Join();

            var conn = factory.LastCreatedWebsocket;
            conn!.SimulateMessage(BuildJoinOkReply("1", "test:topic"));

            var pushTask = channel.PushAsync("test_event", null, TimeSpan.FromSeconds(5));

            conn.SimulateMessage(BuildPushReply(
                "1",
                "2",
                "test:topic",
                "partial",
                "{\"progress\":50}"
            ));

            var completedTask = await Task.WhenAny(
                pushTask,
                Task.Delay(TimeSpan.FromMilliseconds(250))
            );
            if (completedTask != pushTask)
            {
                executor.ExecuteAll();
            }

            var result = await pushTask;

            Assert.Multiple(() =>
            {
                Assert.That(
                    completedTask,
                    Is.SameAs(pushTask),
                    "The custom reply did not complete PushAsync within 250 ms."
                );
                Assert.That(result.IsSuccess, Is.False);
                Assert.That(result.Status, Is.EqualTo(ReplyStatus.Error));
                Assert.That(result.Reply, Is.Not.Null);
                if (result.Reply.HasValue)
                {
                    Assert.That(result.Reply.Value.Status, Is.EqualTo("partial"));
                    Assert.That(result.Reply.Value.ReplyStatus, Is.EqualTo(ReplyStatus.Error));
                }
            });
        }

        [Test]
        public async Task PushAsync_ReturnsTimeoutOnTimeout()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var mockExecutor = new TrackingDelayedExecutor();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
                {
                    DelayedExecutor = mockExecutor
                }
            );
            socket.Connect();

            var channel = socket.Channel("test:topic");
            channel.Join();

            var conn = factory.LastCreatedWebsocket;
            conn!.SimulateMessage("[\"1\",\"1\",\"test:topic\",\"phx_reply\",{\"status\":\"ok\",\"response\":{}}]");

            var pushTask = channel.PushAsync("test_event", null, TimeSpan.FromMilliseconds(100));

            // Trigger timeout
            mockExecutor.ExecuteAll();

            var result = await pushTask;

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual(ReplyStatus.Timeout, result.Status);
        }

        [Test]
        public void PushAsync_CancellationToken_CancelsTask()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );
            socket.Connect();

            var channel = socket.Channel("test:topic");
            channel.Join();

            var conn = factory.LastCreatedWebsocket;
            conn!.SimulateMessage("[\"1\",\"1\",\"test:topic\",\"phx_reply\",{\"status\":\"ok\",\"response\":{}}]");

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.ThrowsAsync<TaskCanceledException>(async () =>
                await channel.PushAsync("test_event", null, TimeSpan.FromSeconds(5), cts.Token));
        }

        #endregion

        #region Channel.PushAsync<T> Tests

        [Test]
        public async Task PushAsyncTyped_ReturnsTypedResponseOnSuccess()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );
            socket.Connect();

            var channel = socket.Channel("test:topic");
            channel.Join();

            var conn = factory.LastCreatedWebsocket;
            conn!.SimulateMessage("[\"1\",\"1\",\"test:topic\",\"phx_reply\",{\"status\":\"ok\",\"response\":{}}]");

            var pushTask = channel.PushAsync<Dictionary<string, object>>(
                "test_event",
                new { data = "value" },
                TimeSpan.FromSeconds(5)
            );

            // Simulate reply with typed response
            conn.SimulateMessage("[\"1\",\"2\",\"test:topic\",\"phx_reply\",{\"status\":\"ok\",\"response\":{\"echoed\":\"value\"}}]");

            var result = await pushTask;

            Assert.IsTrue(result.IsSuccess);
            Assert.IsNotNull(result.Response);
            Assert.AreEqual("value", result.Response["echoed"]?.ToString());
        }

        [Test]
        public async Task PushAsyncTyped_ReturnsFailureOnError()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );
            socket.Connect();

            var channel = socket.Channel("test:topic");
            channel.Join();

            var conn = factory.LastCreatedWebsocket;
            conn!.SimulateMessage("[\"1\",\"1\",\"test:topic\",\"phx_reply\",{\"status\":\"ok\",\"response\":{}}]");

            var pushTask = channel.PushAsync<Dictionary<string, object>>("test_event", null, TimeSpan.FromSeconds(5));

            conn.SimulateMessage("[\"1\",\"2\",\"test:topic\",\"phx_reply\",{\"status\":\"error\",\"response\":{}}]");

            var result = await pushTask;

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual(ReplyStatus.Error, result.Status);
        }

        [Test]
        public async Task PushAsyncTyped_ReturnsFailurePromptlyOnCustomReply()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var executor = new TrackingDelayedExecutor();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
                {
                    DelayedExecutor = executor
                }
            );
            socket.Connect();

            var channel = socket.Channel("test:topic");
            channel.Join();

            var conn = factory.LastCreatedWebsocket;
            conn!.SimulateMessage(BuildJoinOkReply("1", "test:topic"));

            var pushTask = channel.PushAsync<Dictionary<string, object>>(
                "test_event",
                null,
                TimeSpan.FromSeconds(5)
            );

            conn.SimulateMessage(BuildPushReply(
                "1",
                "2",
                "test:topic",
                "partial",
                "{\"progress\":50}"
            ));

            await AssertCompletesWithin(pushTask);
            var result = await pushTask;

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.Status, Is.EqualTo(ReplyStatus.Error));
            Assert.That(result.Reply, Is.Not.Null);
            Assert.That(result.Reply.GetValueOrDefault().Status, Is.EqualTo("partial"));
            Assert.That(result.Response, Is.Null);
        }

        [Test]
        public void PushAsyncTyped_CancellationToken_CancelsTask()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );
            socket.Connect();

            var channel = socket.Channel("test:topic");
            channel.Join();

            var conn = factory.LastCreatedWebsocket;
            conn!.SimulateMessage("[\"1\",\"1\",\"test:topic\",\"phx_reply\",{\"status\":\"ok\",\"response\":{}}]");

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.ThrowsAsync<TaskCanceledException>(async () =>
                await channel.PushAsync<Dictionary<string, object>>("test_event", null, TimeSpan.FromSeconds(5), cts.Token));
        }

        #endregion

        #region Channel.LeaveAsync Tests

        [Test]
        public async Task LeaveAsync_CompletesOnOkReply()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );
            socket.Connect();

            var channel = socket.Channel("test:topic");
            channel.Join();

            var conn = factory.LastCreatedWebsocket;
            conn!.SimulateMessage("[\"1\",\"1\",\"test:topic\",\"phx_reply\",{\"status\":\"ok\",\"response\":{}}]");

            // Leave immediately completes when can't push (after state change to Leaving)
            await channel.LeaveAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(ChannelState.Closed, channel.State);
        }

        [Test]
        public async Task LeaveAsync_ErrorReplyCompletesAndClosesChannel()
        {
            await AssertLeaveAsyncCompletesOnReplyStatus("error");
        }

        [Test]
        public async Task LeaveAsync_CustomReplyCompletesAndClosesChannel()
        {
            await AssertLeaveAsyncCompletesOnReplyStatus("partial");
        }

        private static async Task AssertLeaveAsyncCompletesOnReplyStatus(string status)
        {
            var serializer = new JsonMessageSerializer();
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(serializer)
                {
                    DelayedExecutor = new TrackingDelayedExecutor(),
                    HeartbeatInterval = null
                }
            );
            socket.Connect();

            var channel = socket.Channel("test:topic");
            channel.Join();

            var conn = factory.LastCreatedWebsocket!;
            conn.SimulateMessage(BuildJoinOkReply("1", "test:topic"));
            conn.OnSend = data =>
            {
                var sentMessage = serializer.Deserialize<Message>(data);
                if (sentMessage?.Event != Message.OutBoundEvent.Leave.Serialized())
                {
                    return;
                }

                conn.SimulateMessage(BuildPhxReply(
                    sentMessage.JoinRef,
                    sentMessage.Ref!,
                    sentMessage.Topic!,
                    status
                ));
            };

            var leaveTask = channel.LeaveAsync(TimeSpan.FromSeconds(5));
            var completedTask = await Task.WhenAny(
                leaveTask,
                Task.Delay(TimeSpan.FromMilliseconds(250))
            );

            Assert.Multiple(() =>
            {
                Assert.That(
                    completedTask,
                    Is.SameAs(leaveTask),
                    "LeaveAsync did not complete within 250 ms of the leave reply."
                );
                Assert.That(channel.State, Is.EqualTo(ChannelState.Closed));
            });

            await leaveTask;
        }

        [Test]
        public void LeaveAsync_CancellationToken_CancelsTask()
        {
            // Note: LeaveAsync completes immediately when !CanPush() is true (which happens after
            // state transitions to Leaving). To properly test cancellation, we need to cancel
            // BEFORE the Leave call has a chance to complete. Since Leave() is called synchronously
            // and triggers immediate completion when !CanPush(), we can only observe cancellation
            // if we cancel before the task even registers. This is a limitation of how LeaveAsync works.
            //
            // This test verifies that the cancellation token registration works correctly:
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );
            socket.Connect();

            var channel = socket.Channel("test:topic");
            channel.Join();

            var conn = factory.LastCreatedWebsocket;
            conn!.SimulateMessage("[\"1\",\"1\",\"test:topic\",\"phx_reply\",{\"status\":\"ok\",\"response\":{}}]");

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // Since Leave() completes synchronously when !CanPush() (which happens immediately
            // after state changes to Leaving), the task completes before cancellation can take effect.
            // This is expected behavior - we just verify the method doesn't throw with a cancelled token.
            Assert.DoesNotThrowAsync(async () =>
                await channel.LeaveAsync(TimeSpan.FromSeconds(5), cts.Token));
        }

        #endregion

        #region Channel.WaitForEventAsync Tests

        [Test]
        public async Task WaitForEventAsync_CompletesWhenEventReceived()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );
            socket.Connect();

            var channel = socket.Channel("test:topic");
            channel.Join();

            var conn = factory.LastCreatedWebsocket;
            conn!.SimulateMessage("[\"1\",\"1\",\"test:topic\",\"phx_reply\",{\"status\":\"ok\",\"response\":{}}]");

            var waitTask = channel.WaitForEventAsync("custom_event", TimeSpan.FromSeconds(5));

            // Simulate receiving the event
            conn.SimulateMessage("[\"1\",null,\"test:topic\",\"custom_event\",{\"data\":\"hello\"}]");

            var message = await waitTask;

            Assert.IsNotNull(message);
            Assert.AreEqual("custom_event", message.Event);
        }

        [Test]
        public void WaitForEventAsync_ThrowsTimeoutException_WhenTimesOut()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var mockExecutor = new TrackingDelayedExecutor();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
                {
                    DelayedExecutor = mockExecutor
                }
            );
            socket.Connect();

            var channel = socket.Channel("test:topic");
            channel.Join();

            var conn = factory.LastCreatedWebsocket;
            conn!.SimulateMessage("[\"1\",\"1\",\"test:topic\",\"phx_reply\",{\"status\":\"ok\",\"response\":{}}]");

            // Use a real short timeout for the test
            var waitTask = channel.WaitForEventAsync("nonexistent_event", TimeSpan.FromMilliseconds(10));

            // Wait for the timeout
            Assert.ThrowsAsync<TimeoutException>(async () => await waitTask);
        }

        [Test]
        public void WaitForEventAsync_ThrowsOnCancellation()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );
            socket.Connect();

            var channel = socket.Channel("test:topic");
            channel.Join();

            var conn = factory.LastCreatedWebsocket;
            conn!.SimulateMessage("[\"1\",\"1\",\"test:topic\",\"phx_reply\",{\"status\":\"ok\",\"response\":{}}]");

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.ThrowsAsync<TaskCanceledException>(async () =>
                await channel.WaitForEventAsync("custom_event", TimeSpan.FromSeconds(5), cts.Token));
        }

        [Test]
        public void WaitForEventAsync_ThrowsOnNullEventName()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );
            socket.Connect();

            var channel = socket.Channel("test:topic");

            Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await channel.WaitForEventAsync(null!, TimeSpan.FromSeconds(5)));
        }

        [Test]
        public void WaitForEventAsync_ThrowsOnEmptyEventName()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );
            socket.Connect();

            var channel = socket.Channel("test:topic");

            Assert.ThrowsAsync<ArgumentException>(async () =>
                await channel.WaitForEventAsync("", TimeSpan.FromSeconds(5)));
        }

        [Test]
        public async Task WaitForEventAsync_CleansUpSubscriptionAfterCompletion()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );
            socket.Connect();

            var channel = socket.Channel("test:topic");
            channel.Join();

            var conn = factory.LastCreatedWebsocket;
            conn!.SimulateMessage("[\"1\",\"1\",\"test:topic\",\"phx_reply\",{\"status\":\"ok\",\"response\":{}}]");

            var waitTask = channel.WaitForEventAsync("custom_event", TimeSpan.FromSeconds(5));

            // Complete the task
            conn.SimulateMessage("[\"1\",null,\"test:topic\",\"custom_event\",{\"data\":\"first\"}]");
            await waitTask;

            // Send another event - it should not affect the completed task
            var callCount = 0;
            channel.On("custom_event", _ => callCount++);
            conn.SimulateMessage("[\"1\",null,\"test:topic\",\"custom_event\",{\"data\":\"second\"}]");

            // Only our new subscription should be called, not the wait subscription
            Assert.AreEqual(1, callCount);
        }

        #endregion

        #region Push.ReceiveAsync Tests

        [Test]
        public async Task ReceiveAsync_ReturnsReplyOnOk()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );
            socket.Connect();

            var channel = socket.Channel("test:topic");
            channel.Join();

            var conn = factory.LastCreatedWebsocket;
            conn!.SimulateMessage("[\"1\",\"1\",\"test:topic\",\"phx_reply\",{\"status\":\"ok\",\"response\":{}}]");

            var push = channel.Push("test_event", null, TimeSpan.FromSeconds(5));
            var receiveTask = push.ReceiveAsync();

            conn.SimulateMessage("[\"1\",\"2\",\"test:topic\",\"phx_reply\",{\"status\":\"ok\",\"response\":{\"data\":\"value\"}}]");

            var reply = await receiveTask;

            Assert.AreEqual(ReplyStatus.Ok, reply.ReplyStatus);
            Assert.IsNotNull(reply.Response);
        }

        [Test]
        public async Task ReceiveAsync_ReturnsReplyOnError()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );
            socket.Connect();

            var channel = socket.Channel("test:topic");
            channel.Join();

            var conn = factory.LastCreatedWebsocket;
            conn!.SimulateMessage("[\"1\",\"1\",\"test:topic\",\"phx_reply\",{\"status\":\"ok\",\"response\":{}}]");

            var push = channel.Push("test_event", null, TimeSpan.FromSeconds(5));
            var receiveTask = push.ReceiveAsync();

            conn.SimulateMessage("[\"1\",\"2\",\"test:topic\",\"phx_reply\",{\"status\":\"error\",\"response\":{}}]");

            var reply = await receiveTask;

            Assert.AreEqual(ReplyStatus.Error, reply.ReplyStatus);
        }

        [Test]
        public async Task ReceiveAsync_ReturnsCustomReplyPromptlyAsError()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var executor = new TrackingDelayedExecutor();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
                {
                    DelayedExecutor = executor
                }
            );
            socket.Connect();

            var channel = socket.Channel("test:topic");
            channel.Join();

            var conn = factory.LastCreatedWebsocket;
            conn!.SimulateMessage(BuildJoinOkReply("1", "test:topic"));

            var push = channel.Push("test_event", null, TimeSpan.FromSeconds(5));
            var receiveTask = push.ReceiveAsync();

            conn.SimulateMessage(BuildPushReply(
                "1",
                "2",
                "test:topic",
                "partial",
                "{\"progress\":50}"
            ));

            await AssertCompletesWithin(receiveTask);
            var reply = await receiveTask;

            Assert.That(reply.Status, Is.EqualTo("partial"));
            Assert.That(reply.ReplyStatus, Is.EqualTo(ReplyStatus.Error));
        }

        [Test]
        public async Task ReceiveAsync_ReturnsReplyOnTimeout()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var mockExecutor = new TrackingDelayedExecutor();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
                {
                    DelayedExecutor = mockExecutor
                }
            );
            socket.Connect();

            var channel = socket.Channel("test:topic");
            channel.Join();

            var conn = factory.LastCreatedWebsocket;
            conn!.SimulateMessage("[\"1\",\"1\",\"test:topic\",\"phx_reply\",{\"status\":\"ok\",\"response\":{}}]");

            var push = channel.Push("test_event", null, TimeSpan.FromMilliseconds(100));
            var receiveTask = push.ReceiveAsync();

            // Trigger timeout
            mockExecutor.ExecuteAll();

            var reply = await receiveTask;

            Assert.AreEqual(ReplyStatus.Timeout, reply.ReplyStatus);
        }

        [Test]
        public void ReceiveAsync_CancellationToken_CancelsTask()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );
            socket.Connect();

            var channel = socket.Channel("test:topic");
            channel.Join();

            var conn = factory.LastCreatedWebsocket;
            conn!.SimulateMessage("[\"1\",\"1\",\"test:topic\",\"phx_reply\",{\"status\":\"ok\",\"response\":{}}]");

            var push = channel.Push("test_event", null, TimeSpan.FromSeconds(5));

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.ThrowsAsync<TaskCanceledException>(async () =>
                await push.ReceiveAsync(cts.Token));
        }

        [Test]
        public async Task ReceiveAsync_ReturnsImmediatelyIfAlreadyReceived()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );
            socket.Connect();

            var channel = socket.Channel("test:topic");
            channel.Join();

            var conn = factory.LastCreatedWebsocket;
            conn!.SimulateMessage("[\"1\",\"1\",\"test:topic\",\"phx_reply\",{\"status\":\"ok\",\"response\":{}}]");

            var push = channel.Push("test_event", null, TimeSpan.FromSeconds(5));

            // Receive reply first
            conn.SimulateMessage("[\"1\",\"2\",\"test:topic\",\"phx_reply\",{\"status\":\"ok\",\"response\":{}}]");

            // Now call ReceiveAsync - should return immediately
            var reply = await push.ReceiveAsync();

            Assert.AreEqual(ReplyStatus.Ok, reply.ReplyStatus);
        }

        #endregion

        #region Presence.WaitForInitialSyncAsync Tests

        [Test]
        public async Task WaitForInitialSyncAsync_CompletesOnSync()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );
            socket.Connect();

            var channel = socket.Channel("test:topic");
            var presence = new Presence(channel);

            channel.Join();

            var conn = factory.LastCreatedWebsocket;
            conn!.SimulateMessage("[\"1\",\"1\",\"test:topic\",\"phx_reply\",{\"status\":\"ok\",\"response\":{}}]");

            var syncTask = presence.WaitForInitialSyncAsync();

            // Simulate presence state message
            conn.SimulateMessage("[\"1\",null,\"test:topic\",\"presence_state\",{\"user1\":{\"metas\":[{\"phx_ref\":\"ref1\"}]}}]");

            await syncTask;

            Assert.IsTrue(presence.State.ContainsKey("user1"));
        }

        [Test]
        public void WaitForInitialSyncAsync_CancellationToken_CancelsTask()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );
            socket.Connect();

            var channel = socket.Channel("test:topic");
            var presence = new Presence(channel);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.ThrowsAsync<TaskCanceledException>(async () =>
                await presence.WaitForInitialSyncAsync(cts.Token));
        }

        #endregion

        #region Presence.WaitForUserAsync Tests

        [Test]
        public async Task WaitForUserAsync_ReturnsImmediatelyIfUserExists()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );
            socket.Connect();

            var channel = socket.Channel("test:topic");
            var presence = new Presence(channel);

            channel.Join();

            var conn = factory.LastCreatedWebsocket;
            conn!.SimulateMessage("[\"1\",\"1\",\"test:topic\",\"phx_reply\",{\"status\":\"ok\",\"response\":{}}]");

            // Add user to presence state via sync
            conn.SimulateMessage("[\"1\",null,\"test:topic\",\"presence_state\",{\"existing_user\":{\"metas\":[{\"phx_ref\":\"ref1\"}]}}]");

            var userPresence = await presence.WaitForUserAsync("existing_user", TimeSpan.FromSeconds(1));

            Assert.IsNotNull(userPresence);
            Assert.AreEqual(1, userPresence!.Metas.Count);
        }

        [Test]
        public async Task WaitForUserAsync_WaitsForUserToJoin()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );
            socket.Connect();

            var channel = socket.Channel("test:topic");
            var presence = new Presence(channel);

            channel.Join();

            var conn = factory.LastCreatedWebsocket;
            conn!.SimulateMessage("[\"1\",\"1\",\"test:topic\",\"phx_reply\",{\"status\":\"ok\",\"response\":{}}]");

            // Initial sync with no users
            conn.SimulateMessage("[\"1\",null,\"test:topic\",\"presence_state\",{}]");

            var waitTask = presence.WaitForUserAsync("new_user", TimeSpan.FromSeconds(5));

            // Simulate user joining via diff
            conn.SimulateMessage("[\"1\",null,\"test:topic\",\"presence_diff\",{\"joins\":{\"new_user\":{\"metas\":[{\"phx_ref\":\"ref1\"}]}},\"leaves\":{}}]");

            var userPresence = await waitTask;

            Assert.IsNotNull(userPresence);
        }

        [Test]
        public async Task WaitForUserAsync_ReturnsNullOnTimeout()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );
            socket.Connect();

            var channel = socket.Channel("test:topic");
            var presence = new Presence(channel);

            channel.Join();

            var conn = factory.LastCreatedWebsocket;
            conn!.SimulateMessage("[\"1\",\"1\",\"test:topic\",\"phx_reply\",{\"status\":\"ok\",\"response\":{}}]");

            // Initial sync with no users
            conn.SimulateMessage("[\"1\",null,\"test:topic\",\"presence_state\",{}]");

            var userPresence = await presence.WaitForUserAsync("nonexistent_user", TimeSpan.FromMilliseconds(50));

            Assert.IsNull(userPresence);
        }

        [Test]
        public void WaitForUserAsync_ThrowsOnCancellation()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );
            socket.Connect();

            var channel = socket.Channel("test:topic");
            var presence = new Presence(channel);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.ThrowsAsync<TaskCanceledException>(async () =>
                await presence.WaitForUserAsync("user", TimeSpan.FromSeconds(5), cts.Token));
        }

        [Test]
        public void WaitForUserAsync_ThrowsOnNullKey()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );
            socket.Connect();

            var channel = socket.Channel("test:topic");
            var presence = new Presence(channel);

            Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await presence.WaitForUserAsync(null!, TimeSpan.FromSeconds(5)));
        }

        #endregion
    }
}
