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

		public DotNetWebSocketAdapter(
			ClientWebSocket ws,
			WebsocketConfiguration config,
			bool async = false
		) {
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
				var task = ws.ConnectAsync(config.uri, CancellationToken.None);
				task.Wait();
				receiveTask = Receive();

				config.onOpenCallback(this);
			} catch (Exception ex) {
				config.onErrorCallback(this, ex.Message);
			}
		}

		public void Send(string message) {
			if (!SendMessage(message)) {
				return;
			}

			if (!async) {
				try {
					receiveTask.Wait();
				} catch (Exception e) {
					config.onErrorCallback(this, e.Message);
				}
			}
		}

		private async Task<WebSocketReceiveResult> Receive() {
			byte[] buffer = new byte[receiveChunkSize];
			var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
			if (result.MessageType == WebSocketMessageType.Close) {
				await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
			} else {
				config.onMessageCallback(this, Encoding.Default.GetString(buffer));
				receiveTask = Receive();
			}
			return result;
		}

		private bool SendMessage(string message) {

			byte[] buffer = encoder.GetBytes(message);

			if (ws.State == WebSocketState.Open) {
				ws.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
				return true;
			} else {
				config.onErrorCallback(this, "Could not send message because websocket is closed.");
				return false;
			}
		}

		public void Close(ushort? code = null, string message = null) {
			Task closeTask;

			config.onCloseCallback(this, code ?? 0, message);

			if (code.HasValue && Enum.TryParse(code.ToString(), out WebSocketCloseStatus status)) {
				closeTask = ws.CloseAsync(status, message, CancellationToken.None);
			} else {
				closeTask = ws.CloseAsync(WebSocketCloseStatus.Empty, message, CancellationToken.None);
			}
			try {
				closeTask.Wait();
			} catch (Exception ex) {
				config.onErrorCallback(this, ex.Message);
			} finally {
				ws.Dispose();
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
