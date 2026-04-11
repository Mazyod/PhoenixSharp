# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is a Phoenix (Elixir) server that acts as a public integration test target for [phoenix-sharp](https://github.com/Mazyod/phoenix-sharp), a C# Phoenix Channels client library. It is stateless (no database) and exposes a WebSocket endpoint and a health-check REST endpoint for client libraries to test against.

## Common Commands

```bash
mix setup              # Install dependencies (alias for deps.get)
mix phx.server         # Start dev server on localhost:4000
mix test               # Run all tests
mix test path/to/test.exs           # Run a single test file
mix test path/to/test.exs:LINE      # Run a specific test by line number
```

Production requires `SECRET_KEY_BASE` env var. Optional: `PHX_HOST`, `PORT`.

## Architecture

**Endpoint & Socket:** `TesterWeb.Endpoint` serves WebSocket connections at `/socket` via `TesterWeb.UserSocket`, which routes `tester:*` topics to `TesterWeb.TesterChannel`.

**TesterChannel** (`lib/tester_web/channels/tester_channel.ex`) — the core of the server:
- **Join:** Requires an `"auth"` key in the join payload; rejects without it. On join, tracks presence and pushes an `"after_join"` event.
- **Message handlers:** `"reply_test"` (echoes params back), `"push_test"` (pushes `{works: true}`), `"error_test"` (returns error), `"timeout_test"` (no-ops to trigger client timeout).
- **Presence:** `TesterWeb.Presence` tracks users with `online_at` timestamp and hardcoded device metadata (`make: "Apple"`, `model: "iPhone 11"`).

**REST:** Single route `GET /api/health-check` returns `{ok: true}` via `TesterWeb.BaseController`.

**OTP supervision tree** (`lib/tester/application.ex`): Telemetry → PubSub → Endpoint → Presence.
