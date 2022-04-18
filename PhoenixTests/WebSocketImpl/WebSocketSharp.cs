using System;
using Phoenix;
using WebSocketSharp;

namespace PhoenixTests.WebSocketImpl
{
    public sealed class WebsocketSharpAdapter : IWebsocket
    {
        private readonly WebsocketConfiguration _config;

        private readonly WebSocket _ws;


        public WebsocketSharpAdapter(WebSocket ws, WebsocketConfiguration config)
        {
            _ws = ws;
            _config = config;

            ws.OnOpen += OnWebsocketOpen;
            ws.OnClose += OnWebsocketClose;
            ws.OnError += OnWebsocketError;
            ws.OnMessage += OnWebsocketMessage;
        }


        public WebsocketState State =>
            _ws.ReadyState switch
            {
                WebSocketState.Connecting => WebsocketState.Connecting,
                WebSocketState.Open => WebsocketState.Open,
                WebSocketState.Closing => WebsocketState.Closing,
                _ => WebsocketState.Closed
            };

        public void Connect()
        {
            _ws.Connect();
        }

        public void Send(string message)
        {
            _ws.Send(message);
        }

        public void Close(ushort? code = null, string message = null)
        {
            _ws.Close();
        }


        private void OnWebsocketOpen(object sender, EventArgs args)
        {
            _config.onOpenCallback(this);
        }

        private void OnWebsocketClose(object sender, CloseEventArgs args)
        {
            _config.onCloseCallback(this, args.Code, args.Reason);
        }

        private void OnWebsocketError(object sender, ErrorEventArgs args)
        {
            _config.onErrorCallback(this, args.Message);
        }

        private void OnWebsocketMessage(object sender, MessageEventArgs args)
        {
            _config.onMessageCallback(this, args.Data);
        }
    }

    public sealed class WebsocketSharpFactory : IWebsocketFactory
    {
        public IWebsocket Build(WebsocketConfiguration config)
        {
            var socket = new WebSocket(config.uri.AbsoluteUri);
            return new WebsocketSharpAdapter(socket, config);
        }
    }
}