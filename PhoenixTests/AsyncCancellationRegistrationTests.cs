using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Phoenix;
using PhoenixTests.TestDoubles;

namespace PhoenixTests
{
    [TestFixture, Category("Unit")]
    public sealed class AsyncCancellationRegistrationTests : PhoenixTestBase
    {
        private const BindingFlags InstanceFields =
            BindingFlags.Instance
            | BindingFlags.Public
            | BindingFlags.NonPublic;

        [Test]
        public async Task JoinAsyncNormalCompletionDisposesCancellationRegistrationTest()
        {
            var (channel, websocket) = CreateChannel();
            using var cancellationSource = new CancellationTokenSource();

            var joinTask = channel.JoinAsync(
                TimeSpan.FromSeconds(5),
                cancellationSource.Token
            );

            Assert.That(
                CountActiveRegistrations(cancellationSource),
                Is.EqualTo(1)
            );
            websocket.SimulateMessage(
                BuildJoinOkReply(channel.JoinRef!, channel.Topic)
            );
            var result = await joinTask;

            Assert.Multiple(() =>
            {
                Assert.That(result.IsSuccess, Is.True);
                Assert.That(
                    CountActiveRegistrations(cancellationSource),
                    Is.Zero
                );
            });
        }

        [Test]
        public async Task PushAsyncNormalCompletionDisposesCancellationRegistrationTest()
        {
            var (channel, websocket) = CreateJoinedChannel();
            using var cancellationSource = new CancellationTokenSource();

            var pushTask = channel.PushAsync(
                "test_event",
                null,
                TimeSpan.FromSeconds(5),
                cancellationSource.Token
            );

            Assert.That(
                CountActiveRegistrations(cancellationSource),
                Is.EqualTo(1)
            );
            SimulateLastPushReply(channel, websocket);
            var result = await pushTask;

            Assert.Multiple(() =>
            {
                Assert.That(result.IsSuccess, Is.True);
                Assert.That(
                    CountActiveRegistrations(cancellationSource),
                    Is.Zero
                );
            });
        }

        [Test]
        public async Task PushAsyncTypedNormalCompletionDisposesCancellationRegistrationTest()
        {
            var (channel, websocket) = CreateJoinedChannel();
            using var cancellationSource = new CancellationTokenSource();

            var pushTask = channel.PushAsync<Dictionary<string, object>>(
                "test_event",
                null,
                TimeSpan.FromSeconds(5),
                cancellationSource.Token
            );

            Assert.That(
                CountActiveRegistrations(cancellationSource),
                Is.EqualTo(1)
            );
            SimulateLastPushReply(
                channel,
                websocket,
                "{\"answer\":\"yes\"}"
            );
            var result = await pushTask;

            Assert.Multiple(() =>
            {
                Assert.That(result.IsSuccess, Is.True);
                Assert.That(
                    CountActiveRegistrations(cancellationSource),
                    Is.Zero
                );
            });
        }

        [Test]
        public async Task LeaveAsyncNormalCompletionDisposesCancellationRegistrationTest()
        {
            var (channel, _) = CreateJoinedChannel();
            using var cancellationSource = new CancellationTokenSource();

            await channel.LeaveAsync(
                TimeSpan.FromSeconds(5),
                cancellationSource.Token
            );

            Assert.That(
                CountActiveRegistrations(cancellationSource),
                Is.Zero
            );
        }

        [Test]
        public async Task ReceiveAsyncNormalCompletionDisposesCancellationRegistrationTest()
        {
            var (channel, _) = CreateJoinedChannel();
            using var cancellationSource = new CancellationTokenSource();
            var push = channel.Push(
                "test_event",
                null,
                TimeSpan.FromSeconds(5)
            );

            var receiveTask = push.ReceiveAsync(cancellationSource.Token);

            Assert.That(
                CountActiveRegistrations(cancellationSource),
                Is.EqualTo(1)
            );
            push.Trigger(ReplyStatus.Ok);
            var reply = await receiveTask;

            Assert.Multiple(() =>
            {
                Assert.That(reply.ReplyStatus, Is.EqualTo(ReplyStatus.Ok));
                Assert.That(
                    CountActiveRegistrations(cancellationSource),
                    Is.Zero
                );
            });
        }

        private static (
            Channel Channel,
            MockWebsocketAdapterWithCallbacks Websocket
        ) CreateChannel()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
                {
                    DelayedExecutor = new TrackingDelayedExecutor(),
                    HeartbeatInterval = null,
                    ReconnectAfter = null,
                    RejoinAfter = null
                }
            );
            socket.Connect();

            return (
                socket.Channel("test:topic"),
                factory.LastCreatedWebsocket!
            );
        }

        private static (
            Channel Channel,
            MockWebsocketAdapterWithCallbacks Websocket
        ) CreateJoinedChannel()
        {
            var (channel, websocket) = CreateChannel();
            channel.Join();
            websocket.SimulateMessage(
                BuildJoinOkReply(channel.JoinRef!, channel.Topic)
            );
            return (channel, websocket);
        }

        private static void SimulateLastPushReply(
            Channel channel,
            MockWebsocketAdapterWithCallbacks websocket,
            string response = "{}"
        )
        {
            var serializer = new JsonMessageSerializer();
            var sentMessage = serializer.Deserialize<Message>(
                websocket.CallSend[websocket.CallSend.Count - 1]
            )!;
            websocket.SimulateMessage(
                BuildPushReply(
                    sentMessage.JoinRef!,
                    sentMessage.Ref!,
                    channel.Topic,
                    response: response
                )
            );
        }

        internal static int CountActiveRegistrations(
            CancellationTokenSource cancellationSource
        )
        {
            var registrationsField = typeof(CancellationTokenSource).GetField(
                "_registrations",
                InstanceFields
            );
            Assert.That(
                registrationsField,
                Is.Not.Null,
                "The .NET CancellationTokenSource registration layout changed."
            );

            var registrations = registrationsField!.GetValue(
                cancellationSource
            );
            if (registrations == null)
            {
                return 0;
            }

            var callbacksField = registrations.GetType().GetField(
                "Callbacks",
                InstanceFields
            );
            Assert.That(
                callbacksField,
                Is.Not.Null,
                "The .NET cancellation callback-list layout changed."
            );

            var callback = callbacksField!.GetValue(registrations);
            if (callback == null)
            {
                return 0;
            }

            var nextField = callback.GetType().GetField(
                "Next",
                InstanceFields
            );
            Assert.That(
                nextField,
                Is.Not.Null,
                "The .NET cancellation callback-node layout changed."
            );

            var count = 0;
            while (callback != null)
            {
                count++;
                callback = nextField!.GetValue(callback);
            }

            return count;
        }
    }
}
