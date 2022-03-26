using Phoenix;
using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PhoenixTests {
	public sealed class DotNetWebSocketAdapter : IWebsocket {

		private readonly ClientWebSocket ws;
		private readonly WebsocketConfiguration config;
		private readonly UTF8Encoding encoder = new();
		private const int receiveChunkSize = 1024;
		private Task<WebSocketReceiveResult> receiveTask;
		private readonly bool async;

		public DotNetWebSocketAdapter(ClientWebSocket ws, WebsocketConfiguration config, bool async = false) {
			this.ws = ws;
			this.config = config;
			this.async = async;
		}

		#region IWebsocket methods

		public WebsocketState state {
			get {
				return ws.State switch {
					WebSocketState.Connecting => WebsocketState.Connecting,
					WebSocketState.Open => WebsocketState.Open,
					WebSocketState.CloseSent => WebsocketState.Closing,
					WebSocketState.CloseReceived => WebsocketState.Closing,
					_ => WebsocketState.Closed,
				};
			}
		}

		public void Connect() {
			try {
				var task = this.ws.ConnectAsync(config.uri, CancellationToken.None);
				task.Wait();
				receiveTask = Receive();

				this.config.onOpenCallback(this);
			} catch (Exception ex) {
				this.config.onErrorCallback(this, ex.Message);
			}
		}

		public void Send(string message) {
			if (!SendMessage(message))
				return;

			if (!async) {
				try {
					this.receiveTask.Wait();
				} catch (Exception e) {
					this.config.onErrorCallback(this, e.Message);
					return;
				}
			}
		}

		private async Task<WebSocketReceiveResult> Receive() {
			byte[] buffer = new byte[receiveChunkSize];
			var result = await this.ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
			if (result.MessageType == WebSocketMessageType.Close) {
				await this.ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
			} else {
				this.config.onMessageCallback(this, System.Text.Encoding.Default.GetString(buffer));
				this.receiveTask = Receive();
			}
			return result;
		}

		private bool SendMessage(string message) {

			//byte[] buffer = encoder.GetBytes("{\"op\":\"blocks_sub\"}"); //"{\"op\":\"unconfirmed_sub\"}");
			byte[] buffer = encoder.GetBytes(message);

			if (this.ws.State == WebSocketState.Open) {
				this.ws.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
				return true;
			} else {
				this.config.onErrorCallback(this, "Could not send message because websocket is closed.");
				return false;
			}
		}

		public void Close(ushort? code = null, string message = null) {
			WebSocketCloseStatus status;
			Task closeTask;

			this.config.onCloseCallback(this, code ?? 0, message);

			if (code.HasValue && Enum.TryParse(code.ToString(), out status)) {
				closeTask = this.ws.CloseAsync(status, message, CancellationToken.None);
			} else {
				closeTask = this.ws.CloseAsync(WebSocketCloseStatus.Empty, message, CancellationToken.None);
			}
			try {
				closeTask.Wait();
			} catch (Exception ex) {
				this.config.onErrorCallback(this, ex.Message);
			} finally {
				this.ws.Dispose();
			}

		}

		#endregion
	}

	public sealed class DotNetWebSocketFactory : IWebsocketFactory {
		public IWebsocket Build(WebsocketConfiguration config) {
			var socket = new ClientWebSocket();
			return new DotNetWebSocketAdapter(socket, config);
		}
	}
}
