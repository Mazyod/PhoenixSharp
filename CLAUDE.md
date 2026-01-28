# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build and Test Commands

```bash
# Restore dependencies
dotnet restore

# Build the solution
dotnet build

# Run all tests
dotnet test

# Run a specific test
dotnet test --filter "FullyQualifiedName~IntegrationTests.GeneralIntegrationTest"

# Run tests with verbose output
dotnet test --verbosity normal
```

## Project Structure

- **Phoenix/** - Main library (netstandard2.0, C# 8.0)
- **PhoenixTests/** - NUnit tests (net6.0, C# 9.0)
- **Reference/** - Reference implementations for Unity (BestHTTP websocket, coroutine-based delayed executor)

## Architecture

PhoenixSharp is a C# client for Phoenix Channels (Elixir/Phoenix real-time framework). It mirrors the PhoenixJS client architecture.

### Core Components

**Socket** (`Socket.cs`) - Manages the WebSocket connection to a Phoenix server. Handles:
- Connection lifecycle (connect/disconnect/reconnect)
- Heartbeat mechanism
- Message routing to channels
- Send buffer for queued messages when disconnected

**Channel** (`Channel.cs`) - Represents a topic subscription. Handles:
- Join/leave lifecycle with automatic rejoin on errors
- Event subscriptions via `On()` / `Off()`
- Push messages with reply callbacks via `Receive()`
- State machine: Closed → Joining → Joined → Leaving → Errored

**Push** (`Push.cs`) - Represents a message pushed to a channel with timeout and reply handling.

**Presence** (`Presence.cs`) - Tracks user presence state across channel members.

**Message** (`Message.cs`) - Message structure with InBound/OutBound event enums. Uses Phoenix V2 serialization format (array-based).

### Abstraction Interfaces

The library decouples from specific implementations via interfaces:

- **IWebsocket / IWebsocketFactory** (`IWebsocket.cs`) - WebSocket implementation. Sample implementations in `PhoenixTests/WebSocketImpl/`.
- **IMessageSerializer / IJsonBox** (`Message.cs`) - JSON serialization. Default: `JsonMessageSerializer` using Newtonsoft.Json.
- **IDelayedExecutor** (`DelayedExecutor.cs`) - Timer/scheduler abstraction. Default: `TaskDelayedExecutor` using `System.Threading.Task`.

### Key Patterns

- Parameters are passed at construction time (socket address, channel params), not at connect/join time
- Event callbacks use C# delegates instead of JS-style callback references
- Reply handling uses fluent `.Receive(ReplyStatus.Ok, callback)` pattern
- Automatic reconnect/rejoin via configurable `ReconnectAfter` / `RejoinAfter` functions in `Socket.Options`

## Integration Tests

Integration tests require a running Phoenix server. The test host is configured in `IntegrationTests.cs`:
```csharp
private const string Host = "phoenix-sharp.level3.io:3080";
```

Server source: https://github.com/Mazyod/phoenix-integration-tester
