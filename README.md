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
- Modern async/await API with cancellation and typed lifecycle failures
- Automatic reconnection and channel rejoin
- Thread-safe socket, channel, push, scheduler, and presence state
- Presence tracking with async wait helpers
- Refreshable connection parameters for token rotation
- Structured operational and contained-error reporting
- Level-gated logging with built-in console and Unity sinks
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
    "io.level3.phoenixsharp": "2.0.0"
  }
}
```

Alternatively, download the source and add it to your project, or use a Git submodule.

## Quick Start

```csharp
var options = new Socket.Options(new JsonMessageSerializer())
{
    Logger = new ConsoleLogger()
};

// PhoenixSharp does not ship a transport. Use one of the adapters below.
var socket = new Socket(
    "wss://example.com/socket",
    null,
    new MyWebSocketFactory(),
    options
);

socket.OnError += error =>
    Console.Error.WriteLine($"{error.Kind}: {error.Message}");
socket.OnUnhandledError += error =>
    Console.Error.WriteLine($"Contained {error.Kind}: {error.Message}");
socket.Connect();

var channel = socket.Channel(
    "room:lobby",
    new Dictionary<string, object> { ["userId"] = "123" }
);

channel.On<ChatMessage>("new_message", payload =>
{
    Console.WriteLine($"Received: {payload.Text}");
});

channel.Join();
channel.Push("send_message", new { text = "Hello!" });
```

### Async API

The same setup can use async lifecycle methods instead:

```csharp
try
{
    using var connectTimeout =
        new CancellationTokenSource(TimeSpan.FromSeconds(15));
    await socket.ConnectAsync(connectTimeout.Token);

    var join = await channel.JoinAsync();
    if (join.IsSuccess)
    {
        var push = await channel.PushAsync<ChatMessage>(
            "send_message",
            new { text = "Hello!" }
        );
        if (push.IsSuccess && push.Response != null)
        {
            Console.WriteLine($"Sent with id: {push.Response.Id}");
        }
    }

    await channel.LeaveAsync();
    await socket.DisconnectAsync();
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Connection cancelled");
}
catch (PhoenixConnectionException exception)
{
    Console.Error.WriteLine($"Connection failed: {exception.Message}");
}
catch (PhoenixException exception)
{
    Console.Error.WriteLine($"Phoenix operation failed: {exception.Message}");
}
```

Join and push error/timeout replies are represented by `JoinResult` and `PushResult`. Connection and disconnection failures fault with `PhoenixConnectionException` and `PhoenixException`; disposed use faults with `ObjectDisposedException`.

## Documentation

- [Migration Guide](https://github.com/Mazyod/PhoenixSharp/blob/master/Migration.md) - Upgrading from older versions
- [Integration Tests](PhoenixTests/IntegrationTests.cs) - Complete usage examples

### Refreshable Connection Parameters

Use `ParamsProvider` for credentials that may change between connection attempts. It is called immediately before each transport is built and may be called concurrently, so the provider must be thread-safe. Do not also pass a static parameter dictionary to the `Socket` constructor.

```csharp
var options = new Socket.Options(new JsonMessageSerializer())
{
    ParamsProvider = () => new Dictionary<string, string>
    {
        ["token"] = tokenProvider.GetAccessToken()
    }
};

var socket = new Socket(address, null, factory, options);
```

### WebSocket Implementation

The library requires an `IWebsocket` implementation. Sample implementations are available in [`PhoenixTests/WebSocketImpl`](PhoenixTests/WebSocketImpl).

```csharp
var factory = new MyWebSocketFactory();
var socket = new Socket(address, null, factory, options);
```

Factories receive an immutable `WebsocketConfiguration` with `Uri` and callback properties. `Build` must not invoke callbacks until `Connect()` is called on the returned transport. `State` must be safe to read from any thread, and adapters should preserve close codes and reasons when their underlying transport supports them.

**Recommended implementations:**
- **[NativeWebSocket](https://github.com/endel/NativeWebSocket)** (Unity) - Open source, supports WebGL/Android/iOS/UWP. Install via UPM, then import the sample adapter from the package.
- **[BestHTTP](https://assetstore.unity.com/packages/tools/network/best-http-2-155981)** (Unity) - Commercial plugin. See the sample adapter in the package.
- **System.Net.WebSockets** - Built-in .NET option, no additional dependencies

### JSON Serialization

The default `JsonMessageSerializer` uses [Newtonsoft.Json](https://www.newtonsoft.com/json) with the [Phoenix V2 serialization format](https://github.com/phoenixframework/phoenix/blob/master/lib/phoenix/socket/serializers/v2_json_serializer.ex). To use a different serializer, implement `IMessageSerializer` and `IJsonBox`.

```csharp
var options = new Socket.Options(new MyCustomSerializer());
```

### Errors and Logging

`Socket.OnError` receives operational `PhoenixError` values, including a channel's delivery-blocking `OnMessage` failure. `Socket.OnUnhandledError` receives failures PhoenixSharp deliberately contains so runtime processing can continue, such as an individual throwing event subscriber or a dropped inbound message. When no logger or `OnUnhandledError` handler is configured, the first unobserved contained error emits a fail-safe warning to `Console.Error`.

Use `ConsoleLogger` on .NET, or `UnityLogger` from the auto-referenced `Phoenix.UnityLogger` Unity assembly:

```csharp
var options = new Socket.Options(new JsonMessageSerializer())
{
    // Use new UnityLogger(LogLevel.Info) in Unity.
    Logger = new ConsoleLogger(LogLevel.Info)
};
```

Custom sinks own their filtering and must not throw:

```csharp
public sealed class ErrorOnlyLogger : ILogger
{
    public bool IsEnabled(LogLevel level, string source)
    {
        return level >= LogLevel.Error;
    }

    public void Log(
        LogLevel level,
        string source,
        string message,
        Exception? exception
    )
    {
        try
        {
            Console.Error.WriteLine($"[{level}] [{source}] {message}");
            if (exception != null)
            {
                Console.Error.WriteLine(exception);
            }
        }
        catch
        {
            // ILogger implementations must not throw.
        }
    }
}
```

### Presence

```csharp
var presence = new Presence(channel);

presence.OnJoin += (key, current, newPresence) =>
{
    Console.WriteLine($"{key} joined");
};

presence.OnLeave += (key, current, leftPresence) =>
{
    Console.WriteLine($"{key} left");
};

// Wait for initial state (async)
await presence.WaitForInitialSyncAsync();
```

## Threading model

PhoenixSharp protects its internal state, but it does not serialize user code onto a particular thread. Treat every callback as thread-agnostic: socket delegates (`OnOpen`, `OnClose`, `OnError`, `OnUnhandledError`, and `OnMessage`), channel subscriptions, push reply hooks, presence events (`OnJoin`, `OnLeave`, and `OnSync`), and `ILogger` sinks may all run on any thread.

The default `TaskDelayedExecutor` runs timer callbacks on the thread pool and does not capture the current `SynchronizationContext`. Do not depend on a UI-context unhandled-exception hook: an exception that escapes a user callback running on the pool bypasses that UI hook. Wrap callback bodies and route failures explicitly; PhoenixSharp reports callback failures it contains through `OnError` or `OnUnhandledError`.

Unity consumers must marshal to the main thread before touching `UnityEngine` objects. The UniTask executor sample keeps PhoenixSharp's delayed callbacks on Unity's PlayerLoop, but it does not change the threading contract for transport and subscription callbacks. Queue work to a PlayerLoop or `MonoBehaviour` dispatcher, or use `UniTask.SwitchToMainThread()` in an async flow.

Socket and Presence callbacks (`OnError`, `OnUnhandledError`, `OnJoin`, `OnLeave`, `OnSync`, and friends) are C# events with thread-safe `+=`/`-=`, so handlers can be added or removed from any thread at any time. Handlers themselves may still fire on any thread, and a handler removed concurrently with a dispatch may run one final time.

## Unity Notes

For background on Unity's .NET support, see [Microsoft's Unity scripting documentation](https://docs.microsoft.com/en-us/visualstudio/gamedev/unity/unity-scripting-upgrade).

### Delayed Execution

For Unity, you can replace the default thread-pool executor via `IDelayedExecutor`. The recommended option is **[UniTask](https://github.com/Cysharp/UniTask)** — import the sample from the package and use `UniTaskDelayedExecutor` for zero-allocation delays on Unity's PlayerLoop:

```csharp
var options = new Socket.Options(serializer)
{
    DelayedExecutor = new UniTaskDelayedExecutor()
};
```

A coroutine-based executor is also available as a sample for projects that don't use UniTask.

### WebSocket for Unity

The recommended WebSocket library for Unity is **[NativeWebSocket](https://github.com/endel/NativeWebSocket)** — an open-source, dependency-free WebSocket client that supports WebGL, Android, iOS, and UWP. Install it via UPM with the git URL, then import the **NativeWebSocket** sample adapter from the PhoenixSharp package in Unity's Package Manager.

Alternatively, **[BestHTTP](https://assetstore.unity.com/packages/tools/network/best-http-2-155981)** is a commercial plugin. A sample adapter is also included in the package.

### Recommended Libraries

- **[UniTask](https://github.com/Cysharp/UniTask)** - Zero-allocation async/await; use with the UniTask Delayed Executor sample for best performance
- **[NativeWebSocket](https://github.com/endel/NativeWebSocket)** - Open-source WebSocket client for Unity (WebGL/Android/iOS/UWP)
- **[BestHTTP](https://assetstore.unity.com/packages/tools/network/best-http-2-155981)** - Commercial plugin with a package sample adapter
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
# Offline suite, including allocation guards
dotnet test --filter "Category!=Integration"

# Exclude performance tests when iterating
dotnet test --filter "Category!=Integration&Category!=Performance"

# Integration tests (requires server)
dotnet test --filter "Category=Integration"
```

Integration tests run against `phoenix-sharp.level3.io`. Server source: [phoenix-integration-tester](https://github.com/Mazyod/phoenix-integration-tester)

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
