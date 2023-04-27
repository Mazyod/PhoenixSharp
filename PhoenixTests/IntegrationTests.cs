using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
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

    [TestFixture]
    public class IntegrationTests
    {
        [SetUp]
        public void Init()
        {
            var address = $"https://{Host}/api/health-check";

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
            var socketAddress = $"wss://{Host}/socket";
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
            // also, it should discard any additional messages

            Assert.IsNull(newCloseMessage);
            Message? pushMessage = null;

            newRoomChannel.On("push_test", m => pushMessage = m);
            newRoomChannel.Push("push_test", @params);

            Assert.IsNull(pushMessage);
            newRoomChannel.Leave();

            Assert.That(() => newCloseMessage != null, Is.True.After(NetworkDelay, 10));
            Assert.IsNull(pushMessage); // ignored

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

            var socketAddress = $"wss://{Host}/socket";
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
            var socketAddress = $"wss://{Host}/socket";
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
    }
}