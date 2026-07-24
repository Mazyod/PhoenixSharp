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
    /// <summary>
    /// Simple logger implementation for test output.
    /// Writes all log messages to the console with level, source, and message.
    /// </summary>
    public sealed class BasicLogger : ILogger
    {
        public bool IsEnabled(LogLevel level, string source)
        {
            return true;
        }

        public void Log(
            LogLevel level,
            string source,
            string message,
            Exception? exception
        )
        {
            Console.WriteLine(
                "[{0}]: {1} - {2}{3}",
                level,
                source,
                message,
                exception == null ? string.Empty : $"{Environment.NewLine}{exception}"
            );
        }
    }

    /// <summary>
    /// Integration tests for the PhoenixSharp library.
    ///
    /// These tests verify the complete client functionality against a real Phoenix server,
    /// including socket connections, channel operations, presence tracking, and the async API.
    ///
    /// <para><b>Test Server:</b></para>
    /// Tests connect to a Phoenix server at <c>phoenix-sharp.level3.io</c>.
    /// The server source code is available at: https://github.com/Mazyod/phoenix-integration-tester
    ///
    /// <para><b>Prerequisites:</b></para>
    /// <list type="bullet">
    ///   <item>The test server must be running and accessible</item>
    ///   <item>Network connectivity to the test host is required</item>
    ///   <item>Tests are marked with the "Integration" category and can be filtered with: dotnet test --filter Category=Integration</item>
    /// </list>
    ///
    /// <para><b>Test Structure:</b></para>
    /// Each test follows a common pattern: SetUp (create socket) -> Test operations -> TearDown (disconnect).
    /// Tests use polling assertions (Is.True.After) to handle async operations with network delays.
    /// </summary>
    [TestFixture, Category("Integration")]
    public class IntegrationTests
    {
        /// <summary>
        /// Verifies the test server is available before each test.
        /// Skips the test if the server is not reachable.
        /// </summary>
        [SetUp]
        public void Init()
        {
            var address = $"https://{Host}/api/health-check";

            // Verify server is available before running tests
            using HttpClient client = new();
            var result = client.GetAsync(address).GetAwaiter().GetResult();
            Assert.AreEqual(HttpStatusCode.OK, result.StatusCode);
        }

        /// <summary>
        /// Maximum time to wait for async network operations (in milliseconds).
        /// This accounts for network latency, server processing, and reconnection delays.
        /// </summary>
        private const int NetworkDelay = 5_000 /* ms */;

        /// <summary>
        /// The Phoenix server host used for integration testing.
        /// </summary>
        private const string Host = "phoenix-sharp.level3.io";

        /// <summary>
        /// Default channel parameters used for authentication.
        /// The test server requires an "auth" parameter to successfully join protected channels.
        /// </summary>
        private readonly Dictionary<string, object> _channelParams = new()
        {
            {"auth", "doesn't matter"}
        };

        /// <summary>
        /// Returns all available WebSocket factory implementations for parameterized tests.
        /// Each integration test runs once per factory, verifying transport-agnostic behavior.
        ///
        /// Note: WebSocketSharp is excluded — the NuGet package (1.0.3-rc11) does not support
        /// modern TLS versions and fails with "bad protocol version" on wss:// connections.
        /// </summary>
        private static IEnumerable<IWebsocketFactory> AvailableFactories()
        {
            yield return new DotNetWebSocketFactory();
            yield return new NativeWebSocketFactory();
        }

        /// <summary>
        /// Comprehensive test covering the core functionality of the PhoenixSharp client.
        ///
        /// This test verifies:
        /// <list type="bullet">
        ///   <item>Socket connection and lifecycle callbacks (OnOpen, OnClose)</item>
        ///   <item>Automatic socket reconnection after connection loss</item>
        ///   <item>Channel join error handling (missing auth params)</item>
        ///   <item>Successful channel join with custom events</item>
        ///   <item>Push operations with OK, Error, and Timeout replies</item>
        ///   <item>Channel auto-rejoin after socket disconnect/reconnect</item>
        ///   <item>Channel replacement (joining same topic with new channel instance)</item>
        ///   <item>Channel leave operation</item>
        /// </list>
        ///
        /// Expected behavior: All operations complete successfully with appropriate callbacks
        /// being invoked and state transitions occurring as expected.
        /// </summary>
        [TestCaseSource(nameof(AvailableFactories))]
        public void GeneralIntegrationTest(IWebsocketFactory socketFactory)
        {
            // ===== SECTION: Socket Setup and Connection =====

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

            var socketAddress = $"wss://{Host}/socket";
            var socket = new Socket(
                socketAddress,
                null,
                socketFactory,
                new Socket.Options(new JsonMessageSerializer())
                {
                    // Fast reconnect for testing purposes
                    ReconnectAfter = _ => TimeSpan.FromMilliseconds(200),
                    Logger = new BasicLogger()
                }
            );

            socket.OnOpen += OnOpenCallback;
            socket.OnClose += OnCloseCallback;

            socket.Connect();
            Assert.AreEqual(WebsocketState.Open, socket.State);
            // OnOpen callback should fire exactly once on successful connection
            Assert.AreEqual(1, onOpenCount);

            // ===== SECTION: Socket Error Recovery =====
            // Tests that the socket automatically reconnects when the connection is lost.
            // This simulates an unexpected connection drop (e.g., network issue).

            socket.Conn.Close();

            // Connection should be closed immediately after explicit close
            Assert.AreEqual(WebsocketState.Closed, socket.State);
            // Socket should automatically reconnect due to ReconnectAfter option
            Assert.That(() => socket.State == WebsocketState.Open, Is.True.After(NetworkDelay, 10));
            // OnClose should have been called once when connection dropped
            Assert.AreEqual(1, onCloseData.Count);
            // Close message is null for unexpected disconnections
            Assert.IsNull(onCloseData[0]);

            // ===== SECTION: Channel Join Error Handling =====
            // Tests that joining a channel without required auth params returns an error.
            // The test server requires an "auth" parameter - without it, join fails.

            Reply? okReply = null;
            Reply? errorReply = null;
            var closeCalled = false;

            var errorChannel = socket.Channel("tester:phoenix-sharp");
            errorChannel.On(Message.InBoundEvent.Close, _ => closeCalled = true);

            errorChannel.Join()
                .Receive(ReplyStatus.Ok, r => okReply = r)
                .Receive(ReplyStatus.Error, r => errorReply = r);

            // Join should fail because we didn't provide auth params
            Assert.That(() => errorReply != null, Is.True.After(NetworkDelay, 10));
            // OK callback should NOT be called on error
            Assert.IsNull(okReply);
            // Channel should transition to Errored state on join failure
            Assert.AreEqual(ChannelState.Errored, errorChannel.State);
            // Must explicitly leave to cleanup and prevent automatic rejoin attempts
            errorChannel.Leave();
            // Close event should fire when channel is left
            Assert.IsTrue(closeCalled);

            // ===== SECTION: Successful Channel Join with Custom Events =====
            // Tests joining a channel with proper auth and receiving custom events.
            // The server broadcasts an "after_join" event when a client successfully joins.

            Reply? joinOkReply = null;
            Reply? joinErrorReply = null;

            Message? afterJoinMessage = null;
            Message? closeMessage = null;
            Message? errorMessage = null;

            var roomChannel = socket.Channel("tester:phoenix-sharp", _channelParams);
            roomChannel.On(Message.InBoundEvent.Close, m => closeMessage = m);
            roomChannel.On(Message.InBoundEvent.Error, m => errorMessage = m);
            // Server sends "after_join" event with a welcome message after successful join
            roomChannel.On("after_join", m => afterJoinMessage = m);

            roomChannel.Join()
                .Receive(ReplyStatus.Ok, r => joinOkReply = r)
                .Receive(ReplyStatus.Error, r => joinErrorReply = r);

            // Join should succeed with proper auth params
            Assert.That(() => joinOkReply != null, Is.True.After(NetworkDelay, 10));
            Assert.IsNull(joinErrorReply);

            // Custom "after_join" event should be received from server
            Assert.That(() => afterJoinMessage != null, Is.True.After(NetworkDelay, 10));

            // Verify the payload contains the expected welcome message
            var payload = afterJoinMessage?.Payload.Unbox<JObject>();
            Assert.AreEqual("Welcome!", payload["message"].ToObject<string>());

            // ===== SECTION: Push with OK Reply (Echo Test) =====
            // Tests sending a message and receiving an OK reply with echoed data.
            // The "reply_test" event on the server echoes back whatever we send.

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
            // Server should echo back the exact same params we sent
            CollectionAssert.AreEquivalent(
                @params,
                testOkReply?.Response.Unbox<Dictionary<string, object>>()
            );

            // ===== SECTION: Push with Error Reply =====
            // Tests that the error callback is invoked when server returns an error status.
            // The "error_test" event on the server always returns an error reply.

            Reply? testErrorReply = null;

            roomChannel
                .Push("error_test")
                .Receive(ReplyStatus.Error, r => testErrorReply = r);

            Assert.That(() => testErrorReply != null, Is.True.After(NetworkDelay, 10));
            Assert.AreEqual(ReplyStatus.Error, testErrorReply?.ReplyStatus);

            // ===== SECTION: Push with Timeout =====
            // Tests that the timeout callback is invoked when server doesn't respond in time.
            // The "timeout_test" event on the server intentionally delays its response.

            Reply? testTimeoutReply = null;

            roomChannel
                // Very short timeout (50ms) to ensure the server can't respond in time
                .Push("timeout_test", null, TimeSpan.FromMilliseconds(50))
                .Receive(ReplyStatus.Timeout, r => testTimeoutReply = r);

            // Timeout callback should fire shortly after the specified 50ms
            Assert.That(() => testTimeoutReply != null, Is.True.After(500, 10));

            // ===== SECTION: Channel Auto-Rejoin on Socket Reconnect =====
            // Tests that channels automatically rejoin when the socket disconnects and reconnects.
            // This is critical for maintaining channel subscriptions across network interruptions.

            // No error should have occurred yet
            Assert.IsNull(errorMessage);
            // Reset to track the rejoin - same Push callback will fire again on rejoin
            joinOkReply = null;

            // Simulate a full disconnect/reconnect cycle
            socket.Disconnect();
            socket.Connect();

            // Channel should receive an error event due to socket disconnect
            Assert.That(() => errorMessage != null, Is.True.After(NetworkDelay, 10));
            // Channel should automatically rejoin and trigger the original join callback
            Assert.That(() => joinOkReply != null, Is.True.After(NetworkDelay, 10));
            // Channel should be fully operational (CanPush returns true when joined)
            Assert.That(() => roomChannel.CanPush(), Is.True.After(NetworkDelay, 10));

            // ===== SECTION: Channel Replacement =====
            // Tests joining the same topic with a new channel instance.
            // The old channel should be closed and the new one should take over.

            joinOkReply = null;
            joinErrorReply = null;
            errorMessage = null;
            // closeMessage should still be null from before - original channel hasn't been closed
            Assert.IsNull(closeMessage);
            Message? newCloseMessage = null;

            // Create a new channel instance for the same topic
            var newRoomChannel = socket.Channel("tester:phoenix-sharp", _channelParams);
            newRoomChannel.On(Message.InBoundEvent.Close, m => newCloseMessage = m);

            newRoomChannel.Join()
                .Receive(ReplyStatus.Ok, r => joinOkReply = r)
                .Receive(ReplyStatus.Error, r => joinErrorReply = r);

            // New channel join should succeed
            Assert.That(() => joinOkReply != null, Is.True.After(NetworkDelay, 10));
            Assert.IsNull(joinErrorReply);
            // Original channel should have received a Close event (replaced by new channel)
            Assert.IsNotNull(closeMessage);

            // ===== SECTION: Channel Leave =====
            // Tests explicitly leaving a channel.
            // The Close event should be triggered when leave is acknowledged.

            // New channel's close message should be null (hasn't left yet)
            Assert.IsNull(newCloseMessage);
            newRoomChannel.Leave();

            // Close event should fire after leave is processed
            Assert.That(() => newCloseMessage != null, Is.True.After(NetworkDelay, 10));

            // ===== SECTION: TearDown =====

            socket.Disconnect();
        }

        /// <summary>
        /// Tests the socket connection lifecycle with multiple disconnect/reconnect cycles.
        ///
        /// This test verifies:
        /// <list type="bullet">
        ///   <item>OnOpen callback tracking during connection</item>
        ///   <item>OnClose callback properly balances OnOpen count</item>
        ///   <item>Channel join succeeds and receives server events</item>
        ///   <item>Socket disconnect properly cleans up the connection</item>
        ///   <item>Socket reconnect restores the connection with balanced callbacks</item>
        /// </list>
        ///
        /// Expected behavior: After a disconnect/reconnect cycle, the onOpenCount should
        /// remain balanced (OnClose decrements, OnOpen increments), demonstrating proper
        /// callback lifecycle management.
        /// </summary>
        [TestCaseSource(nameof(AvailableFactories))]
        public void MultipleJoinIntegrationTest(IWebsocketFactory socketFactory)
        {
            // ===== SECTION: Socket Setup with Open/Close Tracking =====
            // Uses onOpenCount to track connection lifecycle - incremented on open, decremented on close

            var onOpenCount = 0;

            void OnOpenCallback()
            {
                onOpenCount++;
            }

            void OnClosedCallback(ushort code, string reason)
            {
                // Decrement to balance the increment from OnOpen
                onOpenCount--;
            }

            var socketAddress = $"wss://{Host}/socket";
            var socket = new Socket(
                socketAddress,
                null,
                socketFactory,
                new Socket.Options(new JsonMessageSerializer())
                {
                    // Fast rejoin for testing purposes
                    RejoinAfter = _ => TimeSpan.FromMilliseconds(200),
                    Logger = new BasicLogger()
                }
            );

            socket.OnOpen += OnOpenCallback;
            socket.OnClose += OnClosedCallback;

            // ===== SECTION: Initial Connection =====

            socket.Connect();
            Assert.AreEqual(WebsocketState.Open, socket.State);
            // First connection should increment count to 1
            Assert.AreEqual(1, onOpenCount);

            // ===== SECTION: Channel Join =====

            Reply? joinOkReply = null;
            Reply? joinErrorReply = null;
            Message? afterJoinMessage = null;

            var roomChannel = socket.Channel("tester:phoenix-sharp", _channelParams);
            roomChannel.On("after_join", m => afterJoinMessage = m);

            roomChannel.Join()
                .Receive(ReplyStatus.Ok, r => joinOkReply = r)
                .Receive(ReplyStatus.Error, r => joinErrorReply = r);

            Assert.That(() => joinOkReply != null, Is.True.After(NetworkDelay, 10));
            Assert.IsNull(joinErrorReply);

            // Server sends "after_join" event after successful join
            Assert.That(() => afterJoinMessage != null, Is.True.After(NetworkDelay, 10));

            var payload = afterJoinMessage?.Payload.Unbox<JObject>();
            Assert.IsNotNull(payload);
            Assert.AreEqual("Welcome!", payload["message"]?.ToObject<string>());

            // Verify channel is in the expected Joined state
            Assert.AreEqual(ChannelState.Joined, roomChannel.State);

            // ===== SECTION: Disconnect and Reconnect Cycle =====
            // Tests that disconnect/reconnect properly triggers callbacks and maintains state

            // Save reference to check it gets cleaned up
            var conn = socket.Conn;
            socket.Disconnect();

            // Socket.Conn should be null after disconnect
            Assert.That(() => socket.Conn == null, Is.True.After(NetworkDelay, 10));
            // The original connection should be in Closed state
            Assert.That(() => conn.State == WebsocketState.Closed, Is.True.After(NetworkDelay, 10));

            socket.Connect();
            Assert.AreEqual(WebsocketState.Open, socket.State);
            // OnClose decremented to 0, OnOpen incremented back to 1
            // This verifies both callbacks fired correctly during the cycle
            Assert.AreEqual(1, onOpenCount);

            // ===== SECTION: TearDown =====

            socket.Disconnect();
        }

        /// <summary>
        /// Tests the Presence tracking functionality using the callback-based API.
        ///
        /// This test verifies:
        /// <list type="bullet">
        ///   <item>Presence instance can be created and attached to a channel</item>
        ///   <item>OnJoin callback fires when users join the channel</item>
        ///   <item>Presence state contains the user key (based on auth param)</item>
        ///   <item>Presence metadata includes PhxRef and online_at timestamp</item>
        ///   <item>Custom presence payload data is accessible</item>
        /// </list>
        ///
        /// Expected behavior: When a client joins a channel with presence tracking,
        /// the OnJoin callback should fire with the user's presence data, including
        /// metadata (metas) with a PhxRef and custom payload data from the server.
        /// </summary>
        [TestCaseSource(nameof(AvailableFactories))]
        public void PresenceTrackingTest(IWebsocketFactory socketFactory)
        {
            // ===== SECTION: Socket Setup =====

            var onOpenCount = 0;

            void OnOpenCallback()
            {
                onOpenCount++;
            }

            var socketAddress = $"wss://{Host}/socket";
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

            // ===== SECTION: Presence Setup and Channel Join =====
            // Presence must be created before joining to capture the initial state sync

            var channel = socket.Channel("tester:phoenix-sharp", _channelParams);
            var presence = new Presence(channel);

            // Track all OnJoin calls to verify presence updates
            var joinCalls = new List<(string, PresencePayload, PresencePayload)>();
            presence.OnJoin += (user, prevState, nextState)
                => joinCalls.Add((user, prevState, nextState));

            Reply? joinOkReply = null;
            channel.Join()
                .Receive(ReplyStatus.Ok, r => joinOkReply = r);

            // ===== SECTION: Verify Channel Join =====

            // Channel join should succeed first
            Assert.That(() => joinOkReply != null, Is.True.After(NetworkDelay, 10));

            // ===== SECTION: Verify Presence State =====
            // After joining, we receive presence_state with all current users,
            // followed by presence_diff for our own join

            // Expect 2 OnJoin calls: one from state sync, one from diff
            Assert.That(() => joinCalls.Count == 2, Is.True.After(NetworkDelay, 10));

            // The user key is the "auth" value we provided in channel params
            var (userId, currentState, newState) = joinCalls[0];
            Assert.AreEqual(userId, _channelParams["auth"] as string);

            // Previous state is null for first join (user wasn't present before)
            Assert.IsNull(currentState);

            // ===== SECTION: Verify Presence Metadata =====
            // Each presence has a list of "metas" - one for each device/session

            Assert.IsNotNull(newState);
            // Should have exactly one meta entry for our single connection
            Assert.AreEqual(1, newState.Metas.Count,
                $"newState.metas: {JsonConvert.SerializeObject(newState)}");

            var newStateMeta = newState.Metas[0];
            // PhxRef is a unique reference assigned by Phoenix for this presence
            Assert.IsNotEmpty(newStateMeta.PhxRef);
            // Verify presence payload contains online_at timestamp from server
            var presenceJson = newStateMeta.Payload.Unbox<JToken>();
            Assert.IsNotEmpty(presenceJson.Value<string>("online_at"));

            // ===== SECTION: Verify Custom Presence Payload =====
            // The test server includes custom device info in the presence payload

            Assert.AreEqual(newState.Payload.Unbox<JToken>()["device"]?.Value<string>("make"), "Apple");

            // ===== SECTION: TearDown =====

            socket.Disconnect();
        }

        /// <summary>
        /// Tests the async/await API for socket, channel, and push operations.
        ///
        /// This test verifies the Task-based async alternatives to the callback-based API:
        /// <list type="bullet">
        ///   <item>Socket.ConnectAsync / DisconnectAsync</item>
        ///   <item>Channel.JoinAsync / LeaveAsync with success and error cases</item>
        ///   <item>Channel.PushAsync with typed and untyped responses</item>
        ///   <item>Channel.WaitForEventAsync for awaiting custom events</item>
        ///   <item>Push.ReceiveAsync for awaiting reply on existing push</item>
        ///   <item>Timeout and cancellation token support for all async methods</item>
        /// </list>
        ///
        /// Expected behavior: All async methods should behave equivalently to their
        /// callback-based counterparts, with proper Task completion, timeout handling,
        /// and cancellation token support.
        /// </summary>
        [TestCaseSource(nameof(AvailableFactories))]
        public async Task AsyncApiIntegrationTest(IWebsocketFactory socketFactory)
        {
            // ===== SECTION: Socket Setup and ConnectAsync =====

            var socketAddress = $"wss://{Host}/socket";
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

            // ConnectAsync returns when the connection is established
            await socket.ConnectAsync();
            Assert.AreEqual(WebsocketState.Open, socket.State);

            // ===== SECTION: JoinAsync Error Handling =====
            // Tests that JoinAsync properly reports errors via the result object

            var errorChannel = socket.Channel("tester:phoenix-sharp");
            var errorJoinResult = await errorChannel.JoinAsync(TimeSpan.FromSeconds(5));
            // IsSuccess should be false when join fails
            Assert.IsFalse(errorJoinResult.IsSuccess);
            // Error field contains the server's error response
            Assert.AreEqual("error", errorJoinResult.Error);
            // Must leave to cleanup even on error
            errorChannel.Leave();

            // ===== SECTION: JoinAsync Success =====

            var roomChannel = socket.Channel("tester:phoenix-sharp", _channelParams);

            // IMPORTANT: Start waiting for event BEFORE joining to avoid race condition
            // The server sends "after_join" immediately after join succeeds
            var afterJoinTask = roomChannel.WaitForEventAsync("after_join", TimeSpan.FromSeconds(5));

            var joinResult = await roomChannel.JoinAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(joinResult.IsSuccess);
            Assert.IsNotNull(joinResult.Reply);

            // Await the event that was fired during/after join
            var afterJoinMessage = await afterJoinTask;
            Assert.IsNotNull(afterJoinMessage);
            var payload = afterJoinMessage.Payload.Unbox<JObject>();
            Assert.AreEqual("Welcome!", payload["message"].ToObject<string>());

            // ===== SECTION: PushAsync with Typed Response =====
            // Generic PushAsync<T> automatically deserializes the response to type T

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
            // Response is already deserialized to Dictionary<string, object>
            Assert.AreEqual("async_test", pushResult.Response["echo"]?.ToString());

            // ===== SECTION: PushAsync without Typed Response =====
            // Non-generic PushAsync returns raw Reply for manual deserialization

            var untypedPushResult = await roomChannel.PushAsync(
                "reply_test",
                echoParams,
                TimeSpan.FromSeconds(5)
            );
            Assert.IsTrue(untypedPushResult.IsSuccess);
            // Access raw Reply for manual handling
            Assert.IsNotNull(untypedPushResult.Reply);

            // ===== SECTION: PushAsync Error Handling =====
            // Tests that server errors are properly reflected in the result

            var errorPushResult = await roomChannel.PushAsync("error_test", null, TimeSpan.FromSeconds(5));
            Assert.IsFalse(errorPushResult.IsSuccess);
            Assert.AreEqual(ReplyStatus.Error, errorPushResult.Status);

            // ===== SECTION: PushAsync Timeout =====
            // Tests that timeout is properly detected and reported

            var timeoutPushResult = await roomChannel.PushAsync(
                "timeout_test",
                null,
                // Very short timeout to ensure it expires before server responds
                TimeSpan.FromMilliseconds(50)
            );
            Assert.IsFalse(timeoutPushResult.IsSuccess);
            Assert.AreEqual(ReplyStatus.Timeout, timeoutPushResult.Status);

            // ===== SECTION: Push.ReceiveAsync =====
            // Alternative: create Push first, then await the reply

            var push = roomChannel.Push("reply_test", echoParams, TimeSpan.FromSeconds(5));
            var reply = await push.ReceiveAsync();
            Assert.AreEqual(ReplyStatus.Ok, reply.ReplyStatus);
            Assert.IsNotNull(reply.Response);

            // ===== SECTION: WaitForEventAsync Timeout =====
            // Tests that waiting for a non-existent event times out properly

            try
            {
                await roomChannel.WaitForEventAsync("nonexistent_event", TimeSpan.FromMilliseconds(100));
                Assert.Fail("Expected TimeoutException");
            }
            catch (TimeoutException)
            {
                // Expected - event was never received within timeout
            }

            // ===== SECTION: WaitForEventAsync Cancellation =====
            // Tests that cancellation token properly cancels the wait

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            try
            {
                // Passing null timeout with a cancellation token
                await roomChannel.WaitForEventAsync("nonexistent_event", null, cts.Token);
                Assert.Fail("Expected OperationCanceledException");
            }
            catch (OperationCanceledException)
            {
                // Expected - cancellation token was triggered
            }

            // ===== SECTION: LeaveAsync =====

            await roomChannel.LeaveAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(ChannelState.Closed, roomChannel.State);

            // ===== SECTION: DisconnectAsync =====

            await socket.DisconnectAsync();
            // Socket.Conn should be null after disconnect
            Assert.IsNull(socket.Conn);

            // ===== SECTION: ConnectAsync Cancellation =====
            // Tests that a pre-canceled token immediately throws

            var socket2 = new Socket(
                socketAddress,
                null,
                socketFactory,
                new Socket.Options(new JsonMessageSerializer())
            );
            using var connectCts = new CancellationTokenSource();
            // Cancel before even attempting to connect
            connectCts.Cancel();
            try
            {
                await socket2.ConnectAsync(connectCts.Token);
                Assert.Fail("Expected OperationCanceledException");
            }
            catch (OperationCanceledException)
            {
                // Expected - pre-canceled token should throw immediately
            }
        }

        /// <summary>
        /// Tests the async/await API for Presence tracking.
        ///
        /// This test verifies the Task-based async alternatives for presence operations:
        /// <list type="bullet">
        ///   <item>Presence.WaitForInitialSyncAsync for awaiting initial state sync</item>
        ///   <item>Presence.WaitForUserAsync for awaiting a specific user's presence</item>
        ///   <item>Timeout behavior when user is not found</item>
        ///   <item>Cancellation token support for presence async methods</item>
        /// </list>
        ///
        /// Expected behavior: The async presence API should allow awaiting presence state
        /// synchronization and specific user joins, with proper timeout and cancellation support.
        /// </summary>
        [TestCaseSource(nameof(AvailableFactories))]
        public async Task PresenceAsyncApiIntegrationTest(IWebsocketFactory socketFactory)
        {
            // ===== SECTION: Socket Setup =====

            var socketAddress = $"wss://{Host}/socket";
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

            // ===== SECTION: Presence Setup =====

            var channel = socket.Channel("tester:phoenix-sharp", _channelParams);
            var presence = new Presence(channel);

            // IMPORTANT: Start waiting for sync BEFORE joining to avoid race condition
            // The server sends presence_state immediately after join succeeds
            var syncTask = presence.WaitForInitialSyncAsync();

            // ===== SECTION: Channel Join =====

            var joinResult = await channel.JoinAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(joinResult.IsSuccess);

            // ===== SECTION: Wait for Initial Presence Sync =====
            // The initial sync completes when presence_state is received from server

            await syncTask;

            // Presence state should be populated after sync completes
            Assert.IsTrue(presence.State.Count > 0, "Presence state should be populated after sync");

            // ===== SECTION: WaitForUserAsync - User Already Present =====
            // When the user is already in the presence state, WaitForUserAsync returns immediately

            var userKey = _channelParams["auth"] as string;
            var userPresence = await presence.WaitForUserAsync(userKey!, TimeSpan.FromSeconds(1));
            Assert.IsNotNull(userPresence, "User presence should be found");
            // Should have one meta entry for our single connection
            Assert.AreEqual(1, userPresence!.Metas.Count);

            // ===== SECTION: WaitForUserAsync Timeout =====
            // Tests that waiting for a non-existent user returns null on timeout (not throws)

            var nonExistentUser = await presence.WaitForUserAsync("non_existent_user_12345", TimeSpan.FromMilliseconds(100));
            // Unlike WaitForEventAsync, WaitForUserAsync returns null on timeout instead of throwing
            Assert.IsNull(nonExistentUser, "Non-existent user should return null on timeout");

            // ===== SECTION: WaitForUserAsync Cancellation =====
            // Tests that cancellation token properly cancels the wait

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            try
            {
                // Timeout is long (10s) but cancellation token triggers at 50ms
                await presence.WaitForUserAsync("another_non_existent", TimeSpan.FromSeconds(10), cts.Token);
                Assert.Fail("Expected OperationCanceledException");
            }
            catch (OperationCanceledException)
            {
                // Expected - cancellation token was triggered before timeout
            }

            // ===== SECTION: TearDown =====

            await channel.LeaveAsync();
            await socket.DisconnectAsync();
        }
    }
}
