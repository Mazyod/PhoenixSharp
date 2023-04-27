using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Phoenix;

namespace PhoenixTests.WebSocketImpl
{
    public sealed class DotNetWebSocketAdapter : IWebsocket
    {
        private const int ReceiveChunkSize = 1024;
        private readonly bool _async;
        private readonly WebsocketConfiguration _config;
        private readonly UTF8Encoding _encoder = new();

        private readonly ClientWebSocket _ws;
        private Task<WebSocketReceiveResult> _receiveTask;

        public DotNetWebSocketAdapter(
            ClientWebSocket ws,
            WebsocketConfiguration config,
            bool async = false
        )
        {
            _ws = ws;
            _config = config;
            _async = async;
        }


        public WebsocketState State =>
            _ws.State switch
            {
                WebSocketState.Connecting => WebsocketState.Connecting,
                WebSocketState.Open => WebsocketState.Open,
                WebSocketState.CloseSent => WebsocketState.Closing,
                WebSocketState.CloseReceived => WebsocketState.Closing,
                _ => WebsocketState.Closed
            };

        public void Connect()
        {
            try
            {
                var task = _ws.ConnectAsync(_config.uri, CancellationToken.None);
                task.Wait();
                _receiveTask = Receive();

                _config.onOpenCallback(this);
            }
            catch (Exception ex)
            {
                _config.onErrorCallback(this, ex.Message);
            }
        }

        public void Send(string message)
        {
            if (!SendMessage(message))
            {
                return;
            }

            if (_async)
            {
                return;
            }

            try
            {
                _receiveTask.Wait();
            }
            catch (Exception e)
            {
                _config.onErrorCallback(this, e.Message);
            }
        }

        public void Close(ushort? code = null, string message = null)
        {
            try
            {
                Task closeTask;
                if (code.HasValue && Enum.TryParse(code.ToString(), out WebSocketCloseStatus status))
                {
                    closeTask = _ws.CloseAsync(status, message, CancellationToken.None);
                }
                else
                {
                    closeTask = _ws.CloseAsync(WebSocketCloseStatus.Empty, message, CancellationToken.None);
                }

                closeTask.Wait();
            }
            catch (Exception ex)
            {
                _config.onErrorCallback(this, ex.Message);
            }
            finally
            {
                _ws.Dispose();
                _config.onCloseCallback(this, code ?? 0, message);
            }
        }

        private async Task<WebSocketReceiveResult> Receive()
        {
            var buffer = new byte[ReceiveChunkSize];
            var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await _ws.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    string.Empty,
                    CancellationToken.None
                );
            }
            else
            {
                _config.onMessageCallback(this, Encoding.Default.GetString(buffer));
                _receiveTask = Receive();
            }

            return result;
        }

        private bool SendMessage(string message)
        {
            var buffer = _encoder.GetBytes(message);

            if (_ws.State == WebSocketState.Open)
            {
                _ws.SendAsync(
                    new ArraySegment<byte>(buffer),
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None
                );
                return true;
            }

            _config.onErrorCallback(this, "Could not send message because websocket is closed.");
            return false;
        }
    }

    public sealed class DotNetWebSocketFactory : IWebsocketFactory
    {
        public IWebsocket Build(WebsocketConfiguration config)
        {
            var socket = new ClientWebSocket();
            return new DotNetWebSocketAdapter(socket, config);
        }
    }
}