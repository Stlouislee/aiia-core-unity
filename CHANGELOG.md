# Changelog

All notable changes to Unity LiveLink will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Added a public `OnToolCall` event on `EmbeddedAgentRuntime` (`UnityEvent<string, string>`) so external UI can react to tool execution with tool name and serialized arguments.
- Added production-ready file-backed chat history persistence for `EmbeddedAgentRuntime` via `FileChatHistoryProvider`, including atomic writes, corruption fallback, and retention limits.
- Added annotation-based dynamic MCP tool bridge support:
  - `LiveLinkToolAttribute` / `LiveLinkToolParameterAttribute`
  - `LiveLinkToolManifestAsset` zero-intrusion method mapping mode for third-party code
  - dynamic tool registry and invoker (`Runtime/Scripts/Tools/*`)
  - request-context-aware exposure (`embedded-agent` vs external MCP consumer)
  - example annotated tools (`livelink_echo`, `livelink_create_empty_object`)

### Changed

- Exposed `EmbeddedAgentRuntime` UnityEvents as public inspector fields for external UI wiring: `OnResponseReceived`, `OnError`, and `OnStatusChanged`.
- Updated `EmbeddedAgentRuntimeEditor` to bind to the public event field names and show `OnToolCall` in the inspector.
- Extended `AgentRuntimeConfig` and `AgentRuntimeConfigEditor` with persistent chat history controls: enable flag, conversation ID, storage subdirectory, max message retention, and max file size threshold.
- `MCPToolHandler` now appends dynamic annotated tools to `tools/list` and can execute them via `tools/call` before legacy fallback routing.
- `MCPHttpServer` now accepts `X-LiveLink-Consumer` and propagates request consumer context for exposure policy decisions.
- `LiveLinkManager` and `LiveLinkManagerEditor` now include configurable dynamic tool policies (assembly allow-list, exposure toggles, mutation gates, allow/deny lists, category/tag filters).
- `AgentMcpClientFactory` now marks local embedded-agent MCP requests with `X-LiveLink-Consumer: embedded-agent`.

### Fixed

- Added serialized field migration support (`FormerlySerializedAs`) for renamed embedded runtime event fields to keep existing prefab/component event wiring intact.

## [1.3.0] - 2026-03-10

### Added

- **Embedded Agent Runtime** built on Microsoft Agent Framework, including:
  - `EmbeddedAgentRuntime` component for in-app QA and tool calling
  - `AgentRuntimeConfig` asset and editor tooling
  - support for downstream HTTP and stdio MCP servers
  - inspector test prompt workflow for validating the embedded agent in Play Mode
- **Agent-facing MCP tools** for read-heavy QA and additional scene mutation support:
  - `read_scene_info`, `read_scene_hierarchy`, `read_object`, `read_object_components`, `read_component_snapshot`, `read_selection`, `read_recent_events`
  - `rename_object`, `set_parent`, `set_active`
- **Package import support for embedded agent dependencies**, including vendored Agent Framework / MCP assemblies and linker configuration

### Changed

- Built-in LiveLink MCP is now documented and configured as a **Streamable HTTP-first** server on `/mcp`, with legacy `/sse` compatibility retained for older clients
- Local embedded-agent connections now prefer the built-in `/mcp` endpoint and modern MCP transport behavior
- Embedded agent editor UX now includes menu actions for creating the config asset and runtime component, plus a one-click suggested test prompt
- Documentation, README guidance, and Copilot instructions now cover the embedded agent workflow and updated MCP transport expectations

### Fixed

- Fixed Unity import/editor issues around assembly definition layout and hidden `Documentation~` metadata
- Fixed several Unity API obsolescence warnings and logging gaps that made embedded-agent initialization harder to debug
- Improved MCP HTTP transport handling for local loopback debugging, including readiness checks, empty-response handling, and protocol version alignment

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
