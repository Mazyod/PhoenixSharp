using System.Collections.Generic;
using Phoenix;

namespace PhoenixTests.WebSocketImpl
{
    public sealed class MockWebsocketAdapter : IWebsocket
    {
        private readonly WebsocketConfiguration _config;

        public readonly List<string> CallSend = new();

        public int CallCloseCount;

        public int CallConnectCount;

        public WebsocketState MockState = WebsocketState.Closed;

        public MockWebsocketAdapter(WebsocketConfiguration config)
        {
            _config = config;
        }


        public WebsocketState State => MockState;

        public void Connect()
        {
            CallConnectCount += 1;

            MockState = WebsocketState.Open;
            _config.onOpenCallback?.Invoke(this);
        }

        public void Send(string message)
        {
            CallSend.Add(message);
        }

        public void Close(ushort? code = null, string message = null)
        {
            CallCloseCount += 1;

            _config.onCloseCallback?.Invoke(this, code ?? 0, message);
        }
    }

    public sealed class MockWebsocketFactory : IWebsocketFactory
    {
        public IWebsocket Build(WebsocketConfiguration config)
        {
            return new MockWebsocketAdapter(config);
        }
    }
}