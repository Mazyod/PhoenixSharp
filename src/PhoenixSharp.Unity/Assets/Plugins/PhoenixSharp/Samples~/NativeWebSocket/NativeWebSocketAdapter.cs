// NativeWebSocket Adapter for PhoenixSharp
//
// IMPORTANT: NativeWebSocket queues received messages internally on ALL platforms
// (not just WebGL). You MUST call Tick() every frame from your MonoBehaviour's
// Update() method, otherwise OnMessage callbacks will never fire and channels
// will timeout and rejoin in an infinite loop.
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
//       // Required: pump NativeWebSocket's internal message queue every frame.
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

            var websocket = new NativeWebSocket.WebSocket(config.uri.ToString());

            var adapter = new NativeWebSocketAdapter(websocket, config);

            websocket.OnOpen += () => config.onOpenCallback(adapter);
            websocket.OnClose += (code) => config.onCloseCallback(adapter, (ushort)code, code.ToString());
            websocket.OnError += (error) => config.onErrorCallback(adapter, error);
            websocket.OnMessage += (data) => config.onMessageCallback(adapter, Encoding.UTF8.GetString(data));

            return adapter;
        }
    }

    public sealed class NativeWebSocketAdapter : IWebsocket {

        private readonly NativeWebSocket.WebSocket _ws;
        private readonly WebsocketConfiguration _config;

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
            _config = default;
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
        /// NativeWebSocket receives data on a background thread but does NOT invoke
        /// OnMessage directly. Instead, it queues messages and waits for you to call
        /// DispatchMessageQueue(). Without this call, received messages are silently
        /// buffered, OnMessage never fires, and PhoenixSharp channels will timeout
        /// waiting for server replies — causing an infinite rejoin loop.
        ///
        /// Call this every frame from your MonoBehaviour's Update() method.
        /// Performance cost is negligible (lock + list copy, typically 0-2 items).
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

        public async void Close(ushort? code = null, string message = null) {
            // NativeWebSocket's non-WebGL Close() does not accept parameters —
            // it always performs a normal (1000) close. The WebGL JS interop
            // variant does accept a code, but we use the parameterless version
            // for cross-platform consistency.
            try {
                await _ws.Close();
            } catch (Exception exception) {
                // Socket teardown polls the transport and force-completes even
                // when NativeWebSocket cannot finish its close handshake.
                // Reporting this through onErrorCallback while the adapter is
                // still current would incorrectly error every channel during
                // an otherwise clean disconnect.
                UnityEngine.Debug.LogException(exception);
            }
        }

        private void ReportError(string operation, Exception exception) {
            if (_config.onErrorCallback == null) {
                UnityEngine.Debug.LogException(exception);
                return;
            }

            try {
                _config.onErrorCallback(
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
