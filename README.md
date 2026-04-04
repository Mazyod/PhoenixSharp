<p align="center">
  <img width="340" height="271" alt="phoenix-sharp-splash" src="https://github.com/user-attachments/assets/3977a485-02cb-49a4-b484-b3320d21355a" />
</p>

<p align="center">
  <a href="https://github.com/Mazyod/PhoenixSharp/actions/workflows/dotnet.yml"><img src="https://github.com/Mazyod/PhoenixSharp/actions/workflows/dotnet.yml/badge.svg" alt=".NET" /></a>
  <a href="https://codecov.io/gh/Mazyod/PhoenixSharp"><img src="https://codecov.io/gh/Mazyod/PhoenixSharp/branch/master/graph/badge.svg" alt="codecov" /></a>
  <a href="https://www.nuget.org/packages/PhoenixSharp"><img src="https://img.shields.io/nuget/v/PhoenixSharp" alt="NuGet" /></a>
  <a href="https://openupm.com/packages/io.level3.phoenixsharp/"><img src="https://img.shields.io/npm/v/io.level3.phoenixsharp?label=openupm&registry_uri=https://package.openupm.com" alt="OpenUPM" /></a>
  <img src="https://img.shields.io/badge/netstandard-2.0-blue" alt="netstandard 2.0" />
</p>

<p align="center">
  <strong>A C# client for Phoenix Channels.</strong><br>
  Unity compatible. Powering <a href="http://level3.io">Dama King</a>.
</p>

---

## Features

- Full [Phoenix Channels](https://hexdocs.pm/phoenix/channels.html) protocol support
- Modern async/await API with cancellation support
- Automatic reconnection and channel rejoin
- Presence tracking
- Customizable WebSocket and JSON implementations
- Unity and .NET Standard 2.0 compatible

## Installation

### NuGet

```bash
dotnet add package PhoenixSharp
```

### Unity (OpenUPM)

Install via [openupm-cli](https://github.com/openupm/openupm-cli#openupm-cli):

```bash
openupm add io.level3.phoenixsharp
```

Or add manually to `Packages/manifest.json`:

```json
{
  "scopedRegistries": [
    {
      "name": "package.openupm.com",
      "url": "https://package.openupm.com",
      "scopes": ["io.level3"]
    }
  ],
  "dependencies": {
    "io.level3.phoenixsharp": "1.4.0"
  }
}
```

Alternatively, download the source and add it to your project, or use a Git submodule.

## Quick Start

```csharp
// Create and connect a socket
var socket = new Socket(
    "wss://example.com/socket",
    new Socket.Options(new JsonMessageSerializer())
);
socket.Connect();

// Join a channel
var channel = socket.Channel("room:lobby", new { userId = "123" });
channel.Join();

// Listen for events
channel.On("new_message", message => {
    var payload = message.Payload.Unbox<ChatMessage>();
    Console.WriteLine($"Received: {payload.Text}");
});

// Send messages
channel.Push("send_message", new { text = "Hello!" });
```

### Async API

```csharp
await socket.ConnectAsync();

var result = await channel.JoinAsync();
if (result.IsSuccess)
{
    var response = await channel.PushAsync<ChatMessage>(
        "send_message",
        new { text = "Hello!" }
    );
    if (response.IsSuccess)
        Console.WriteLine($"Sent with id: {response.Response.Id}");
}

await channel.LeaveAsync();
```

## Documentation

- [Migration Guide](https://github.com/Mazyod/PhoenixSharp/blob/master/Migration.md) - Upgrading from older versions
- [Integration Tests](PhoenixTests/IntegrationTests.cs) - Complete usage examples

### WebSocket Implementation

The library requires an `IWebsocket` implementation. Sample implementations are available in [`PhoenixTests/WebSocketImpl`](PhoenixTests/WebSocketImpl).

```csharp
var factory = new MyWebSocketFactory();
var socket = new Socket(address, null, factory, options);
```

**Recommended implementations:**
- **[NativeWebSocket](https://github.com/endel/NativeWebSocket)** (Unity) - Open source, supports WebGL/Android/iOS/UWP. Install via UPM, then import the sample adapter from the package.
- **[BestHTTP](https://assetstore.unity.com/packages/tools/network/best-http-2-155981)** (Unity) - Commercial plugin, handles threading automatically. See sample adapter in the package.
- **System.Net.WebSockets** - Built-in .NET option, no additional dependencies

### JSON Serialization

The default `JsonMessageSerializer` uses [Newtonsoft.Json](https://www.newtonsoft.com/json) with the [Phoenix V2 serialization format](https://github.com/phoenixframework/phoenix/blob/master/lib/phoenix/socket/serializers/v2_json_serializer.ex). To use a different serializer, implement `IMessageSerializer` and `IJsonBox`.

```csharp
var options = new Socket.Options(new MyCustomSerializer());
```

### Presence

```csharp
var presence = new Presence(channel);

presence.OnJoin += (key, current, newPresence) => {
    Console.WriteLine($"{key} joined");
};

presence.OnLeave += (key, current, leftPresence) => {
    Console.WriteLine($"{key} left");
};

// Wait for initial state (async)
await presence.WaitForInitialSyncAsync();
```

## Unity Notes

For background on Unity's .NET support, see [Microsoft's Unity scripting documentation](https://docs.microsoft.com/en-us/visualstudio/gamedev/unity/unity-scripting-upgrade).

### Threading

By default, callbacks use `System.Threading.Tasks` which works with Unity's `SynchronizationContext`. For more control, implement `IDelayedExecutor` (or use [UniTask](https://github.com/Cysharp/UniTask) for better performance):

```csharp
var options = new Socket.Options(serializer)
{
    DelayedExecutor = new CoroutineDelayedExecutor()
};
```

See [`Reference/Unity`](Reference/Unity) for a coroutine-based implementation.

### WebSocket for Unity

The recommended WebSocket library for Unity is **[NativeWebSocket](https://github.com/endel/NativeWebSocket)** — an open-source, dependency-free WebSocket client that supports WebGL, Android, iOS, and UWP. Install it via UPM with the git URL, then import the **NativeWebSocket** sample adapter from the PhoenixSharp package in Unity's Package Manager.

Alternatively, **[BestHTTP](https://assetstore.unity.com/packages/tools/network/best-http-2-155981)** is a commercial plugin that handles threading automatically. A sample adapter is also included in the package.

### Recommended Libraries

- **[NativeWebSocket](https://github.com/endel/NativeWebSocket)** - Open-source WebSocket client for Unity (WebGL/Android/iOS/UWP)
- **[BestHTTP](https://assetstore.unity.com/packages/tools/network/best-http-2-155981)** - Commercial plugin, handles threading automatically
- **[com.unity.nuget.newtonsoft-json](https://docs.unity3d.com/Packages/com.unity.nuget.newtonsoft-json@3.2/manual/index.html)** - Unity's official Newtonsoft.Json package

## API Reference

### Socket

| Method | Description |
|--------|-------------|
| `Connect()` | Connect to the server |
| `ConnectAsync()` | Connect asynchronously |
| `Disconnect()` | Disconnect from the server |
| `DisconnectAsync()` | Disconnect asynchronously |
| `Channel(topic, params)` | Create a channel |

### Channel

| Method | Description |
|--------|-------------|
| `Join()` | Join the channel |
| `JoinAsync()` | Join asynchronously, returns `JoinResult` |
| `Leave()` | Leave the channel |
| `LeaveAsync()` | Leave asynchronously |
| `Push(event, payload)` | Send a message |
| `PushAsync(event, payload)` | Send asynchronously, returns `PushResult` |
| `PushAsync<T>(event, payload)` | Send with typed response |
| `On(event, callback)` | Subscribe to events |
| `Off(event)` | Unsubscribe from events |
| `WaitForEventAsync(event)` | Wait for a single event |

### Presence

| Method | Description |
|--------|-------------|
| `OnJoin` | Event fired when a user joins |
| `OnLeave` | Event fired when a user leaves |
| `OnSync` | Event fired on state sync |
| `WaitForInitialSyncAsync()` | Wait for initial presence state |
| `WaitForUserAsync(key, timeout)` | Wait for a specific user |

## Running Tests

```bash
# All tests
dotnet test

# Unit tests only
dotnet test --filter "Category!=Integration"

# Integration tests (requires server)
dotnet test --filter "Category=Integration"
```

Integration tests run against `phoenix-sharp.level3.io:3080`. Server source: [phoenix-integration-tester](https://github.com/Mazyod/phoenix-integration-tester)

## Contributing

Issues and pull requests are welcome!

## License

MIT

## Author

Maz (Mazyad Alabduljaleel)

---

<p align="center">
  <sub>Logo is a mix of Unity and Phoenix logos. Please don't sue me.</sub>
</p>
