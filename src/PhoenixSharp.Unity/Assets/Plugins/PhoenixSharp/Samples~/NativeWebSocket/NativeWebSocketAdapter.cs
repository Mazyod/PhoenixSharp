using System;
using System.Text;
using Phoenix;
using NativeWebSocket;


namespace Phoenix {

    public sealed class NativeWebSocketFactory : IWebsocketFactory {

        public IWebsocket Build(WebsocketConfiguration config) {

            var websocket = new NativeWebSocket.WebSocket(config.uri.ToString());

            var adapter = new NativeWebSocketAdapter(websocket);

            websocket.OnOpen += () => config.onOpenCallback(adapter);
            websocket.OnClose += (code) => config.onCloseCallback(adapter, (ushort)code, code.ToString());
            websocket.OnError += (error) => config.onErrorCallback(adapter, error);
            websocket.OnMessage += (data) => config.onMessageCallback(adapter, Encoding.UTF8.GetString(data));

            return adapter;
        }
    }

    sealed class NativeWebSocketAdapter : IWebsocket {

        private readonly NativeWebSocket.WebSocket _ws;

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

        public async void Connect() => await _ws.Connect();
        public async void Send(string message) => await _ws.SendText(message);
        public async void Close(ushort? code = null, string message = null) {
            if (code.HasValue) {
                await _ws.Close((WebSocketCloseCode)code.Value, message);
            } else {
                await _ws.Close();
            }
        }
    }
}
