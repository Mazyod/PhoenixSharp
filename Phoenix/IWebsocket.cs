using System;


namespace Phoenix {

	public struct WebsocketConfiguration {

		public Uri uri;

		public Action onOpenCallback;
		public Action onCloseCallback;
		public Action onErrorCallback;
		public Action<string> onMessageCallback;
	}

	public interface IWebsocketFactory {
		IWebsocket Build(WebsocketConfiguration config);
	}

	public interface IWebsocket {

		void Connect();
		void Send(string data);
		void Close(ushort? code = null, string reason = null);
	}
}
