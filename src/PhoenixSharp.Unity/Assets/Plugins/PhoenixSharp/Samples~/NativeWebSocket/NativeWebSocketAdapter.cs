#nullable enable

// NativeWebSocket Adapter for PhoenixSharp
//
// REQUIRES NativeWebSocket 2.x (Close(code, reason) does not exist in 1.x).
//
// IMPORTANT: You MUST call Tick() every frame, even on 2.x. NativeWebSocket 2.x
// auto-dispatches callbacks only through the SynchronizationContext captured
// when the WebSocket is CONSTRUCTED - and PhoenixSharp constructs a fresh
// transport on every automatic reconnect from a thread-pool timer, where no
// Unity context exists. Without Tick(), the socket works until the first
// reconnect and then silently stops receiving (infinite timeout/rejoin loop).
//
// Example usage:
//
//   private Socket _socket;
//
//   void Start() {
//       _socket = new Socket(address, null, new NativeWebSocketFactory(), options);
//       _socket.Connect();
//       _socket.Channel("room:lobby").Join();
//   }
//
//   void Update() {
//       // Required: pumps messages whenever the transport lacks a Unity
//       // SynchronizationContext (always after an automatic reconnect).
//       if (_socket?.Conn is NativeWebSocketAdapter adapter) {
//           adapter.Tick();
//       }
//   }

using System;
using System.Text;
using Phoenix;
using NativeWebSocket;


namespace Phoenix {

    public sealed class NativeWebSocketFactory : IWebsocketFactory {

        public IWebsocket Build(WebsocketConfiguration config) {

            var websocket = new NativeWebSocket.WebSocket(config.Uri.ToString());

            var adapter = new NativeWebSocketAdapter(websocket, config);

            websocket.OnOpen += () => config.OnOpenCallback(adapter);
            websocket.OnClose += (code) => config.OnCloseCallback(adapter, (ushort)code, code.ToString());
            websocket.OnError += (error) => config.OnErrorCallback(adapter, error);
            websocket.OnMessage += (data) => config.OnMessageCallback(adapter, Encoding.UTF8.GetString(data));

            return adapter;
        }
    }

    public sealed class NativeWebSocketAdapter : IWebsocket {

        private readonly NativeWebSocket.WebSocket _ws;
        private readonly WebsocketConfiguration? _config;

        public WebsocketState State {
            get {
                return _ws.State switch {
                    NativeWebSocket.WebSocketState.Connecting => WebsocketState.Connecting,
                    NativeWebSocket.WebSocketState.Open => WebsocketState.Open,
                    NativeWebSocket.WebSocketState.Closing => WebsocketState.Closing,
                    _ => WebsocketState.Closed,
                };
            }
        }

        public NativeWebSocketAdapter(NativeWebSocket.WebSocket ws) {
            _ws = ws;
        }

        public NativeWebSocketAdapter(
            NativeWebSocket.WebSocket ws,
            WebsocketConfiguration config
        ) : this(ws) {
            _config = config;
        }

        /// <summary>
        /// Dispatches queued messages from NativeWebSocket's internal buffer.
        ///
        /// Call this every frame from a MonoBehaviour's Update() method.
        /// NativeWebSocket 2.x auto-dispatches only via the
        /// SynchronizationContext captured at construction; transports built
        /// during automatic reconnects are constructed on thread-pool timers
        /// (no Unity context), so without this pump the socket silently stops
        /// receiving after the first reconnect.
        /// </summary>
        public void Tick() {
            #if !UNITY_WEBGL || UNITY_EDITOR
            _ws.DispatchMessageQueue();
            #endif
        }

        public async void Connect() {
            try {
                await _ws.Connect();
            } catch (Exception exception) {
                ReportError("connect", exception);
            }
        }

        public async void Send(string message) {
            try {
                await _ws.SendText(message);
            } catch (Exception exception) {
                ReportError("send", exception);
            }
        }

        public async void Close(ushort? code = null, string? reason = null) {
            try {
                var closeCode = code.HasValue
                    ? (NativeWebSocket.WebSocketCloseCode)code.Value
                    : NativeWebSocket.WebSocketCloseCode.Normal;
                await _ws.Close(closeCode, reason);
            } catch (Exception exception) {
                // Socket teardown polls the transport and force-completes even
                // when NativeWebSocket cannot finish its close handshake.
                // Reporting this through OnErrorCallback while the adapter is
                // still current would incorrectly error every channel during
                // an otherwise clean disconnect.
                UnityEngine.Debug.LogException(exception);
            }
        }

        private void ReportError(string operation, Exception exception) {
            if (_config == null) {
                UnityEngine.Debug.LogException(exception);
                return;
            }

            try {
                _config.OnErrorCallback(
                    this,
                    $"NativeWebSocket {operation} failed: {exception.Message}"
                );
            } catch (Exception callbackException) {
                // Keep async-void failures contained even if a non-Phoenix
                // consumer supplies a throwing configuration callback.
                UnityEngine.Debug.LogException(exception);
                UnityEngine.Debug.LogException(callbackException);
            }
        }
    }
}
