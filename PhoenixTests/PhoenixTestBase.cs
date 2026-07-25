using System;
using System.Collections.Generic;
using System.Reflection;
using Phoenix;
using PhoenixTests.TestDoubles;
using PhoenixTests.WebSocketImpl;

namespace PhoenixTests
{
    /// <summary>
    /// Base class providing shared test helpers for Phoenix tests.
    /// Reduces duplication of common socket and channel creation patterns.
    /// </summary>
    public abstract class PhoenixTestBase
    {
        protected static bool HasSocketEventSubscribers(
            Socket socket,
            string eventName
        )
        {
            var eventField = typeof(Socket).GetField(
                eventName,
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            return eventField?.GetValue(socket) is Delegate;
        }

        /// <summary>
        /// Creates a socket with a mock factory and default options, then connects it.
        /// </summary>
        /// <param name="mockExecutor">Optional mock executor for controlling delayed executions.</param>
        /// <returns>A connected socket using MockWebsocketFactoryWithCallbackTracking.</returns>
        protected static Socket CreateConnectedSocket(MockDelayedExecutor? mockExecutor = null)
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var options = new Socket.Options(new JsonMessageSerializer());
            if (mockExecutor != null)
            {
                options.DelayedExecutor = mockExecutor;
            }

            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                options
            );
            socket.Connect();
            return socket;
        }

        /// <summary>
        /// Creates a socket with a mock factory using a TrackingDelayedExecutor, then connects it.
        /// Returns both the socket and the executor for fine-grained control in tests.
        /// </summary>
        /// <returns>A tuple containing the connected socket and the tracking executor.</returns>
        protected static (Socket socket, TrackingDelayedExecutor executor) CreateConnectedSocketWithTrackingExecutor()
        {
            var executor = new TrackingDelayedExecutor();
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                DelayedExecutor = executor
            };

            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                options
            );
            socket.Connect();
            return (socket, executor);
        }

        /// <summary>
        /// Creates a connected socket and channel that has successfully joined.
        /// Returns the channel, mock websocket, and executor for comprehensive testing.
        /// </summary>
        /// <param name="topic">The channel topic (defaults to "test-topic").</param>
        /// <returns>A tuple containing the joined channel, mock websocket adapter, and mock executor.</returns>
        protected static (Channel channel, MockWebsocketAdapterWithCallbacks websocket, MockDelayedExecutor executor) CreateJoinedChannel(
            string topic = "test-topic")
        {
            var mockExecutor = new MockDelayedExecutor();
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var options = new Socket.Options(new JsonMessageSerializer())
            {
                DelayedExecutor = mockExecutor
            };

            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                options
            );
            socket.Connect();
            var channel = socket.Channel(topic);
            var joinPush = channel.Join();
            joinPush.Trigger(ReplyStatus.Ok);

            return (channel, factory.LastCreatedWebsocket!, mockExecutor);
        }

        /// <summary>
        /// Creates a connected socket with specific options.
        /// </summary>
        /// <param name="options">The socket options to use.</param>
        /// <returns>A tuple containing the connected socket and the mock websocket factory.</returns>
        protected static (Socket socket, MockWebsocketFactoryWithCallbackTracking factory) CreateConnectedSocketWithOptions(
            Socket.Options options)
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                options
            );
            socket.Connect();
            return (socket, factory);
        }

        /// <summary>
        /// Creates a socket without connecting it.
        /// Useful for testing pre-connection behavior.
        /// </summary>
        /// <returns>A tuple containing the socket and the mock websocket factory.</returns>
        protected static (Socket socket, MockWebsocketFactoryWithCallbackTracking factory) CreateDisconnectedSocket()
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
            );
            return (socket, factory);
        }

        /// <summary>
        /// Creates a basic socket using the simple MockWebsocketFactory.
        /// This matches the original SocketTests.Socket static property.
        /// </summary>
        /// <returns>A new socket with a basic mock websocket factory.</returns>
        protected static Socket CreateBasicSocket()
        {
            return new Socket(
                "ws://localhost:1234",
                null,
                new MockWebsocketFactory(),
                new Socket.Options(new JsonMessageSerializer())
            );
        }

        /// <summary>
        /// Creates a basic socket with optional connection parameters.
        /// This matches the original SocketTests.SocketWithParams method.
        /// </summary>
        /// <param name="params">Optional connection parameters.</param>
        /// <returns>A new socket with a basic mock websocket factory.</returns>
        protected static Socket CreateBasicSocketWithParams(Dictionary<string, string>? @params = null)
        {
            return new Socket(
                "ws://localhost:1234",
                @params,
                new MockWebsocketFactory(),
                new Socket.Options(new JsonMessageSerializer())
            );
        }

        /// <summary>
        /// Creates a channel on the provided socket and successfully joins it.
        /// </summary>
        /// <param name="socket">The socket to create the channel on.</param>
        /// <param name="topic">The channel topic (defaults to "test-topic").</param>
        /// <returns>The joined channel and its join push.</returns>
        protected static (Channel channel, Push joinPush) JoinChannel(Socket socket, string topic = "test-topic")
        {
            var channel = socket.Channel(topic);
            var joinPush = channel.Join();
            joinPush.Trigger(ReplyStatus.Ok);
            return (channel, joinPush);
        }

        #region Phoenix Message Builders

        /// <summary>
        /// Builds a Phoenix phx_reply message in V2 format.
        /// </summary>
        /// <param name="joinRef">The join reference (use "null" for no join ref).</param>
        /// <param name="msgRef">The message reference.</param>
        /// <param name="topic">The channel topic.</param>
        /// <param name="status">The reply status (ok, error, timeout).</param>
        /// <param name="response">The response payload as JSON string (defaults to "{}").</param>
        /// <returns>A Phoenix V2 formatted phx_reply message.</returns>
        protected static string BuildPhxReply(
            string? joinRef,
            string msgRef,
            string topic,
            string status = "ok",
            string response = "{}")
        {
            var joinRefStr = joinRef == null ? "null" : $"\"{joinRef}\"";
            return $"[{joinRefStr},\"{msgRef}\",\"{topic}\",\"phx_reply\",{{\"status\":\"{status}\",\"response\":{response}}}]";
        }

        /// <summary>
        /// Builds a Phoenix message in V2 format.
        /// </summary>
        /// <param name="joinRef">The join reference (use null for no join ref).</param>
        /// <param name="msgRef">The message reference (use null for server-pushed events).</param>
        /// <param name="topic">The channel topic.</param>
        /// <param name="eventName">The event name.</param>
        /// <param name="payload">The payload as JSON string (defaults to "{}").</param>
        /// <returns>A Phoenix V2 formatted message.</returns>
        protected static string BuildPhxMessage(
            string? joinRef,
            string? msgRef,
            string topic,
            string eventName,
            string payload = "{}")
        {
            var joinRefStr = joinRef == null ? "null" : $"\"{joinRef}\"";
            var msgRefStr = msgRef == null ? "null" : $"\"{msgRef}\"";
            return $"[{joinRefStr},{msgRefStr},\"{topic}\",\"{eventName}\",{payload}]";
        }

        /// <summary>
        /// Builds a heartbeat reply message from the Phoenix server.
        /// </summary>
        /// <param name="msgRef">The message reference that was sent with the heartbeat.</param>
        /// <returns>A Phoenix V2 formatted heartbeat reply.</returns>
        protected static string BuildHeartbeatReply(string msgRef)
        {
            return BuildPhxReply(null, msgRef, "phoenix", "ok");
        }

        /// <summary>
        /// Builds a join success reply message.
        /// </summary>
        /// <param name="joinRef">The join reference.</param>
        /// <param name="topic">The channel topic.</param>
        /// <param name="response">Optional response payload (defaults to "{}").</param>
        /// <returns>A Phoenix V2 formatted join success reply.</returns>
        protected static string BuildJoinOkReply(string joinRef, string topic, string response = "{}")
        {
            return BuildPhxReply(joinRef, joinRef, topic, "ok", response);
        }

        /// <summary>
        /// Builds a join error reply message.
        /// </summary>
        /// <param name="joinRef">The join reference.</param>
        /// <param name="topic">The channel topic.</param>
        /// <param name="response">Optional response payload with error details (defaults to "{}").</param>
        /// <returns>A Phoenix V2 formatted join error reply.</returns>
        protected static string BuildJoinErrorReply(string joinRef, string topic, string response = "{}")
        {
            return BuildPhxReply(joinRef, joinRef, topic, "error", response);
        }

        /// <summary>
        /// Builds a push reply message (for channel.push() responses).
        /// </summary>
        /// <param name="joinRef">The join reference of the channel.</param>
        /// <param name="msgRef">The message reference of the push.</param>
        /// <param name="topic">The channel topic.</param>
        /// <param name="status">The reply status (ok, error).</param>
        /// <param name="response">Optional response payload (defaults to "{}").</param>
        /// <returns>A Phoenix V2 formatted push reply.</returns>
        protected static string BuildPushReply(
            string joinRef,
            string msgRef,
            string topic,
            string status = "ok",
            string response = "{}")
        {
            return BuildPhxReply(joinRef, msgRef, topic, status, response);
        }

        /// <summary>
        /// Builds a server-pushed event message.
        /// </summary>
        /// <param name="joinRef">The join reference of the channel.</param>
        /// <param name="topic">The channel topic.</param>
        /// <param name="eventName">The event name.</param>
        /// <param name="payload">The event payload as JSON string (defaults to "{}").</param>
        /// <returns>A Phoenix V2 formatted server event message.</returns>
        protected static string BuildServerEvent(
            string joinRef,
            string topic,
            string eventName,
            string payload = "{}")
        {
            return BuildPhxMessage(joinRef, null, topic, eventName, payload);
        }

        /// <summary>
        /// Builds a presence_state message.
        /// </summary>
        /// <param name="joinRef">The join reference of the channel.</param>
        /// <param name="topic">The channel topic.</param>
        /// <param name="presenceState">The presence state as JSON string (e.g., "{\"user1\":{\"metas\":[{\"phx_ref\":\"ref1\"}]}}").</param>
        /// <returns>A Phoenix V2 formatted presence_state message.</returns>
        protected static string BuildPresenceState(string joinRef, string topic, string presenceState)
        {
            return BuildServerEvent(joinRef, topic, "presence_state", presenceState);
        }

        /// <summary>
        /// Builds a presence_diff message.
        /// </summary>
        /// <param name="joinRef">The join reference of the channel.</param>
        /// <param name="topic">The channel topic.</param>
        /// <param name="joins">The joins object as JSON string.</param>
        /// <param name="leaves">The leaves object as JSON string.</param>
        /// <returns>A Phoenix V2 formatted presence_diff message.</returns>
        protected static string BuildPresenceDiff(string joinRef, string topic, string joins = "{}", string leaves = "{}")
        {
            return BuildServerEvent(joinRef, topic, "presence_diff", $"{{\"joins\":{joins},\"leaves\":{leaves}}}");
        }

        #endregion
    }
}
