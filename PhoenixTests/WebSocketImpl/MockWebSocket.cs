using Phoenix;
using System.Collections.Generic;

namespace PhoenixTests {
	public sealed class MockWebsocketAdapter : IWebsocket {

		public WebsocketState mockState = WebsocketState.Closed;
		public readonly WebsocketConfiguration config;

		public MockWebsocketAdapter(WebsocketConfiguration config) {
			this.config = config;
		}

		#region IWebsocket methods

		public WebsocketState state => mockState;

		public int callConnectCount = 0;
		public void Connect() {
			callConnectCount += 1;

			mockState = WebsocketState.Open;
			config.onOpenCallback?.Invoke(this);
		}

		public List<string> callSend = new();
		public void Send(string message) {
			callSend.Add(message);
		}

		public int callCloseCount = 0;
		public void Close(ushort? code = null, string message = null) {
			callCloseCount += 1;

			config.onCloseCallback?.Invoke(this, code ?? 0, message);
		}

		#endregion
	}

	public sealed class MockWebsocketFactory : IWebsocketFactory {

		public IWebsocket Build(WebsocketConfiguration config) => new MockWebsocketAdapter(config);
	}
}
