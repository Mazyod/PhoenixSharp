# Phoenix Integration Tester

A Phoenix server used as the integration test target for [PhoenixSharp](https://github.com/Mazyod/phoenixsharp), a C# Phoenix Channels client library.

The server is deployed publicly so that PhoenixSharp's integration tests can run against it. It exposes a WebSocket endpoint and a health-check REST endpoint covering the key scenarios: channel join with auth, message reply/push/error/timeout, and presence tracking.

## Running Locally

```bash
mix setup
mix phx.server
```

Server starts at `localhost:4000`.

## Running with Docker

```bash
docker compose up --build
```

## API Surface

- **WebSocket**: `/socket` — accepts connections, routes `tester:*` topics
- **Channel join**: requires `{"auth": "<value>"}` in the payload
- **Channel events**: `reply_test`, `push_test`, `error_test`, `timeout_test`
- **Presence**: tracks users with device metadata on join
- **Health check**: `GET /api/health-check` returns `{"ok": true}`

## Running Tests

```bash
mix test
```
