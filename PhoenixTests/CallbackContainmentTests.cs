using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Phoenix;
using PhoenixTests.TestDoubles;

namespace PhoenixTests
{
    [TestFixture, Category("Unit")]
    public sealed class CallbackContainmentTests : PhoenixTestBase
    {
        [Test]
        public async Task ThrowingEarlyOnOpenDoesNotStarveConnectAsyncWaiterTest()
        {
            var (socket, _) = CreateDisconnectedSocket();
            using (socket)
            using (var cancellationTokenSource = new CancellationTokenSource())
            {
                var callbackException = new InvalidOperationException(
                    "early OnOpen failed"
                );
                var unhandledErrors = new List<PhoenixError>();
                socket.OnUnhandledError += unhandledErrors.Add;
                socket.OnOpen += () => throw callbackException;

                var connectTask = socket.ConnectAsync(
                    cancellationTokenSource.Token
                );

                try
                {
                    await AssertCompletesWithin(connectTask);
                    await connectTask;
                }
                finally
                {
                    cancellationTokenSource.Cancel();
                }

                Assert.That(
                    unhandledErrors.Exists(error =>
                        error.Kind == PhoenixErrorKind.Dispatch
                        && ReferenceEquals(
                            error.Exception,
                            callbackException
                        )
                    ),
                    Is.True
                );
            }
        }

        [Test]
        public async Task ThrowingEarlyReceiveHookDoesNotStarveReceiveAsyncWaiterTest()
        {
            var (channel, _, _) = CreateJoinedChannel();
            using (channel.Socket)
            using (var cancellationTokenSource = new CancellationTokenSource())
            {
                var callbackException = new InvalidOperationException(
                    "early receive hook failed"
                );
                var unhandledErrors = new List<PhoenixError>();
                channel.Socket.OnUnhandledError += unhandledErrors.Add;
                var push = channel.Push("contained_reply");
                push.Receive(
                    ReplyStatus.Ok,
                    _ => throw callbackException
                );
                var receiveTask = push.ReceiveAsync(
                    cancellationTokenSource.Token
                );

                push.Trigger(ReplyStatus.Ok);

                try
                {
                    await AssertCompletesWithin(receiveTask);
                    Assert.That(
                        (await receiveTask).ReplyStatus,
                        Is.EqualTo(ReplyStatus.Ok)
                    );
                }
                finally
                {
                    cancellationTokenSource.Cancel();
                }

                Assert.That(
                    unhandledErrors.Exists(error =>
                        error.Kind == PhoenixErrorKind.Dispatch
                        && ReferenceEquals(
                            error.Exception,
                            callbackException
                        )
                    ),
                    Is.True
                );
            }
        }

        [Test]
        public async Task ThrowingEarlyOnSyncDoesNotStarveInitialSyncWaiterTest()
        {
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                DelayedExecutor = new MockDelayedExecutor(),
                HeartbeatInterval = null,
                ReconnectAfter = null,
                RejoinAfter = null
            };
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            using (var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                options
            ))
            using (var cancellationTokenSource = new CancellationTokenSource())
            {
                socket.Connect();
                var channel = socket.Channel("presence:contained");
                var presence = new Presence(channel);
                var joinPush = channel.Join();
                joinPush.Trigger(ReplyStatus.Ok);
                var callbackException = new InvalidOperationException(
                    "early OnSync failed"
                );
                var unhandledErrors = new List<PhoenixError>();
                socket.OnUnhandledError += unhandledErrors.Add;
                presence.OnSync += () => throw callbackException;
                var syncTask = presence.WaitForInitialSyncAsync(
                    cancellationTokenSource.Token
                );

                channel.Trigger(new Message(
                    @event: "presence_state",
                    payload: JsonBox.Serialize(
                        new Dictionary<string, PresencePayload>()
                    )
                ));

                try
                {
                    await AssertCompletesWithin(syncTask);
                    await syncTask;
                }
                finally
                {
                    cancellationTokenSource.Cancel();
                }

                Assert.That(
                    unhandledErrors.Exists(error =>
                        error.Kind == PhoenixErrorKind.Dispatch
                        && ReferenceEquals(
                            error.Exception,
                            callbackException
                        )
                    ),
                    Is.True
                );
            }
        }

        [Test]
        public void PushTimeoutDispatchUsesSocketContainmentTest()
        {
            var executor = new MockDelayedExecutor();
            using var socket = new Socket(
                "ws://localhost:1234",
                null,
                new MockWebsocketFactoryWithCallbackTracking(),
                new Socket.Options(new JsonMessageSerializer())
                {
                    DelayedExecutor = executor,
                    HeartbeatInterval = null,
                    ReconnectAfter = null,
                    RejoinAfter = null
                }
            );
            var dispatchException = new InvalidOperationException(
                "timeout dispatch failed"
            );
            var channel = new ThrowingOnMessageChannel(
                socket,
                dispatchException
            );
            var errors = new List<PhoenixError>();
            socket.OnError += errors.Add;
            var push = new Push(
                channel,
                "timeout_dispatch",
                null,
                TimeSpan.FromSeconds(1)
            );
            push.StartTimeout();

            Assert.DoesNotThrow(executor.ExecutePending);
            Assert.That(
                errors.Exists(error =>
                    error.Kind == PhoenixErrorKind.Dispatch
                    && ReferenceEquals(
                        error.Exception,
                        dispatchException
                    )
                ),
                Is.True
            );
        }

        private static async Task AssertCompletesWithin(Task task)
        {
            var completedTask = await Task.WhenAny(
                task,
                Task.Delay(TimeSpan.FromSeconds(1))
            );
            Assert.That(
                completedTask,
                Is.SameAs(task),
                "The internal completion handler was starved."
            );
        }

        private sealed class ThrowingOnMessageChannel : Channel
        {
            private readonly Exception _exception;

            public ThrowingOnMessageChannel(
                Socket socket,
                Exception exception
            ) : base("push:timeout-dispatch", null, socket)
            {
                _exception = exception;
            }

            public override IJsonBox? OnMessage(Message message)
            {
                throw _exception;
            }
        }
    }
}
