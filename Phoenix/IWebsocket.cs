using System;


namespace Phoenix {

	public struct WebsocketConfiguration {

		public Uri uri;

		public Action<IWebsocket> onOpenCallback;
		public Action<IWebsocket, ushort, string> onCloseCallback;
		public Action<IWebsocket, string> onErrorCallback;
		public Action<IWebsocket, string> onMessageCallback;
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
