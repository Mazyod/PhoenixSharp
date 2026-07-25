#nullable enable
using System;

namespace Phoenix
{
    public enum WebsocketState
    {
        Connecting,
        Open,
        Closing,
        Closed
    }

    /// <summary>
    /// Immutable configuration supplied to an <see cref="IWebsocketFactory"/>.
    /// </summary>
    public sealed class WebsocketConfiguration
    {
        /// <summary>
        /// Gets the URI the transport should connect to.
        /// </summary>
        public Uri Uri { get; }

        /// <summary>
        /// Gets the callback to invoke when the transport opens.
        /// </summary>
        public Action<IWebsocket> OnOpenCallback { get; }

        /// <summary>
        /// Gets the callback to invoke when the transport closes.
        /// </summary>
        public Action<IWebsocket, ushort, string> OnCloseCallback { get; }

        /// <summary>
        /// Gets the callback to invoke when the transport reports an error.
        /// </summary>
        public Action<IWebsocket, string> OnErrorCallback { get; }

        /// <summary>
        /// Gets the callback to invoke when the transport receives a message.
        /// </summary>
        public Action<IWebsocket, string> OnMessageCallback { get; }

        /// <summary>
        /// Creates an immutable WebSocket configuration.
        /// </summary>
        public WebsocketConfiguration(
            Uri uri,
            Action<IWebsocket> onOpenCallback,
            Action<IWebsocket, ushort, string> onCloseCallback,
            Action<IWebsocket, string> onErrorCallback,
            Action<IWebsocket, string> onMessageCallback
        )
        {
            Uri = uri ?? throw new ArgumentNullException(nameof(uri));
            OnOpenCallback = onOpenCallback
                ?? throw new ArgumentNullException(nameof(onOpenCallback));
            OnCloseCallback = onCloseCallback
                ?? throw new ArgumentNullException(nameof(onCloseCallback));
            OnErrorCallback = onErrorCallback
                ?? throw new ArgumentNullException(nameof(onErrorCallback));
            OnMessageCallback = onMessageCallback
                ?? throw new ArgumentNullException(nameof(onMessageCallback));
        }
    }

    /// <summary>
    /// Creates WebSocket transports for a <see cref="Socket"/>.
    /// </summary>
    public interface IWebsocketFactory
    {
        /// <summary>
        /// Builds a transport for the supplied configuration.
        /// </summary>
        /// <remarks>
        /// Implementations must not invoke any configuration callback before
        /// <see cref="IWebsocket.Connect"/> is called on the returned transport.
        /// PhoenixSharp claims the returned transport before starting it, so
        /// callbacks raised during <c>Build</c> violate the socket lifecycle.
        /// </remarks>
        IWebsocket Build(WebsocketConfiguration config);
    }

    /// <summary>
    /// WebSocket transport used by a <see cref="Socket"/>.
    /// </summary>
    public interface IWebsocket
    {
        /// <summary>
        /// Gets the current transport state.
        /// </summary>
        /// <remarks>
        /// Implementations must make this property safe to read from any thread.
        /// </remarks>
        WebsocketState State { get; }

        /// <summary>
        /// Starts the transport.
        /// </summary>
        /// <remarks>
        /// Configuration callbacks may be invoked synchronously by this method or
        /// later from any thread, but never before this method is called.
        /// </remarks>
        void Connect();

        /// <summary>
        /// Sends a text message.
        /// </summary>
        /// <remarks>
        /// This is a synchronous <see langword="void"/> contract. Adapters that
        /// wrap an asynchronous send must report failures observed after this
        /// method returns through the supplied configuration's
        /// <see cref="WebsocketConfiguration.OnErrorCallback"/>. PhoenixSharp
        /// surfaces those late failures as transport errors rather than send
        /// errors. A Task-based transport contract is a 3.0 candidate.
        /// </remarks>
        void Send(string data);

        /// <summary>
        /// Closes the transport.
        /// </summary>
        /// <remarks>
        /// Implementations SHOULD forward <paramref name="code"/> and
        /// <paramref name="reason"/> when the underlying transport supports
        /// them. Close callbacks SHOULD likewise preserve the peer's close code
        /// and reason when available.
        /// </remarks>
        void Close(ushort? code = null, string? reason = null);
    }
}
