# Changelog

All notable changes to Unity LiveLink will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.3.0] - 2026-05-05

### Added

- Build-time dynamic tool cache pipeline:
  - New `LiveLinkToolCacheAsset` to store pre-computed attribute-discovered MCP tools.
  - New `LiveLinkToolCacheBuilder` editor/build hook (`LiveLink > Rebuild Tool Cache`, plus pre-build refresh).
- `LiveLinkManager` now supports assigning a tool cache asset for dynamic MCP discovery.

#### A2A (Agent-to-Agent) Protocol Support

- **A2A Client** (`Runtime/Agent/A2A/`): delegate tasks to remote A2A-compliant agents (OpenClaw, Hermes, etc.)
  - `A2AClient` — lightweight HTTP+JSON client (IL2CPP-compatible, no external deps)
  - `A2AAgentToolWrapper` — wraps remote A2A agents as callable `AIFunction` tools
  - `AgentA2ARemoteConfig` — serializable Inspector config for remote agents
  - Agent card discovery via `/.well-known/agent-card.json`
  - SSE streaming with automatic reconnection (exponential backoff)
- **A2A Host Server** (`Runtime/Agent/A2A/A2AHostServer.cs`): expose Unity agent as discoverable A2A endpoint
  - `GET /.well-known/agent-card.json` — agent discovery
  - `POST /a2a` — receive messages, return agent responses (sync + SSE streaming)
  - `GET /health` — health check
  - Bearer token authentication, IP rate limiting, CORS support
  - Configurable via `A2AHostConfig` in `AgentRuntimeConfig` Inspector
- **Android/Quest platform compatibility**:
  - Thread-safe logging (`[Conditional("UNITY_EDITOR")]` prevents background-thread crashes on IL2CPP)
  - `link.xml` preserves `System.Text.Json` types from IL2CPP stripping
  - Self-signed certificate support via `AcceptSelfSignedCertificates` config toggle
- 48 unit tests for A2A types, client, host server, and tool wrapper

### Fixed

- **MCP client resilience** (Issue #46):
  - Initialization now retries with exponential backoff (3 attempts)
  - MCP connection heartbeat (30s) with automatic reconnection on failure
  - `OnConnectionLost` / `OnConnectionRestored` events for UI integration
  - `HttpWebRequest` replaced with cross-platform `HttpClient` for health probes
  - `_isInitialized` / `_isBusy` fields now `volatile` for thread safety
  - SSE transport override in Inspector now shows clear warning with fix instructions
- HttpClient socket exhaustion in `GetAgentCardAsync` (now accepts optional handler)
- SSE multi-line `data:` lines now joined with `\n` per SSE spec
- `CancellationToken` now properly interrupts `ReadLineAsync` in SSE streaming
- `_delegateToolPrefix` config now correctly wired to `A2AAgentToolWrapper`
- Agent card interface selection now prefers `HTTP+JSON` protocol binding

### Changed

- Dynamic tool registry now uses pre-computed cache first and falls back to runtime reflection scanning when cache is missing or stale.
- `LiveLinkManager` inspector now includes dynamic-tool cache controls and manifest convenience actions (create/ping).

## [1.2.2-beta.1] - 2026-03-21

### Changed

- enhance MCP connection messages

## [1.2.1] - 2026-02-14

### Fixed

- Fixed `ObjectDisposedException` error in `MCPHttpServer` when exiting Play Mode (disposed `HttpListener` now caught gracefully during shutdown)

## [1.2.0] - 2026-02-14

### Added

- **New `unity://` Resource URI Scheme**: Complete redesign of MCP resources with 7 new endpoints
  - `unity://scene/active` — Scene overview (name, path, root count, render pipeline, time, quality, platform, Unity version)
  - `unity://scene/hierarchy?root=/&depth=2` — Configurable hierarchy tree with depth control
  - `unity://go/{instanceId}` — Full GameObject metadata (name, tag, layer, active, parent, children, transform)
  - `unity://go/{instanceId}/components` — Component list with types, instance IDs, and enabled states
  - `unity://component/{instanceId}/{componentType}` — Component field snapshot with all public fields and properties
  - `unity://selection` — Current editor selection (Editor mode only)
  - `unity://events/recent?count=50` — Recent scene events for incremental understanding
- **Scene Event Tracker** (`SceneEventTracker`): MonoBehaviour that records scene mutations
  - Tracks object creation, destruction, reparenting, transform changes, active state changes, name changes
  - Tracks component additions, removals, and enabled state changes
  - Tracks scene load/unload events
  - Ring buffer with configurable max events (default 1000)
- **Resource Provider** (`MCPResourceProvider`): Central resource routing and data extraction
  - URI parsing with query parameter support
  - Reflection-based component field serialization with Unity type handling
  - Handles Vector2/3/4, Quaternion, Color, Bounds, Rect, LayerMask, UnityEngine.Object references
- **Resource DTOs** (`ResourceDTOs.cs`): Type-safe data transfer objects for all resource responses
- Legacy `mcp://unity/` URIs remain supported for backward compatibility

### Changed

- `MCPToolHandler.HandleListResources()` now returns resource URI templates instead of per-object listings
- `MCPToolHandler.HandleReadResource()` routes `unity://` URIs to `MCPResourceProvider`, falls back to legacy handler
- `MCPResourceMapper` now supports both `unity://` and legacy `mcp://unity` URI schemes
- `LiveLinkManager` now initializes `SceneEventTracker` and `MCPResourceProvider`
- All command handlers (spawn, delete, transform, rename, set_parent, set_active) now record events
- MCP prompts updated to reference new `unity://` resource URIs in their workflows

## [1.1.0] - 2026-02-14

### Added

- **MCP Prompts Support**: Full implementation of `prompts/list` and `prompts/get` endpoints
  - `scene_analysis` - Analyze scene hierarchy and propose tool-based fixes
  - `spawn_from_intent` - Natural-language level design to spawn operations
  - `object_repair` - Diagnose and repair transform/parenting issues
  - `scene_cleanup` - Safe scene cleanup workflow planning
- **Session Management**: Complete HTTP+SSE session lifecycle implementation
  - Session creation on SSE connection with unique `sessionId`
  - Session validation for all MCP method calls
  - Session initialization state tracking (`initialize` required before other methods)
  - Automatic session expiration after 5 minutes of inactivity
  - Background cleanup task for expired sessions
  - Session-aware error codes: `-32001` (session required), `-32002` (not initialized)
- **Editor Enhancements**: MCP session status display in LiveLink Manager inspector
- **Client Examples**: Updated Python HTTP client with proper session workflow

### Changed

- MCP HTTP endpoint now requires `?sessionId=...` query parameter for all requests except health check
- `initialize` method now marks session as initialized and stores client info
- SSE broadcast now uses session dictionary instead of raw connection list

### Fixed

- Session state now properly tracked across MCP request lifecycle
- SSE connections cleaned up immediately on disconnect
- Concurrent session access protected with proper locking

## [1.0.0] - 2025-12-10

### Added

- Initial release of Unity LiveLink
- WebSocket server for bidirectional communication
- Scene hierarchy serialization and synchronization
- Delta sync for efficient updates
- Command handling for spawn, transform, delete, rename, set_parent, set_active
- Custom editor inspector with status display and controls
- MainThreadDispatcher for thread-safe Unity API access
- Support for configurable sync scope (WholeScene / TargetObjectOnly)
- Spawnable prefabs registry
- Python and Node.js client examples in documentation
