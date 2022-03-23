using Phoenix;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WebSocketSharp;

namespace PhoenixTests {
	public sealed class WebsocketSharpAdapter : IWebsocket {

		private readonly WebSocket ws;
		private readonly WebsocketConfiguration config;


		public WebsocketSharpAdapter(WebSocket ws, WebsocketConfiguration config) {
			this.ws = ws;
			this.config = config;

			ws.OnOpen += OnWebsocketOpen;
			ws.OnClose += OnWebsocketClose;
			ws.OnError += OnWebsocketError;
			ws.OnMessage += OnWebsocketMessage;
		}


		#region IWebsocket methods

		public WebsocketState state {
			get {
				switch (ws.ReadyState) {
					case WebSocketState.Connecting:
						return WebsocketState.Connecting;
					case WebSocketState.Open:
						return WebsocketState.Open;
					case WebSocketState.Closing:
						return WebsocketState.Closing;
					default:
						return WebsocketState.Closed;
				}
			}
		}

		public void Connect() {
			ws.Connect();
		}

		public void Send(string message) {
			ws.Send(message);
		}

		public void Close(ushort? code = null, string message = null) {
			ws.Close();
		}

		#endregion


		#region websocketsharp callbacks

		public void OnWebsocketOpen(object sender, EventArgs args) {
			config.onOpenCallback(this);
		}

		public void OnWebsocketClose(object sender, CloseEventArgs args) {
			config.onCloseCallback(this, args.Code, args.Reason);
		}

		public void OnWebsocketError(object sender, ErrorEventArgs args) {
			config.onErrorCallback(this, args.Message);
		}

		public void OnWebsocketMessage(object sender, MessageEventArgs args) {
			config.onMessageCallback(this, args.Data);
		}

		#endregion
	}

	public sealed class WebsocketSharpFactory : IWebsocketFactory {

		public IWebsocket Build(WebsocketConfiguration config) {

			var socket = new WebSocket(config.uri.AbsoluteUri);
			return new WebsocketSharpAdapter(socket, config);
		}
	}
}
