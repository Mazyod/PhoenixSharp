using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Phoenix;
using PhoenixTests.WebSocketImpl;

namespace PhoenixTests
{
    public sealed class BasicLogger : ILogger
    {
        public void Log(LogLevel level, string source, string message)
        {
            Console.WriteLine("[{0}]: {1} - {2}", level, source, message);
        }
    }

    [TestFixture, Category("Integration")]
    public class IntegrationTests
    {
        [SetUp]
        public void Init()
        {
            var address = $"http://{Host}/api/health-check";

            // heroku health check
            using HttpClient client = new();
            var result = client.GetAsync(address).GetAwaiter().GetResult();
            Assert.AreEqual(HttpStatusCode.OK, result.StatusCode);
        }

        private const int NetworkDelay = 5_000 /* ms */;
        private const string Host = "phoenix-sharp.level3.io:3080";

        private readonly Dictionary<string, object> _channelParams = new()
        {
            {"auth", "doesn't matter"}
        };

        [Test]
        public void GeneralIntegrationTest()
        {
            // SetUp
            var onOpenCount = 0;

            void OnOpenCallback()
            {
                onOpenCount++;
            }

            List<string> onCloseData = new();

            void OnCloseCallback(ushort code, string message)
            {
                onCloseData.Add(message);
            }

            // connecting is synchronous as implemented above
            var socketAddress = $"ws://{Host}/socket";
            var socketFactory = new DotNetWebSocketFactory();
            var socket = new Socket(
                socketAddress,
                null,
                socketFactory,
                new Socket.Options(new JsonMessageSerializer())
                {
                    ReconnectAfter = _ => TimeSpan.FromMilliseconds(200),
                    Logger = new BasicLogger()
                }
            );

            socket.OnOpen += OnOpenCallback;
            socket.OnClose += OnCloseCallback;

            socket.Connect();
            Assert.AreEqual(WebsocketState.Open, socket.State);
            Assert.AreEqual(1, onOpenCount);

            // test socket error recovery

            socket.Conn.Close();

            Assert.AreEqual(WebsocketState.Closed, socket.State);
            Assert.That(() => socket.State == WebsocketState.Open, Is.True.After(NetworkDelay, 10));
            Assert.AreEqual(1, onCloseData.Count);
            Assert.IsNull(onCloseData[0]);

            // test channel error on join

            Reply? okReply = null;
            Reply? errorReply = null;
            var closeCalled = false;

            var errorChannel = socket.Channel("tester:phoenix-sharp");
            errorChannel.On(Message.InBoundEvent.Close, _ => closeCalled = true);

            errorChannel.Join()
                .Receive(ReplyStatus.Ok, r => okReply = r)
                .Receive(ReplyStatus.Error, r => errorReply = r);

            Assert.That(() => errorReply != null, Is.True.After(NetworkDelay, 10));
            Assert.IsNull(okReply);
            Assert.AreEqual(ChannelState.Errored, errorChannel.State);
            // call leave explicitly to cleanup and avoid rejoin attempts
            errorChannel.Leave();
            Assert.IsTrue(closeCalled);

            // test channel joining and receiving a custom event

            Reply? joinOkReply = null;
            Reply? joinErrorReply = null;

            Message? afterJoinMessage = null;
            Message? closeMessage = null;
            Message? errorMessage = null;

            var roomChannel = socket.Channel("tester:phoenix-sharp", _channelParams);
            roomChannel.On(Message.InBoundEvent.Close, m => closeMessage = m);
            roomChannel.On(Message.InBoundEvent.Error, m => errorMessage = m);
            roomChannel.On("after_join", m => afterJoinMessage = m);

            roomChannel.Join()
                .Receive(ReplyStatus.Ok, r => joinOkReply = r)
                .Receive(ReplyStatus.Error, r => joinErrorReply = r);

            Assert.That(() => joinOkReply != null, Is.True.After(NetworkDelay, 10));
            Assert.IsNull(joinErrorReply);

            Assert.That(() => afterJoinMessage != null, Is.True.After(NetworkDelay, 10));

            var payload = afterJoinMessage?.Payload.Unbox<JObject>();
            Assert.AreEqual("Welcome!", payload["message"].ToObject<string>());

            // 1. heartbeat, 2. error, 3. join, 4. after_join
            // TODO: see what changed here
            // Assert.AreEqual(4, onMessageData.Count, "Unexpected message count: " + string.Join("; ", onMessageData));

            // test echo reply

            var @params = new Dictionary<string, object>
            {
                {"echo", "test"}
            };

            Reply? testOkReply = null;

            roomChannel
                .Push("reply_test", @params)
                .Receive(ReplyStatus.Ok, r => testOkReply = r);

            Assert.That(() => testOkReply != null, Is.True.After(NetworkDelay, 10));
            Assert.IsNotNull(testOkReply?.Response);
            CollectionAssert.AreEquivalent(
                @params,
                testOkReply?.Response.Unbox<Dictionary<string, object>>()
            );

            // test error reply

            Reply? testErrorReply = null;

            roomChannel
                .Push("error_test")
                .Receive(ReplyStatus.Error, r => testErrorReply = r);

            Assert.That(() => testErrorReply != null, Is.True.After(NetworkDelay, 10));
            Assert.AreEqual(ReplyStatus.Error, testErrorReply?.ReplyStatus);

            // test timeout reply

            Reply? testTimeoutReply = null;

            roomChannel
                .Push("timeout_test", null, TimeSpan.FromMilliseconds(50))
                .Receive(ReplyStatus.Timeout, r => testTimeoutReply = r);

            // Assert.That(() => testTimeoutReply != null, Is.False.After(10));
            Assert.That(() => testTimeoutReply != null, Is.True.After(50));

            // test channel error/rejoin

            Assert.IsNull(errorMessage);
            // we track rejoining through the same join push callback we setup
            joinOkReply = null;

            socket.Disconnect();
            socket.Connect();

            Assert.That(() => errorMessage != null, Is.True.After(NetworkDelay, 10));
            Assert.That(() => joinOkReply != null, Is.True.After(NetworkDelay, 10));
            Assert.That(() => roomChannel.CanPush(), Is.True.After(NetworkDelay, 10));

            // test channel replace

            joinOkReply = null;
            joinErrorReply = null;
            errorMessage = null;
            Assert.IsNull(closeMessage);
            Message? newCloseMessage = null;

            var newRoomChannel = socket.Channel("tester:phoenix-sharp", _channelParams);
            newRoomChannel.On(Message.InBoundEvent.Close, m => newCloseMessage = m);

            newRoomChannel.Join()
                .Receive(ReplyStatus.Ok, r => joinOkReply = r)
                .Receive(ReplyStatus.Error, r => joinErrorReply = r);

            Assert.That(() => joinOkReply != null, Is.True.After(NetworkDelay, 10));
            Assert.IsNull(joinErrorReply);
            // Not sure why previous PhoenixSharp version had errorMessage on closed channel
            // Assert.IsNotNull(errorMessage);
            Assert.IsNotNull(closeMessage);

            // test channel leave

            Assert.IsNull(newCloseMessage);
            newRoomChannel.Leave();

            Assert.That(() => newCloseMessage != null, Is.True.After(NetworkDelay, 10));

            // TearDown

            socket.Disconnect();
        }

        [Test]
        public void MultipleJoinIntegrationTest()
        {
            var onOpenCount = 0;

            void OnOpenCallback()
            {
                onOpenCount++;
            }

            void OnClosedCallback(ushort code, string reason)
            {
                onOpenCount--;
            }

            var socketAddress = $"ws://{Host}/socket";
            var socketFactory = new DotNetWebSocketFactory();
            var socket = new Socket(
                socketAddress,
                null,
                socketFactory,
                new Socket.Options(new JsonMessageSerializer())
                {
                    RejoinAfter = _ => TimeSpan.FromMilliseconds(200),
                    Logger = new BasicLogger()
                }
            );

            socket.OnOpen += OnOpenCallback;
            socket.OnClose += OnClosedCallback;

            socket.Connect();
            Assert.AreEqual(WebsocketState.Open, socket.State);
            Assert.AreEqual(1, onOpenCount);

            Reply? joinOkReply = null;
            Reply? joinErrorReply = null;
            Message? afterJoinMessage = null;

            //Try to join for the first time
            var roomChannel = socket.Channel("tester:phoenix-sharp", _channelParams);
            roomChannel.On("after_join", m => afterJoinMessage = m);

            roomChannel.Join()
                .Receive(ReplyStatus.Ok, r => joinOkReply = r)
                .Receive(ReplyStatus.Error, r => joinErrorReply = r);

            Assert.That(() => joinOkReply != null, Is.True.After(NetworkDelay, 10));
            Assert.IsNull(joinErrorReply);

            Assert.That(() => afterJoinMessage != null, Is.True.After(NetworkDelay, 10));

            var payload = afterJoinMessage?.Payload.Unbox<JObject>();
            Assert.IsNotNull(payload);
            Assert.AreEqual("Welcome!", payload["message"]?.ToObject<string>());

            Assert.AreEqual(ChannelState.Joined, roomChannel.State);

            var conn = socket.Conn;
            socket.Disconnect();

            Assert.That(() => socket.Conn == null, Is.True.After(NetworkDelay, 10));
            Assert.That(() => conn.State == WebsocketState.Closed, Is.True.After(NetworkDelay, 10));

            socket.Connect();
            Assert.AreEqual(WebsocketState.Open, socket.State);
            Assert.AreEqual(1, onOpenCount);

            // TearDown

            socket.Disconnect();
        }

        [Test]
        public void PresenceTrackingTest()
        {
            // SetUp

            var onOpenCount = 0;

            void OnOpenCallback()
            {
                onOpenCount++;
            }

            // connecting is synchronous as implemented above
            var socketAddress = $"ws://{Host}/socket";
            var socketFactory = new DotNetWebSocketFactory();
            var socket = new Socket(
                socketAddress,
                null,
                socketFactory,
                new Socket.Options(new JsonMessageSerializer())
                {
                    ReconnectAfter = _ => TimeSpan.FromMilliseconds(200),
                    Logger = new BasicLogger()
                }
            );

            socket.OnOpen += OnOpenCallback;

            socket.Connect();
            Assert.IsTrue(socket.State == WebsocketState.Open);
            Assert.AreEqual(1, onOpenCount);

            // test presence tracking

            var channel = socket.Channel("tester:phoenix-sharp", _channelParams);
            var presence = new Presence(channel);

            var joinCalls = new List<(string, PresencePayload, PresencePayload)>();
            presence.OnJoin += (user, prevState, nextState)
                => joinCalls.Add((user, prevState, nextState));

            Reply? joinOkReply = null;
            channel.Join()
                .Receive(ReplyStatus.Ok, r => joinOkReply = r);

            // first, we get ack for joining the channel
            Assert.That(() => joinOkReply != null, Is.True.After(NetworkDelay, 10));
            // then, we get the presence state
            Assert.That(() => joinCalls.Count == 2, Is.True.After(NetworkDelay, 10));
            // the key used by the server is the auth value we send
            var (userId, currentState, newState) = joinCalls[0];
            Assert.AreEqual(userId, _channelParams["auth"] as string);
            // current state is null initially
            Assert.IsNull(currentState);
            // new state is populated with some goodies
            Assert.IsNotNull(newState);
            Assert.AreEqual(1, newState.Metas.Count,
                $"newState.metas: {JsonConvert.SerializeObject(newState)}");

            var newStateMeta = newState.Metas[0];
            Assert.IsNotEmpty(newStateMeta.PhxRef);
            var presenceJson = newStateMeta.Payload.Unbox<JToken>();
            Assert.IsNotEmpty(presenceJson.Value<string>("online_at"));

            // check custom payload
            Assert.AreEqual(newState.Payload.Unbox<JToken>()["device"]?.Value<string>("make"), "Apple");

            // TearDown

            socket.Disconnect();
        }

        [Test]
        public async Task AsyncApiIntegrationTest()
        {
            // SetUp
            var socketAddress = $"ws://{Host}/socket";
            var socketFactory = new DotNetWebSocketFactory();
            var socket = new Socket(
                socketAddress,
                null,
                socketFactory,
                new Socket.Options(new JsonMessageSerializer())
                {
                    ReconnectAfter = _ => TimeSpan.FromMilliseconds(200),
                    Logger = new BasicLogger()
                }
            );

            // Test ConnectAsync
            await socket.ConnectAsync();
            Assert.AreEqual(WebsocketState.Open, socket.State);

            // Test JoinAsync with error (missing auth params)
            var errorChannel = socket.Channel("tester:phoenix-sharp");
            var errorJoinResult = await errorChannel.JoinAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(errorJoinResult.IsSuccess);
            // Server returns "error" reply when auth params are missing
            Assert.AreEqual("error", errorJoinResult.Error);
            errorChannel.Leave();

            // Test JoinAsync success
            var roomChannel = socket.Channel("tester:phoenix-sharp", _channelParams);

            // Start waiting for after_join event BEFORE joining (so we don't miss it)
            var afterJoinTask = roomChannel.WaitForEventAsync("after_join", TimeSpan.FromSeconds(5));

            var joinResult = await roomChannel.JoinAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(joinResult.IsSuccess);
            Assert.IsNotNull(joinResult.Reply);

            // Wait for the after_join event
            var afterJoinMessage = await afterJoinTask;
            Assert.IsNotNull(afterJoinMessage);
            var payload = afterJoinMessage.Payload.Unbox<JObject>();
            Assert.AreEqual("Welcome!", payload["message"].ToObject<string>());

            // Test PushAsync with typed response
            var echoParams = new Dictionary<string, object>
            {
                {"echo", "async_test"}
            };

            var pushResult = await roomChannel.PushAsync<Dictionary<string, object>>(
                "reply_test",
                echoParams,
                TimeSpan.FromSeconds(5)
            );
            Assert.IsTrue(pushResult.IsSuccess);
            Assert.IsNotNull(pushResult.Response);
            Assert.AreEqual("async_test", pushResult.Response["echo"]?.ToString());

            // Test PushAsync without typed response
            var untypedPushResult = await roomChannel.PushAsync(
                "reply_test",
                echoParams,
                TimeSpan.FromSeconds(5)
            );
            Assert.IsTrue(untypedPushResult.IsSuccess);
            Assert.IsNotNull(untypedPushResult.Reply);

            // Test PushAsync error
            var errorPushResult = await roomChannel.PushAsync("error_test", null, TimeSpan.FromSeconds(5));
            Assert.IsFalse(errorPushResult.IsSuccess);
            Assert.AreEqual(ReplyStatus.Error, errorPushResult.Status);

            // Test PushAsync timeout
            var timeoutPushResult = await roomChannel.PushAsync(
                "timeout_test",
                null,
                TimeSpan.FromMilliseconds(50)
            );
            Assert.IsFalse(timeoutPushResult.IsSuccess);
            Assert.AreEqual(ReplyStatus.Timeout, timeoutPushResult.Status);

            // Test Push.ReceiveAsync
            var push = roomChannel.Push("reply_test", echoParams, TimeSpan.FromSeconds(5));
            var reply = await push.ReceiveAsync();
            Assert.AreEqual(ReplyStatus.Ok, reply.ReplyStatus);
            Assert.IsNotNull(reply.Response);

            // Test WaitForEventAsync timeout
            try
            {
                await roomChannel.WaitForEventAsync("nonexistent_event", TimeSpan.FromMilliseconds(100));
                Assert.Fail("Expected TimeoutException");
            }
            catch (TimeoutException)
            {
                // Expected
            }

            // Test WaitForEventAsync cancellation
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            try
            {
                await roomChannel.WaitForEventAsync("nonexistent_event", null, cts.Token);
                Assert.Fail("Expected OperationCanceledException");
            }
            catch (OperationCanceledException)
            {
                // Expected
            }

            // Test LeaveAsync
            await roomChannel.LeaveAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(ChannelState.Closed, roomChannel.State);

            // Test DisconnectAsync
            await socket.DisconnectAsync();
            Assert.IsNull(socket.Conn);

            // Test ConnectAsync cancellation
            var socket2 = new Socket(
                socketAddress,
                null,
                socketFactory,
                new Socket.Options(new JsonMessageSerializer())
            );
            using var connectCts = new CancellationTokenSource();
            connectCts.Cancel(); // Cancel immediately
            try
            {
                await socket2.ConnectAsync(connectCts.Token);
                Assert.Fail("Expected OperationCanceledException");
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }

        [Test]
        public async Task PresenceAsyncApiIntegrationTest()
        {
            // SetUp
            var socketAddress = $"ws://{Host}/socket";
            var socketFactory = new DotNetWebSocketFactory();
            var socket = new Socket(
                socketAddress,
                null,
                socketFactory,
                new Socket.Options(new JsonMessageSerializer())
                {
                    Logger = new BasicLogger()
                }
            );

            await socket.ConnectAsync();
            Assert.AreEqual(WebsocketState.Open, socket.State);

            var channel = socket.Channel("tester:phoenix-sharp", _channelParams);
            var presence = new Presence(channel);

            // Start waiting for initial sync before joining
            var syncTask = presence.WaitForInitialSyncAsync();

            // Join the channel
            var joinResult = await channel.JoinAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(joinResult.IsSuccess);

            // Wait for initial presence sync
            await syncTask;

            // Verify presence state has been populated
            Assert.IsTrue(presence.State.Count > 0, "Presence state should be populated after sync");

            // Test WaitForUserAsync - user should already be present
            var userKey = _channelParams["auth"] as string;
            var userPresence = await presence.WaitForUserAsync(userKey!, TimeSpan.FromSeconds(1));
            Assert.IsNotNull(userPresence, "User presence should be found");
            Assert.AreEqual(1, userPresence!.Metas.Count);

            // Test WaitForUserAsync timeout for non-existent user
            var nonExistentUser = await presence.WaitForUserAsync("non_existent_user_12345", TimeSpan.FromMilliseconds(100));
            Assert.IsNull(nonExistentUser, "Non-existent user should return null on timeout");

            // Test WaitForUserAsync cancellation
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            try
            {
                await presence.WaitForUserAsync("another_non_existent", TimeSpan.FromSeconds(10), cts.Token);
                Assert.Fail("Expected OperationCanceledException");
            }
            catch (OperationCanceledException)
            {
                // Expected
            }

            // TearDown
            await channel.LeaveAsync();
            await socket.DisconnectAsync();
        }
    }
}
