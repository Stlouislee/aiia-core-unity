# LiveLink MCP Server — Protocol Reference

> **Package:** `aiia-core-unity` (Aroaro LiveLink for Unity)
> **Document version:** 2026-05-03
> **MCP Protocol version:** `2025-11-25`
> **Default port:** 8081

---

## Table of Contents

1. [Protocol Overview](#1-protocol-overview)
2. [Transport Modes](#2-transport-modes)
3. [Session Lifecycle & Error Codes](#3-session-lifecycle--error-codes)
4. [MCP Resources (unity:// URIs)](#4-mcp-resources-unity-uris)
5. [MCP Tools](#5-mcp-tools)
6. [MCP Prompts](#6-mcp-prompts)
7. [Dynamic Tool Bridge](#7-dynamic-tool-bridge)
8. [Design Issues & Protocol Compliance Concerns](#8-design-issues--protocol-compliance-concerns)
9. [Refactoring Suggestions](#9-refactoring-suggestions)

---

## 1. Protocol Overview

LiveLink exposes a **Model Context Protocol (MCP)** server over HTTP. MCP is a JSON-RPC 2.0–based protocol that lets LLM agents and external tools interact with a Unity scene through a uniform surface of **Resources** (read-only data), **Tools** (state-mutating operations), and **Prompts** (reusable workflow templates).

### JSON-RPC 2.0 Baseline

Every request and response follows the JSON-RPC 2.0 wire format:

```jsonc
// Request
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "initialize",
  "params": { ... }
}

// Success response
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": { ... }
}

// Error response
{
  "jsonrpc": "2.0",
  "id": 1,
  "error": { "code": -32601, "message": "Method not found: foo" }
}
```

Notifications (`notifications/initialized`) are JSON-RPC requests without an `id`; the server returns no response (HTTP 204).

### Server Capabilities (advertised at `initialize`)

| Capability | Supported | Notes |
|---|---|---|
| `resources` | ✅ | `subscribe: false` (no push updates) |
| `tools` | ✅ | `listChanged: true` (dynamic tools may change at runtime) |
| `prompts` | ✅ | `listChanged: false` |
| `logging` | ✅ | Empty object (no specific log levels declared) |

---

## 2. Transport Modes

The server supports two HTTP-based transport modes on the same port (default 8081). Both share the same JSON-RPC endpoint logic; they differ only in how the client establishes a connection and receives responses.

### 2.1 Streamable HTTP (Recommended)

| Aspect | Detail |
|---|---|
| **Endpoint** | `POST /mcp` |
| **Session header** | None required (stateless) |
| **Protocol version header** | `MCP-Protocol-Version: 2025-11-25` (returned by server on every response) |
| **Consumer header** | `X-LiveLink-Consumer: embedded-agent` or `external` (used for tool exposure policy) |

**Flow:**

```text
Client                              Server
  │                                    │
  │──POST /mcp {initialize}──────────▶│
  │◀──200 {protocolVersion,capab.}────│
  │                                    │
  │──POST /mcp {notifications/init}──▶│
  │◀──204 No Content──────────────────│
  │                                    │
  │──POST /mcp {tools/list}──────────▶│
  │◀──200 {tools:[...]}───────────────│
  │                                    │
  │──POST /mcp {tools/call, ...}─────▶│
  │◀──200 {content:[...]}─────────────│
```

Every `POST /mcp` is a standalone HTTP request–response pair. No long-lived connection is needed. This is the transport used by the embedded agent runtime and modern MCP SDKs.

### 2.2 Legacy SSE (HTTP + Server-Sent Events)

| Aspect | Detail |
|---|---|
| **SSE endpoint** | `GET /sse` |
| **Request endpoint** | `POST /mcp?sessionId={sessionId}` (provided by server via SSE `endpoint` event) |
| **Heartbeat** | SSE comment every 30 seconds |
| **Session timeout** | 5 minutes of inactivity |

**Flow:**

```text
Client                              Server
  │                                    │
  │──GET /sse─────────────────────────▶│
  │◀──event: endpoint─────────────────│
  │   data: /mcp?sessionId=abc123     │
  │                                    │
  │──POST /mcp?sessionId=abc123──────▶│
  │   {initialize}                     │
  │◀──200 {protocolVersion,capab.}────│
  │                                    │
  │──POST /mcp?sessionId=abc123──────▶│
  │   {notifications/initialized}      │
  │◀──204 No Content──────────────────│
  │                                    │
  │  ... (keep SSE connection alive)   │
  │◀──: heartbeat (every 30s)─────────│
```

The SSE connection must remain open while the session is active. If it drops, the session is removed from the server's session registry and a new `GET /sse` must be established.

### 2.3 Health Endpoint

```text
GET /health  →  200 {"status":"ok","protocol":"MCP","version":"1.0","transport":"HTTP+SSE"}
GET /        →  (same response)
```

### 2.4 CORS

All responses include permissive CORS headers:

```text
Access-Control-Allow-Origin: *
Access-Control-Allow-Methods: POST, GET, OPTIONS
Access-Control-Allow-Headers: Content-Type, Accept, Origin, MCP-Protocol-Version, MCP-Session-Id, Last-Event-ID, X-LiveLink-Consumer
```

`OPTIONS` requests return 204 immediately.

---

## 3. Session Lifecycle & Error Codes

### Session Lifecycle (Legacy SSE only)

| State | Trigger |
|---|---|
| **Created** | `GET /sse` establishes an SSE connection; server generates a `sessionId` (GUID) |
| **Initialized** | Client sends `initialize` request; server marks session as initialized and stores `clientInfo` |
| **Active** | Client sends normal MCP requests to `POST /mcp?sessionId=...` |
| **Expired** | 5 minutes of inactivity (no requests); cleaned up by a background sweep every 30 seconds |
| **Disconnected** | SSE connection drops; session is removed immediately |

Streamable HTTP (`POST /mcp` without `sessionId`) is **stateless** — no session is created or required.

### Error Codes

| Code | HTTP Status | Meaning | When |
|---|---|---|---|
| `-32700` | 400 | Parse error | Request body is not valid JSON-RPC |
| `-32601` | 200 | Method not found | Unknown `method` value |
| `-32602` | 200 | Invalid params | Missing required parameter (e.g. `URI`, tool name) |
| `-32603` | 200 | Internal error | Unhandled exception during request processing |
| `-32001` | 401 | Session required | Legacy SSE client sent a request without a valid `sessionId` |
| `-32002` | 403 | Not initialized | Legacy SSE client sent a request before `initialize` |
| `-32004` | 200 | Resource not found | `resources/read` for a non-existent URI or UUID |

**Note:** The server also returns HTTP 405 for unsupported methods on known paths (e.g. `GET /mcp` returns 200 with SSE headers, `POST /sse` returns 405).

---

## 4. MCP Resources (unity:// URIs)

Resources are read-only views of Unity scene data. They are accessed via `resources/read` with a `unity://` URI.

### 4.1 Resource Templates (returned by `resources/list`)

| URI Template | Description |
|---|---|
| `unity://scene/active` | Active scene summary |
| `unity://scene/hierarchy?root=/&depth=2` | Hierarchy tree |
| `unity://go/{instanceId}` | GameObject metadata |
| `unity://go/{instanceId}/components` | Component list |
| `unity://component/{instanceId}/{componentType}` | Component field snapshot |
| `unity://selection` | Editor selection |
| `unity://events/recent?count=50` | Recent scene events |

### 4.2 `unity://scene/active`

Returns a `SceneInfoDTO` with the following fields:

| Field | Type | Description |
|---|---|---|
| `scene_name` | string | Active scene name |
| `scene_path` | string | Asset path |
| `is_loaded` | bool | Whether the scene is loaded |
| `is_dirty` | bool | Whether the scene has unsaved changes |
| `root_count` | int | Number of root GameObjects |
| `object_count` | int | Total object count (recursive) |
| `render_pipeline` | string | `"Built-in"`, `"URP"`, `"HDRP"`, or type name |
| `time_scale` | float | `Time.timeScale` |
| `game_time` | float | `Time.time` |
| `real_time` | float | `Time.realtimeSinceStartup` |
| `frame_count` | long | `Time.frameCount` |
| `quality_level` | int | Quality settings level index |
| `platform` | string | `Application.platform` |
| `unity_version` | string | `Application.unityVersion` |

**Example:**

```json
{"jsonrpc":"2.0","id":1,"method":"resources/read","params":{"uri":"unity://scene/active"}}
```

### 4.3 `unity://scene/hierarchy`

Returns a recursive tree of `HierarchyNodeDTO` nodes.

**Query parameters:**

| Param | Type | Default | Description |
|---|---|---|---|
| `root` | string | `/` | Slash-separated path to a hierarchy root (e.g. `Player/Body`) |
| `depth` | int | `2` | Recursion depth (clamped 1–50) |

**Each node contains:**

| Field | Type |
|---|---|
| `instance_id` | int |
| `name` | string |
| `active` | bool |
| `layer` | int |
| `tag` | string |
| `is_static` | bool |
| `depth` | int |
| `child_count` | int |
| `children` | `HierarchyNodeDTO[]` (recursive) |

**Example:**

```json
{"jsonrpc":"2.0","id":2,"method":"resources/read","params":{"uri":"unity://scene/hierarchy?root=/&depth=3"}}
```

### 4.4 `unity://go/{instanceId}`

Returns `GameObjectMetadataDTO` for a single object.

| Field | Type | Description |
|---|---|---|
| `instance_id` | int | Unity instance ID |
| `name` | string | Object name |
| `active` | bool | `activeSelf` |
| `active_in_hierarchy` | bool | `activeInHierarchy` |
| `is_static` | bool | Static flag |
| `layer` | int | Layer index |
| `tag` | string | Tag |
| `scene` | string | Scene name |
| `transform` | object | Position, rotation, scale (world + local) as float arrays |
| `parent` | object \| null | `{instance_id, name}` |
| `children` | array | `[{instance_id, name, active}]` |
| `component_count` | int | Number of components |

**Example:**

```json
{"jsonrpc":"2.0","id":3,"method":"resources/read","params":{"uri":"unity://go/12345"}}
```

### 4.5 `unity://go/{instanceId}/components`

Returns `ComponentListDTO`.

| Field | Type |
|---|---|
| `instance_id` | int (the GameObject's instance ID) |
| `game_object_name` | string |
| `components` | `[{instance_id, type, short_type, enabled}]` |

The `enabled` field is resolved from `Behaviour.enabled`, `Renderer.enabled`, or `Collider.enabled` as appropriate.

### 4.6 `unity://component/{instanceId}/{componentType}`

Returns `ComponentSnapshotDTO` — a reflection-based snapshot of all public fields and properties.

| Field | Type |
|---|---|
| `instance_id` | int |
| `type` | string (full name) |
| `short_type` | string (short name) |
| `enabled` | bool |
| `fields` | `Dictionary<string, object>` |

**Field serialization rules:**

| Unity type | Serialized as |
|---|---|
| `Vector2/3/4` | `float[]` |
| `Quaternion` | `float[4]` (x,y,z,w) |
| `Color/Color32` | `{r,g,b,a}` |
| `Bounds` | `{center: float[3], size: float[3]}` |
| `Rect` | `{x,y,width,height}` |
| `UnityEngine.Object` | `{instance_id, name, type}` |
| `AnimationCurve`, `Gradient` | Placeholder string (e.g. `"<AnimationCurve>"`) |
| Arrays/Lists (>100 elements) | Truncated to `"<Array [N]>"` / `"<List [N]>"` |
| Everything else | `ToString()` |

**Skipped properties** (to avoid noise or side effects): `hideFlags`, `transform`, `gameObject`, `rigidbody`, `camera`, `light`, `renderer`, `audio`, `collider`, etc.

### 4.7 `unity://selection`

Returns `SelectionDTO` — currently selected objects in the Unity Editor. Only populated in Editor mode; returns `count: 0` in builds.

| Field | Type |
|---|---|
| `count` | int |
| `active_object` | `{instance_id, name, scene}` or null |
| `objects` | `[{instance_id, name, scene}]` |

### 4.8 `unity://events/recent`

Returns `RecentEventsDTO` from the `SceneEventTracker`.

**Query parameters:**

| Param | Type | Default | Description |
|---|---|---|---|
| `count` | int | `50` | Number of recent events (max 1000) |

**Event types tracked:**

| Event | Data fields |
|---|---|
| `ObjectCreated` | `instance_id, name, scene, position, parent_id` |
| `ObjectDestroyed` | `instance_id, name, scene, parent_id` |
| `ObjectParentChanged` | `instance_id, name, old_parent_id, new_parent_id, old_parent_name, new_parent_name` |
| `ObjectTransformChanged` | `instance_id, name, position, rotation, scale` |
| `ObjectActiveChanged` | `instance_id, name, old_active, new_active, active_in_hierarchy` |
| `ObjectNameChanged` | `instance_id, old_name, new_name` |
| `ComponentAdded` | `component_instance_id, component_type, game_object_instance_id, game_object_name` |
| `ComponentRemoved` | (same schema) |
| `ComponentEnabledChanged` | (same + `old_enabled, new_enabled`) |
| `SceneLoaded` | `scene_name, scene_path, build_index, is_loaded` |
| `SceneUnloaded` | `scene_name, scene_path, build_index` |

Each event also includes `event_id` (incrementing int as string), `timestamp` (Unix ms), and `game_time`.

---

## 5. MCP Tools

Tools are invoked via `tools/call`. They are divided into **mutation tools** (modify scene state) and **read tools** (return data without side effects).

### 5.1 Core Scene Management (Mutation)

#### `spawn_object`

Spawn a new object from a registered prefab.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `prefab_key` | string | ✅ | Name of the prefab (e.g. `"Cube"`, `"Sphere"`) |
| `position` | float[3] | — | World position `[x, y, z]` |
| `rotation` | float[4] | — | Quaternion `[x, y, z, w]` |
| `scale` | float[3] | — | Local scale `[x, y, z]` |
| `name` | string | — | Name for the spawned object |
| `parent_uuid` | string | — | UUID of the parent object |

```json
{
  "name": "spawn_object",
  "arguments": {
    "prefab_key": "Cube",
    "position": [0, 1, 0],
    "name": "MCP Cube"
  }
}
```

#### `spawn_gltf`

Spawn a glTF asset at runtime (requires `com.unity.cloud.gltfast`).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `url` | string | —* | URL or `file://` path to `.gltf`/`.glb` |
| `data_base64` | string | —* | Base64-encoded `.glb` bytes |
| `source_uri` | string | — | Original URI for resolving relative refs (with `data_base64`) |
| `id` | string | — | UUID to assign to the root |
| `name` | string | — | Name for the root object |
| `position` | float[3] | — | World position |
| `rotation` | float[4] | — | Quaternion rotation |
| `scale` | float[3] | — | Local scale |
| `parent_uuid` | string | — | UUID of the parent object |

\* One of `url` or `data_base64` is required.

```json
{
  "name": "spawn_gltf",
  "arguments": {
    "url": "https://example.com/model.glb",
    "position": [0, 1, 0],
    "name": "Imported Model"
  }
}
```

#### `transform_object`

Update position, rotation, or scale of an existing object.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `uuid` | string | ✅ | Object UUID |
| `position` | float[3] | — | New world position |
| `rotation` | float[4] | — | New quaternion rotation |
| `scale` | float[3] | — | New local scale |

```json
{
  "name": "transform_object",
  "arguments": {
    "uuid": "abc123",
    "position": [10, 0, 10]
  }
}
```

#### `delete_object`

| Parameter | Type | Required |
|---|---|---|
| `uuid` | string | ✅ |

#### `rename_object`

| Parameter | Type | Required |
|---|---|---|
| `uuid` | string | ✅ |
| `name` | string | ✅ |

#### `set_parent`

| Parameter | Type | Required | Description |
|---|---|---|---|
| `uuid` | string | ✅ | Object to move |
| `parent_uuid` | string | — | New parent (omit for root) |
| `world_position_stays` | bool | — | Keep world transform (default: true) |

#### `set_active`

| Parameter | Type | Required |
|---|---|---|
| `uuid` | string | ✅ |
| `active` | bool | ✅ |

#### `scene_dump`

| Parameter | Type | Required | Description |
|---|---|---|---|
| `include_inactive` | bool | — | Include inactive objects |

Returns a simplified hierarchy with `uuid`, `name`, `parent_uuid`, `active`, `children_count`, and `position` per object.

#### `list_spawnable_objects`

No parameters. Returns `{"prefabs": ["Cube","Sphere",...], "count": N}`.

#### `get_view_context`

Returns camera/player perspective for spatial reasoning.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `camera_tag` | string | — | Camera tag (default: `"MainCamera"`) |
| `include_visible_objects` | bool | — | List objects in camera frustum |
| `raycast_distance` | float | — | Raycast distance (default: 100) |

Returns camera position, orientation, forward/right/up vectors, FOV, raycast hit info, and optionally visible objects.

### 5.2 Read-Optimized Agent Tools

These tools mirror the `unity://` resources but are exposed as tools for embedded agents that prefer `tools/call` over `resources/read`.

| Tool | Parameters | Maps to Resource |
|---|---|---|
| `read_scene_info` | (none) | `unity://scene/active` |
| `read_scene_hierarchy` | `root` (string), `depth` (int) | `unity://scene/hierarchy?...` |
| `read_object` | `uuid` (string) or `instance_id` (int) | `unity://go/{id}` |
| `read_object_components` | `uuid` or `instance_id` | `unity://go/{id}/components` |
| `read_component_snapshot` | `uuid` or `instance_id`, `component_type` (required) | `unity://component/{id}/{type}` |
| `read_selection` | (none) | `unity://selection` |
| `read_recent_events` | `count` (int, default 50) | `unity://events/recent?count=N` |

**Example — read object by UUID:**

```json
{
  "name": "read_object",
  "arguments": { "uuid": "abc123def456" }
}
```

**Example — read component snapshot:**

```json
{
  "name": "read_component_snapshot",
  "arguments": {
    "instance_id": 12345,
    "component_type": "MeshRenderer"
  }
}
```

---

## 6. MCP Prompts

Prompts are reusable workflow templates that return structured `messages` arrays for LLM consumption. They are accessed via `prompts/list` and `prompts/get`.

### 6.1 `scene_analysis`

Analyze the scene, identify hotspots, and suggest concrete MCP tool actions.

| Argument | Required | Default |
|---|---|---|
| `analysis_goal` | No | `"Find structural, gameplay, and performance improvements."` |
| `include_inactive` | No | `false` |
| `focus_query` | No | (empty) |

**Example:**

```json
{
  "name": "prompts/get",
  "params": {
    "name": "scene_analysis",
    "arguments": {
      "analysis_goal": "Check for performance issues with too many draw calls",
      "focus_query": "MeshRenderer"
    }
  }
}
```

### 6.2 `spawn_from_intent`

Turn a natural-language level design intent into spawn/transform operations.

| Argument | Required | Default |
|---|---|---|
| `intent` | ✅ | — |
| `count` | No | `3` |
| `placement_strategy` | No | `"front_of_camera"` |

**Example:**

```json
{
  "name": "prompts/get",
  "params": {
    "name": "spawn_from_intent",
    "arguments": {
      "intent": "Create a small obstacle course in front of the player",
      "count": 5,
      "placement_strategy": "front_of_camera"
    }
  }
}
```

### 6.3 `object_repair`

Diagnose and repair transform/parenting issues for a target object.

| Argument | Required | Default |
|---|---|---|
| `uuid` | ✅ | — |
| `issue_description` | No | `"Object appears misplaced..."` |
| `preserve_world_pose` | No | `true` |

### 6.4 `scene_cleanup`

Produce (and optionally execute) a safe cleanup plan for redundant objects.

| Argument | Required | Default |
|---|---|---|
| `scope` | No | `"inactive_only"` (`all`, `inactive_only`, `name_pattern`) |
| `name_pattern` | No | (empty) |
| `dry_run` | No | `true` |

---

## 7. Dynamic Tool Bridge

LiveLink supports runtime tool discovery from annotated C# methods, so third-party code can expose MCP tools without modifying `MCPToolHandler`.

### Discovery Mechanism

1. Mark static methods with `[LiveLinkTool("tool_name")]`.
2. Optionally use `[LiveLinkToolParameter("arg_name")]` for parameter metadata.
3. LiveLink scans runtime assemblies and registers discovered tools.
4. `tools/list` appends dynamic tools after the built-in set.
5. `tools/call` routes to the dynamic registry first, then falls back to built-ins.

### Build-Time Cache

To avoid startup reflection costs:

1. Generate cache: **LiveLink > Rebuild Tool Cache** (Unity menu).
2. Assign the `LiveLinkToolCache` asset in `LiveLinkManager > Dynamic MCP Tools > Tool Cache Asset`.
3. At runtime, the cache is preferred; reflection is only used as a fallback.

### Zero-Intrusion Manifest Mode

For third-party packages that cannot depend on `LiveLink.Tools`:

1. Create `LiveLinkToolManifest` assets with `Assembly Name`, `Type Name`, and `Method Name`.
2. Add them to `LiveLinkManager > Dynamic MCP Tools > Tool Manifest Assets`.
3. LiveLink resolves and exposes methods without modifying third-party source.

### Consumer-Based Exposure Policy

The server distinguishes between two consumer types via the `X-LiveLink-Consumer` header:

| Consumer | Header value | Use case |
|---|---|---|
| Embedded Agent | `embedded-agent` | In-app agent runtime |
| External | (any other / absent) | Claude Desktop, external MCP clients |

Each consumer has independent allow/deny lists, category/tag filters, and mutation-tool toggles.

---

## 8. Design Issues & Protocol Compliance Concerns

### 8.1 Non-Standard Error Codes

The server uses `-32001`, `-32002`, and `-32004` which are **not** in the JSON-RPC 2.0 spec (which reserves `-32000` to `-32099` for "Server error" but does not define specific values). The MCP spec defines its own error codes. These custom codes should be documented as application-level extensions or aligned with the MCP spec's error code registry.

### 8.2 `notifications/initialized` Returns 204 but No JSON-RPC Response

The server returns HTTP 204 for `notifications/initialized`, which is correct (notifications have no `id` and should not receive a JSON-RPC response). However, the HTTP response still includes `MCP-Protocol-Version` headers, which is fine.

### 8.3 Dual Sync/Async Entry Points

`MCPToolHandler` has both `HandleRequest()` (sync) and `HandleRequestAsync()` (async). The sync version silently rejects `spawn_gltf` with an error instead of executing it. This creates a confusing dual contract. The server always uses the async path via `MainThreadDispatcher`, but the sync path exists and could be called by mistake.

### 8.4 `Connection: close` on Every Response

Every HTTP response includes `Connection: close`, which forces the TCP connection to close after each request. For Streamable HTTP this is wasteful — HTTP/1.1 keep-alive would allow connection reuse. The current design creates a new TCP connection per request, adding latency.

### 8.5 No `MCP-Session-Id` Header Support

The MCP Streamable HTTP spec recommends `MCP-Session-Id` as a response header for stateful servers. The server does not return this header; it only uses query-parameter-based session IDs for legacy SSE. This is acceptable for stateless Streamable HTTP but may cause issues with clients that expect the header.

### 8.6 `tools/call` Error Responses Use `isError: true` Inside `result`

When a tool execution fails, the server returns a **success** JSON-RPC response (no `error` field) with `isError: true` inside the `result.content`. This is actually correct per the MCP spec (tool errors are reported in `result.isError`, not as JSON-RPC errors). However, some tool failures (like "Tool not found") return JSON-RPC `-32601` errors instead, creating an inconsistency: the same class of failure (bad tool name) could surface as either a JSON-RPC error or a tool-level error depending on code path.

### 8.7 No Resource Subscriptions

The server advertises `resources.subscribe: false`. Clients cannot subscribe to resource change notifications. For real-time scene monitoring, clients must poll `resources/read` or `unity://events/recent`.

### 8.8 Instance IDs Are Not Stable Across Domain Reloads

Unity instance IDs reset on domain reload (entering/exiting Play Mode). The `unity://go/{instanceId}` URIs are therefore ephemeral. The `uuid` system (used by the WebSocket transport) is more stable but is not directly exposed in the `unity://` resource scheme. The `read_object` tool bridges this by accepting both `uuid` and `instance_id`.

### 8.9 `scene_dump` Tool Returns Different Format Than `unity://scene/hierarchy`

The `scene_dump` tool returns a flat object list via the WebSocket `CommandPacket` system, while `unity://scene/hierarchy` returns a recursive tree. They serve overlapping purposes with different shapes, which could confuse consumers.

### 8.10 Thread Safety Concerns in `SceneEventTracker`

`SceneEventTracker` uses `List<SceneEventDTO>` and several `Dictionary` instances without synchronization. Events are recorded from Unity callbacks (main thread) and read via `GetRecentEvents` (also main thread via `MainThreadDispatcher`), so this is safe in practice, but the class has no thread-safety documentation or enforcement.

### 8.11 Legacy `mcp://unity` Scheme Still Present

`MCPResourceMapper` still contains legacy `mcp://unity` URI handling and `HandleReadResource` has a fallback path for it. This dead code path should be removed or explicitly deprecated.

---

## 9. Refactoring Suggestions

### 9.1 Unify Sync/Async Handler

Remove `HandleRequest()` (sync) entirely. The server always calls `HandleRequestAsync()` via `MainThreadDispatcher`. The sync path is dead code that can only cause confusion.

### 9.2 Enable HTTP Keep-Alive for Streamable HTTP

For `POST /mcp` requests (Streamable HTTP), support `Connection: keep-alive` and HTTP/1.1 persistent connections. Only use `Connection: close` for legacy SSE connections where the connection lifetime is managed differently.

### 9.3 Extract Resource Routing into a Dedicated Router

`MCPResourceProvider.ReadResource()` is a large if/else chain parsing URI paths. Extract this into a small router class with registered route handlers (e.g. `Route("unity://go/{id}/components", handler)`). This would make adding new resources easier and improve testability.

### 9.4 Make Error Codes Consistent

Decide on a strategy:
- **Option A:** Always return JSON-RPC errors for invalid requests (tool not found, missing params). Return `isError: true` only for runtime failures during tool execution.
- **Option B:** Always use `isError: true` for all tool failures, never return JSON-RPC errors from `tools/call`.

Option A is more standard. Document the chosen approach.

### 9.5 Remove Legacy `mcp://unity` Code Path

Delete `MCPResourceMapper.IsLegacyScheme()`, `GetUUIDFromURI()`, `GetResourceURI()`, and the legacy fallback in `HandleReadResource()`. If backward compatibility is needed, add a deprecation warning log.

### 9.6 Add `MCP-Session-Id` Response Header

For Streamable HTTP, return `MCP-Session-Id` as a response header when the client sends one. This aligns with the MCP spec and helps stateful clients track sessions.

### 9.7 Document the `X-LiveLink-Consumer` Header

This header is the only way for external tools to influence tool exposure policy. It should be documented prominently in the API reference, not just in the embedded agent config section.

### 9.8 Add Pagination to `unity://events/recent`

The `count` parameter only controls how many events to return from the end of the buffer. Add `before_event_id` or `before_timestamp` parameters for pagination, enabling clients to page through history.

### 9.9 Consolidate `SceneEventTracker` State

The tracker maintains six separate dictionaries (`_lastPositions`, `_lastRotations`, etc.) for change detection. Consolidate these into a single `Dictionary<int, TrackedObjectState>` struct to reduce allocations and simplify the code.

### 9.10 Add Input Validation for `unity://component/{id}/{type}`

The `componentType` path segment is used directly in a reflection lookup without sanitization. While this is not a security risk (Unity's reflection API won't expose non-existent types), adding a validation step and a clear error message for invalid type names would improve the developer experience.

### 9.11 Make `scene_dump` and `unity://scene/hierarchy` Consistent

Either deprecate `scene_dump` in favor of `read_scene_hierarchy` (which maps to `unity://scene/hierarchy`), or document the difference clearly. Having two overlapping APIs with different response shapes is a maintenance burden.

### 9.12 Consider `resources/subscribe` for Event Tracking

Since `SceneEventTracker` already records events, enabling `resources.subscribe` (even in a limited form) would allow MCP clients to receive real-time notifications instead of polling. This would require adding SSE-based notification delivery to the Streamable HTTP transport.
