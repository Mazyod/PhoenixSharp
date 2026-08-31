# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build and Test Commands

```bash
# Restore dependencies
dotnet restore Phoenix.sln

# Build the solution
dotnet build Phoenix.sln --no-restore

# Run the offline suite, including allocation-guarded performance tests
dotnet test PhoenixTests/PhoenixTests.csproj --no-build --filter "Category!=Integration"

# Exclude performance tests when iterating
dotnet test PhoenixTests/PhoenixTests.csproj --no-build --filter "Category!=Integration&Category!=Performance"

# Run a specific test
dotnet test PhoenixTests/PhoenixTests.csproj --no-build --filter "FullyQualifiedName~SocketConnectionTests"

# Integration tests require network access to phoenix-sharp.level3.io
dotnet test PhoenixTests/PhoenixTests.csproj --no-build --filter "Category=Integration"

# ...or point them at the local server from server/docker-compose.yml
(cd server && docker compose up -d --build)
PHOENIX_TEST_SERVER=http://localhost:4000 \
    dotnet test PhoenixTests/PhoenixTests.csproj --no-build --filter "Category=Integration"

# Check formatting (CI uses this)
dotnet format Phoenix.sln --no-restore --verify-no-changes

# Auto-fix formatting issues
dotnet format Phoenix.sln --no-restore
```

## Project Structure

- **src/PhoenixSharp.Unity/Assets/Plugins/PhoenixSharp/Runtime/** - Source of truth for the library
- **Phoenix/Phoenix.csproj** - netstandard2.0/C# 9 project that links `Runtime/**/*.cs` for NuGet
- **Runtime/PhoenixSharp.asmdef** - Engine-free Unity core assembly; **Runtime/Unity/Phoenix.UnityLogger.asmdef** is the nested engine-enabled logger assembly
- **src/PhoenixSharp.Unity/Assets/Plugins/PhoenixSharp/Samples~/** - Unity package samples; these are not compiled by CI and require manual review
- **PhoenixUnityCompile/** - CI fixtures that compile the engine-free core and nested Unity assembly boundaries
- **PhoenixTests/** - NUnit net9.0 tests; `Category=Performance` is optional locally, while `Category=Integration` requires network access
- **server/** - The Phoenix (Elixir) integration-test server, merged into this monorepo; has its own CLAUDE.md

## Publishing

The library is distributed via two channels: **NuGet** (`PhoenixSharp`) and **OpenUPM** (`io.level3.phoenixsharp`). Both are published from a single trigger.

**To release:**
1. Before tagging, update the Unity `package.json` version at `src/PhoenixSharp.Unity/Assets/Plugins/PhoenixSharp/package.json` and the README manifest example, then commit those changes
2. Create the version tag from that commit and publish the GitHub release (for 2.0, `v2.0.0`)
3. The `publish.yml` workflow:
   - Derives the package version from the tag
   - Keeps the Unity manifest and README example aligned with that version
   - Builds and pushes the NuGet package via trusted publishing (OIDC)
4. OpenUPM automatically detects the new git tag and publishes the Unity package from the tagged `package.json`

**Version management:** The git tag sets the NuGet release version, while OpenUPM reads the immutable tagged manifest. The manifest version must therefore be bumped before tagging. The `<Version>` in `Phoenix.csproj` is the local-build fallback and should match the release line.

## Code Style

Code style is enforced via `.editorconfig` and checked in CI. Key conventions:
- 4-space indentation
- Allman-style braces (on new line)
- Private fields use `_camelCase`
- Final newline required

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

- Static parameters are snapshotted at construction; `Options.ParamsProvider` supplies refreshable parameters per connection attempt
- Channel parameters are passed at construction, not at join time
- Event callbacks use C# delegates instead of JS-style callback references
- Reply handling uses fluent `.Receive(ReplyStatus.Ok, callback)` pattern
- Automatic reconnect/rejoin via configurable `ReconnectAfter` / `RejoinAfter` functions in `Socket.Options`

## Integration Tests

Integration tests require a running Phoenix server. They default to `https://phoenix-sharp.level3.io`;
set `PHOENIX_TEST_SERVER` to override it:

```bash
# Full URL - the scheme selects http/ws vs https/wss
PHOENIX_TEST_SERVER=http://localhost:4000

# Bare host[:port] - assumed to be TLS-secured (https/wss)
PHOENIX_TEST_SERVER=phoenix-sharp.level3.io
```

The server itself lives in `server/` and runs locally via Docker:

```bash
cd server && docker compose up -d --build   # --build, or compose pulls the ghcr image
curl http://localhost:4000/api/health-check # -> {"ok":true}
docker compose down
```

Server source: https://github.com/Mazyod/phoenix-integration-tester
