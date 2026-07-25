using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Phoenix;

namespace PhoenixTests.WebSocketImpl
{
    public sealed class NativeWebSocketAdapter : IWebsocket
    {
        private readonly NativeWebSocket.WebSocket _ws;
        private readonly WebsocketConfiguration _config;
        private CancellationTokenSource? _dispatchCts;
        private volatile bool _closeHandled;

        public WebsocketState State =>
            _ws.State switch
            {
                NativeWebSocket.WebSocketState.Connecting => WebsocketState.Connecting,
                NativeWebSocket.WebSocketState.Open => WebsocketState.Open,
                NativeWebSocket.WebSocketState.Closing => WebsocketState.Closing,
                _ => WebsocketState.Closed
            };

        public NativeWebSocketAdapter(
            NativeWebSocket.WebSocket ws,
            WebsocketConfiguration config
        )
        {
            _ws = ws;
            _config = config;

            _ws.OnOpen += () => config.OnOpenCallback(this);
            _ws.OnClose += (code) =>
            {
                // When Close() is called by the caller, it handles the callback
                // directly to pass the caller-provided code (matching DotNet adapter).
                if (!_closeHandled)
                {
                    config.OnCloseCallback(this, (ushort)code, code.ToString());
                }
            };
            _ws.OnError += (error) => config.OnErrorCallback(this, error);
            _ws.OnMessage += (data) => config.OnMessageCallback(this, Encoding.UTF8.GetString(data));
        }

        public void Connect()
        {
            // Start the async connect + receive loop on a background thread.
            // NativeWebSocket.Connect() establishes the connection and enters
            // a receive loop — the Task never completes until the connection closes.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _ws.Connect();
                }
                catch
                {
                    // Connection errors are surfaced via the OnError event
                }
            });

            // Block until the connection is established
            SpinWait.SpinUntil(
                () => _ws.State == NativeWebSocket.WebSocketState.Open,
                TimeSpan.FromSeconds(10)
            );

            // Small delay: state transitions to Open slightly before the OnOpen
            // event is queued internally. Give the background thread time to enqueue it.
            Thread.Sleep(50);

            // Dispatch the queued OnOpen event (fires OnOpenCallback synchronously)
            _ws.DispatchMessageQueue();

            // Start a background loop to dispatch future message/close events.
            // NativeWebSocket queues all events internally and requires
            // DispatchMessageQueue() to be called periodically (designed for game loops).
            _dispatchCts = new CancellationTokenSource();
            var token = _dispatchCts.Token;
            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        _ws.DispatchMessageQueue();
                        await Task.Delay(10, token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            });
        }

        public void Send(string message)
        {
            _ws.SendText(message).GetAwaiter().GetResult();
        }

        public void Close(ushort? code = null, string? message = null)
        {
            // Suppress the OnClose event handler — we call OnCloseCallback directly
            // to pass the caller-provided code, matching DotNet adapter behavior.
            _closeHandled = true;
            try
            {
                var closeCode = code.HasValue
                    ? (NativeWebSocket.WebSocketCloseCode)code.Value
                    : NativeWebSocket.WebSocketCloseCode.Normal;
                _ws.Close(closeCode, message).GetAwaiter().GetResult();
            }
            catch
            {
                // Close errors are not fatal
            }
            finally
            {
                _dispatchCts?.Cancel();
                _dispatchCts?.Dispose();
                _config.OnCloseCallback(this, code ?? 0, message);
            }
        }
    }

    public sealed class NativeWebSocketFactory : IWebsocketFactory
    {
        public IWebsocket Build(WebsocketConfiguration config)
        {
            var websocket = new NativeWebSocket.WebSocket(config.Uri.ToString());
            return new NativeWebSocketAdapter(websocket, config);
        }
    }
}
