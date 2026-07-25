using System;
using Phoenix;
using BestHTTP.WebSocket;


namespace Phoenix {

    public sealed class BestHTTPWebsocketFactory : IWebsocketFactory {

        public IWebsocket Build(WebsocketConfiguration config) {

            var websocket = new WebSocket(config.Uri);
            websocket.InternalRequest.ConnectTimeout = TimeSpan.FromSeconds(8);

            var adapter = new BestHTTPWebsocketAdapter(websocket);

            websocket.OnOpen += (_) => config.OnOpenCallback(adapter);
            websocket.OnClosed += (_, code, message) => config.OnCloseCallback(adapter, code, message);
            websocket.OnError += (_, message) => config.OnErrorCallback(adapter, message);
            websocket.OnMessage += (_, msg) => config.OnMessageCallback(adapter, msg);

            return adapter;
        }
    }

    sealed class BestHTTPWebsocketAdapter : IWebsocket {

        public WebSocket ws { get; private set; }

        public WebsocketState State {
            get {
                return ws.State switch {
                    WebSocketStates.Connecting => WebsocketState.Connecting,
                    WebSocketStates.Open => WebsocketState.Open,
                    WebSocketStates.Closing => WebsocketState.Closing,
                    _ => WebsocketState.Closed,
                };
            }
        }

        public BestHTTPWebsocketAdapter(WebSocket ws) {
            this.ws = ws;
        }

        public void Connect() => ws.Open();
        public void Send(string message) => ws.Send(message);
        public void Close(ushort? code = null, string message = null) {
            if (code.HasValue) {
                ws.Close(code.Value, message);
            } else {
                ws.Close();
            }
        }
    }
}
