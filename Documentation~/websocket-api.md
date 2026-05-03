# LiveLink WebSocket API & Client Integration Guide

> **Package:** `aiia-core-unity` (LiveLink)  
> **Protocol Version:** 1.0  
> **Default Port:** 8080  
> **Transport:** WebSocket (RFC 6455), JSON payloads  
> **Last Updated:** 2026-05-03

---

## Table of Contents

1. [Protocol Overview](#1-protocol-overview)
2. [Connection Lifecycle](#2-connection-lifecycle)
3. [Message Types Reference](#3-message-types-reference)
4. [Full JSON Schemas](#4-full-json-schemas)
5. [Delta Sync Mechanism](#5-delta-sync-mechanism)
6. [Python Client Example](#6-python-client-example)
7. [Node.js Client Example](#7-nodejs-client-example)
8. [Error Handling & Reconnection](#8-error-handling--reconnection)
9. [Design Issues](#9-design-issues)
10. [Refactoring Suggestions](#10-refactoring-suggestions)

---

## 1. Protocol Overview

LiveLink exposes a **custom JSON-over-WebSocket** server on port 8080 (configurable via `LiveLinkManager.Port`). The protocol is purpose-built for real-time Unity scene synchronization: the server pushes full scene state on connect, then delta updates at a configurable frequency. External clients can send commands to spawn, transform, delete, rename, reparent, and toggle GameObjects.

### Key Characteristics

| Property | Value |
|----------|-------|
| **Wire format** | JSON (UTF-8 text frames) |
| **Framing** | Standard WebSocket frames; server sends unmasked, client sends masked |
| **Directionality** | Full-duplex bidirectional |
| **Session model** | Connection-scoped; no explicit session handshake beyond WS upgrade |
| **Authentication** | None (see [Design Issues](#9-design-issues)) |
| **Compression** | None (no `permessage-deflate`) |
| **TLS** | Not supported (`ws://` only) |
| **Max message size** | No enforced limit |

### Architecture

```
┌──────────────────────────────┐
│   External Client            │
│  (Python / Node.js / Web)    │
└──────────────┬───────────────┘
               │  ws://host:8080
               ▼
┌──────────────────────────────┐
│   LiveLinkServer             │  ← Background thread (async I/O)
│   - WebSocket framing        │
│   - Connection management    │
│   - Message routing          │
└──────────────┬───────────────┘
               │  ConcurrentQueue<Action>
               ▼
┌──────────────────────────────┐
│   MainThreadDispatcher       │  ← Unity main thread
└──────────────┬───────────────┘
               │
               ▼
┌──────────────────────────────┐
│   LiveLinkManager            │
│   ├─ SceneScanner (read)     │
│   └─ Command Handlers (write)│
│      - spawn                 │
│      - transform             │
│      - delete                │
│      - rename                │
│      - set_parent            │
│      - set_active            │
│      - scene_dump            │
│      - ping                  │
└──────────────────────────────┘
```

### Companion Transport: MCP HTTP (Port 8081)

LiveLink also exposes an MCP (Model Context Protocol) HTTP server on port 8081 for LLM agent integration. This document covers the **WebSocket transport only**. See the README for MCP HTTP details.

---

## 2. Connection Lifecycle

### 2.1 WebSocket Handshake

The server implements a standard RFC 6455 handshake:

1. Client opens a TCP connection to `ws://{host}:{port}/`.
2. Client sends an HTTP/1.1 `GET` request with `Upgrade: websocket` and a `Sec-WebSocket-Key`.
3. Server validates the upgrade headers, computes `Sec-WebSocket-Accept` using SHA-1, and responds with `101 Switching Protocols`.
4. The connection is now a WebSocket tunnel.

```
Client → Server:
GET / HTTP/1.1
Host: localhost:8080
Upgrade: websocket
Connection: Upgrade
Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==
Sec-WebSocket-Version: 13

Server → Client:
HTTP/1.1 101 Switching Protocols
Connection: Upgrade
Upgrade: websocket
Sec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=
```

### 2.2 Post-Handshake: Automatic Scene Dump

Immediately after a successful handshake, the server **automatically sends a `scene_dump` message** to the newly connected client. This contains the full scene hierarchy, giving the client an initial snapshot without needing to request it.

```json
{
  "type": "scene_dump",
  "timestamp": 1702234567890,
  "payload": {
    "root_id": "scene_root",
    "scene_name": "SampleScene",
    "object_count": 42,
    "objects": [ ... ]
  }
}
```

### 2.3 Steady State

After the initial dump, the client enters the normal message loop:

- **Server → Client:** Periodic `sync` messages (delta updates) at the configured `SyncFrequency`. Also `heartbeat` messages and `response` messages for commands.
- **Client → Server:** Commands (`spawn`, `transform`, `delete`, `rename`, `set_parent`, `set_active`, `scene_dump`, `ping`).

### 2.4 Disconnection

Either side can initiate a close:

- **Client-initiated:** Send a WebSocket close frame (opcode `0x8`). The server removes the client from its tracking list and fires `OnClientDisconnected`.
- **Server-initiated:** `LiveLinkServer.StopServer()` disposes all connections and stops the TCP listener.
- **Abnormal:** TCP disconnect / network failure. The server detects this when `ReadExactlyAsync` returns fewer bytes than expected, and cleans up in the `finally` block.

### 2.5 Connection State Diagram

```
                    ┌──────────────┐
                    │ Disconnected │
                    └──────┬───────┘
                           │ TCP connect
                           ▼
                    ┌──────────────┐
                    │  Handshake   │
                    └──────┬───────┘
                           │ 101 Switching Protocols
                           ▼
                    ┌──────────────┐
              ┌────►│  Connected   │◄────────┐
              │     └──────┬───────┘         │
              │            │                 │
              │     scene_dump received      │
              │            │                 │
              │            ▼                 │
              │     ┌──────────────┐         │
              │     │  Idle / Sync │─────────┘
              │     └──────┬───────┘   (sync messages)
              │            │
              │     close frame / error
              │            │
              │            ▼
              │     ┌──────────────┐
              └─────│ Disconnected │
              retry  └──────────────┘
```

---

## 3. Message Types Reference

All messages are JSON objects with a `"type"` field identifying the message kind.

### 3.1 Server → Client Messages

| Type | Direction | Description |
|------|-----------|-------------|
| `scene_dump` | S→C | Full scene hierarchy (sent on connect and on request) |
| `sync` | S→C | Periodic delta or full update of changed objects |
| `heartbeat` | S→C | Keep-alive with client count |
| `response` | S→C | Command acknowledgement with success/failure |
| `object_spawned` | S→C | Notification that an object was spawned |
| `object_destroyed` | S→C | Notification that an object was deleted |

### 3.2 Client → Server Messages

| Type | Direction | Description |
|------|-----------|-------------|
| `scene_dump` | C→S | Request a full scene dump |
| `spawn` | C→S | Spawn a new object from a registered prefab |
| `transform` | C→S | Update position, rotation, and/or scale |
| `delete` | C→S | Delete an object |
| `rename` | C→S | Rename an object |
| `set_parent` | C→S | Change an object's parent |
| `set_active` | C→S | Enable or disable a GameObject |
| `ping` | C→S | Application-level health check |

---

## 4. Full JSON Schemas

### Common Fields

Every outgoing packet from the server includes:

| Field | Type | Description |
|-------|------|-------------|
| `type` | `string` | Message type identifier |
| `timestamp` | `number` | Unix timestamp in milliseconds (UTC) |

Every incoming command from the client includes:

| Field | Type | Description |
|-------|------|-------------|
| `type` | `string` | Command type identifier |
| `request_id` | `string` | Client-generated correlation ID for response matching |
| `payload` | `object` | Command-specific parameters |

---

### 4.1 `scene_dump` — Full Scene Hierarchy

#### Server → Client (Automatic on Connect)

Sent automatically when a client connects. Contains the complete scene graph.

```json
{
  "type": "scene_dump",
  "timestamp": 1702234567890,
  "payload": {
    "root_id": "scene_root",
    "scene_name": "SampleScene",
    "object_count": 3,
    "objects": [
      {
        "uuid": "a1b2c3d4e5f6",
        "parent_uuid": null,
        "name": "Player",
        "active": true,
        "layer": 0,
        "tag": "Player",
        "transform": {
          "pos": [0.0, 1.0, 0.0],
          "rot": [0.0, 0.0, 0.0, 1.0],
          "scale": [1.0, 1.0, 1.0]
        },
        "children": ["child-uuid-1", "child-uuid-2"]
      },
      {
        "uuid": "child-uuid-1",
        "parent_uuid": "a1b2c3d4e5f6",
        "name": "Camera",
        "active": true,
        "layer": 0,
        "tag": "MainCamera",
        "transform": {
          "pos": [0.0, 2.0, -5.0],
          "rot": [0.0, 0.0, 0.0, 1.0],
          "scale": [1.0, 1.0, 1.0]
        },
        "children": []
      },
      {
        "uuid": "f6e5d4c3b2a1",
        "parent_uuid": null,
        "name": "Ground",
        "active": true,
        "layer": 0,
        "tag": "Untagged",
        "transform": {
          "pos": [0.0, 0.0, 0.0],
          "rot": [0.0, 0.0, 0.0, 1.0],
          "scale": [10.0, 0.1, 10.0]
        },
        "children": []
      }
    ]
  }
}
```

#### Client → Server (Request)

Request a fresh full dump at any time.

```json
{
  "type": "scene_dump",
  "request_id": "req-dump-001",
  "payload": {
    "include_inactive": false
  }
}
```

| Payload Field | Type | Default | Description |
|---------------|------|---------|-------------|
| `include_inactive` | `boolean` | `false` | Whether to include disabled GameObjects |

#### Response

```json
{
  "type": "response",
  "timestamp": 1702234567890,
  "success": true,
  "message": "Scene dump sent",
  "request_id": "req-dump-001",
  "data": null
}
```

The actual scene dump is sent as a separate `scene_dump` message (not embedded in the response).

---

### 4.2 `sync` — Delta / Full Update

Sent periodically by the server based on `SyncFrequency`. When `DeltaSync` is enabled, only changed objects are included.

```json
{
  "type": "sync",
  "timestamp": 1702234567900,
  "is_delta": true,
  "objects": [
    {
      "uuid": "a1b2c3d4e5f6",
      "name": "Player",
      "transform": {
        "pos": [5.0, 1.0, 3.0],
        "rot": [0.0, 0.707, 0.0, 0.707],
        "scale": [1.0, 1.0, 1.0]
      }
    }
  ]
}
```

| Field | Type | Description |
|-------|------|-------------|
| `is_delta` | `boolean` | `true` if only changed objects; `false` for full state |
| `objects` | `SceneObjectDTO[]` | Array of scene objects (may be partial in delta mode) |

**Note:** In delta mode, `SceneObjectDTO` fields may be sparse — only `uuid` and `transform` are guaranteed. The `parent_uuid`, `children`, `active`, `layer`, and `tag` fields may be omitted for brevity.

---

### 4.3 `spawn` — Spawn Object

Create a new GameObject from a registered prefab.

```json
{
  "type": "spawn",
  "request_id": "req-spawn-001",
  "payload": {
    "prefab_key": "Cube",
    "id": "my-custom-uuid",
    "position": [5.0, 0.0, 5.0],
    "rotation": [0.0, 0.0, 0.0, 1.0],
    "scale": [2.0, 2.0, 2.0],
    "name": "My Cube",
    "parent_uuid": "parent-uuid-here"
  }
}
```

| Payload Field | Type | Required | Default | Description |
|---------------|------|----------|---------|-------------|
| `prefab_key` | `string` | **Yes** | — | Name of a prefab registered in `LiveLinkManager.SpawnablePrefabs` |
| `id` | `string` | No | Auto-generated | Custom UUID for the new object |
| `position` | `float[3]` | No | `[0, 0, 0]` | World position `[x, y, z]` |
| `rotation` | `float[4]` | No | `[0, 0, 0, 1]` | Quaternion rotation `[x, y, z, w]` |
| `scale` | `float[3]` | No | `[1, 1, 1]` | Local scale `[x, y, z]` |
| `name` | `string` | No | Prefab name | Name for the new GameObject |
| `parent_uuid` | `string` | No | Scene root | UUID of parent object |

#### Response

```json
{
  "type": "response",
  "timestamp": 1702234567890,
  "success": true,
  "message": "Object spawned",
  "request_id": "req-spawn-001",
  "data": {
    "uuid": "my-custom-uuid",
    "name": "My Cube"
  }
}
```

#### Broadcast Notification

Other connected clients receive:

```json
{
  "type": "object_spawned",
  "timestamp": 1702234567890,
  "uuid": "my-custom-uuid",
  "prefab": "Cube",
  "object": {
    "uuid": "my-custom-uuid",
    "parent_uuid": null,
    "name": "My Cube",
    "transform": { "pos": [5, 0, 5], "rot": [0, 0, 0, 1], "scale": [2, 2, 2] },
    "active": true,
    "layer": 0,
    "tag": "Untagged",
    "children": []
  }
}
```

---

### 4.4 `transform` — Update Transform

Move, rotate, or scale an existing object.

```json
{
  "type": "transform",
  "request_id": "req-transform-001",
  "payload": {
    "uuid": "a1b2c3d4e5f6",
    "position": [10.0, 0.0, 10.0],
    "rotation": [0.0, 0.5, 0.0, 0.866],
    "scale": [1.0, 1.0, 1.0],
    "local": false
  }
}
```

| Payload Field | Type | Required | Default | Description |
|---------------|------|----------|---------|-------------|
| `uuid` | `string` | **Yes** | — | UUID of the target object |
| `position` | `float[3]` | No | Unchanged | New position |
| `rotation` | `float[4]` | No | Unchanged | New quaternion rotation `[x, y, z, w]` |
| `scale` | `float[3]` | No | Unchanged | New scale |
| `local` | `boolean` | No | `false` | `true` = local space; `false` = world space |

#### Response

```json
{
  "type": "response",
  "timestamp": 1702234567890,
  "success": true,
  "message": "Object transformed",
  "request_id": "req-transform-001",
  "data": null
}
```

---

### 4.5 `delete` — Delete Object

Remove a GameObject from the scene.

```json
{
  "type": "delete",
  "request_id": "req-delete-001",
  "payload": {
    "uuid": "a1b2c3d4e5f6",
    "include_children": true
  }
}
```

| Payload Field | Type | Required | Default | Description |
|---------------|------|----------|---------|-------------|
| `uuid` | `string` | **Yes** | — | UUID of the object to delete |
| `include_children` | `boolean` | No | `true` | Whether to recursively delete children |

#### Response

```json
{
  "type": "response",
  "timestamp": 1702234567890,
  "success": true,
  "message": "Object deleted",
  "request_id": "req-delete-001",
  "data": null
}
```

#### Broadcast Notification

```json
{
  "type": "object_destroyed",
  "timestamp": 1702234567890,
  "uuid": "a1b2c3d4e5f6"
}
```

---

### 4.6 `rename` — Rename Object

```json
{
  "type": "rename",
  "request_id": "req-rename-001",
  "payload": {
    "uuid": "a1b2c3d4e5f6",
    "name": "New Name"
  }
}
```

| Payload Field | Type | Required | Description |
|---------------|------|----------|-------------|
| `uuid` | `string` | **Yes** | UUID of the target object |
| `name` | `string` | **Yes** | New name for the GameObject |

#### Response

```json
{
  "type": "response",
  "timestamp": 1702234567890,
  "success": true,
  "message": "Object renamed",
  "request_id": "req-rename-001",
  "data": null
}
```

---

### 4.7 `set_parent` — Reparent Object

```json
{
  "type": "set_parent",
  "request_id": "req-parent-001",
  "payload": {
    "uuid": "child-uuid",
    "parent_uuid": "new-parent-uuid",
    "world_position_stays": true
  }
}
```

| Payload Field | Type | Required | Default | Description |
|---------------|------|----------|---------|-------------|
| `uuid` | `string` | **Yes** | — | UUID of the object to reparent |
| `parent_uuid` | `string` | **Yes** | — | UUID of the new parent (`null` or empty for scene root) |
| `world_position_stays` | `boolean` | No | `true` | If `true`, preserves world-space position during reparent |

#### Response

```json
{
  "type": "response",
  "timestamp": 1702234567890,
  "success": true,
  "message": "Object reparented",
  "request_id": "req-parent-001",
  "data": null
}
```

---

### 4.8 `set_active` — Enable/Disable Object

```json
{
  "type": "set_active",
  "request_id": "req-active-001",
  "payload": {
    "uuid": "a1b2c3d4e5f6",
    "active": false
  }
}
```

| Payload Field | Type | Required | Description |
|---------------|------|----------|-------------|
| `uuid` | `string` | **Yes** | UUID of the target object |
| `active` | `boolean` | **Yes** | `true` = enabled, `false` = disabled |

#### Response

```json
{
  "type": "response",
  "timestamp": 1702234567890,
  "success": true,
  "message": "Object active state changed",
  "request_id": "req-active-001",
  "data": null
}
```

---

### 4.9 `ping` — Health Check

Application-level keep-alive. The server responds with a `pong` message.

```json
{
  "type": "ping",
  "request_id": "req-ping-001",
  "payload": {}
}
```

#### Response

```json
{
  "type": "response",
  "timestamp": 1702234567890,
  "success": true,
  "message": "pong",
  "request_id": "req-ping-001",
  "data": null
}
```

---

### 4.10 `heartbeat` — Server Keep-Alive

Sent periodically by the server (not a response to client action).

```json
{
  "type": "heartbeat",
  "timestamp": 1702234567890,
  "client_count": 3
}
```

| Field | Type | Description |
|-------|------|-------------|
| `client_count` | `number` | Number of currently connected clients |

---

### 4.11 `response` — Command Response

Generic response envelope for all client commands.

```json
{
  "type": "response",
  "timestamp": 1702234567890,
  "success": true,
  "message": "Object spawned",
  "request_id": "req-001",
  "data": {
    "uuid": "new-uuid",
    "name": "My Cube"
  }
}
```

| Field | Type | Description |
|-------|------|-------------|
| `success` | `boolean` | `true` if command succeeded |
| `message` | `string` | Human-readable status message |
| `request_id` | `string` | Correlates to the client's `request_id` |
| `data` | `object \| null` | Command-specific result data (varies by command type) |

---

## 5. Delta Sync Mechanism

### How It Works

When `DeltaSync` is enabled on `LiveLinkManager`:

1. The `SceneScanner` tracks a hash/fingerprint of each object's transform state.
2. On each sync tick (controlled by `SyncFrequency`), only objects whose state has **changed since the last sync** are included in the `sync` message.
3. The `is_delta` field is set to `true`.
4. Clients should **merge** delta objects into their local scene graph, not replace it.

### Client-Side Merging Strategy

```python
# Pseudocode for delta merging
scene_graph = {}  # uuid → object_state

def handle_sync(message):
    if message["is_delta"]:
        # Merge: update only the objects present in the delta
        for obj in message["objects"]:
            uuid = obj["uuid"]
            if uuid in scene_graph:
                scene_graph[uuid].update(obj)
            else:
                scene_graph[uuid] = obj
    else:
        # Full replace
        scene_graph.clear()
        for obj in message["objects"]:
            scene_graph[obj["uuid"]] = obj

def handle_object_destroyed(message):
    scene_graph.pop(message["uuid"], None)
```

### Performance Implications

| Scenario | Objects Sent | Bandwidth |
|----------|-------------|-----------|
| Full sync, 1000 objects | ~1000 | High |
| Delta sync, 5 objects moved | ~5 | Low |
| Delta sync, nothing changed | ~0 | Minimal |

**Recommendation:** Always enable `DeltaSync` in production. Disable only for debugging or initial integration.

---

## 6. Python Client Example

A production-ready Python client with reconnection, structured logging, and proper async handling.

### Requirements

```bash
pip install websockets
```

### Client Implementation

```python
#!/usr/bin/env python3
"""
LiveLink WebSocket Client for Python

Production-ready client with:
- Automatic reconnection with exponential backoff
- Request/response correlation via request_id
- Structured logging
- Type-safe message handling
"""

import asyncio
import json
import logging
import uuid
from dataclasses import dataclass, field
from typing import Any, Callable, Optional

import websockets
from websockets.exceptions import ConnectionClosed

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(message)s")
logger = logging.getLogger("livelink")


@dataclass
class LiveLinkConfig:
    """Client configuration."""
    host: str = "localhost"
    port: int = 8080
    reconnect: bool = True
    max_reconnect_delay: float = 30.0
    initial_reconnect_delay: float = 1.0
    ping_interval: float = 15.0
    ping_timeout: float = 10.0

    @property
    def uri(self) -> str:
        return f"ws://{self.host}:{self.port}"


class LiveLinkClient:
    """Async WebSocket client for Unity LiveLink."""

    def __init__(self, config: Optional[LiveLinkConfig] = None):
        self.config = config or LiveLinkConfig()
        self.ws: Optional[websockets.WebSocketClientProtocol] = None
        self._pending: dict[str, asyncio.Future] = {}
        self._handlers: dict[str, list[Callable]] = {}
        self._running = False
        self._scene: dict[str, dict] = {}  # uuid → object state

    # ── Connection ──────────────────────────────────────────

    async def connect(self):
        """Connect with automatic reconnection."""
        self._running = True
        while self._running:
            try:
                await self._connect_once()
            except (ConnectionClosed, OSError, Exception) as e:
                if not self._running:
                    break
                logger.warning(f"Connection lost: {e}")
                if not self.config.reconnect:
                    break
                delay = self.config.initial_reconnect_delay
                logger.info(f"Reconnecting in {delay}s...")
                await asyncio.sleep(delay)
                delay = min(delay * 2, self.config.max_reconnect_delay)

    async def _connect_once(self):
        """Single connection attempt."""
        logger.info(f"Connecting to {self.config.uri}...")
        async with websockets.connect(
            self.config.uri,
            ping_interval=self.config.ping_interval,
            ping_timeout=self.config.ping_timeout,
        ) as ws:
            self.ws = ws
            logger.info("Connected!")
            self._fire("connected")
            await self._read_loop()

    async def disconnect(self):
        """Gracefully disconnect."""
        self._running = False
        if self.ws:
            await self.ws.close()
            self.ws = None

    async def _read_loop(self):
        """Read and dispatch messages."""
        try:
            async for raw in self.ws:
                msg = json.loads(raw)
                msg_type = msg.get("type")

                # Correlate responses to pending requests
                if msg_type == "response" and msg.get("request_id") in self._pending:
                    future = self._pending.pop(msg["request_id"])
                    if not future.done():
                        future.set_result(msg)
                    continue

                # Update local scene graph
                if msg_type == "scene_dump":
                    for obj in msg.get("payload", {}).get("objects", []):
                        self._scene[obj["uuid"]] = obj
                elif msg_type == "sync":
                    for obj in msg.get("objects", []):
                        if obj["uuid"] in self._scene:
                            self._scene[obj["uuid"]].update(obj)
                        else:
                            self._scene[obj["uuid"]] = obj
                elif msg_type == "object_destroyed":
                    self._scene.pop(msg.get("uuid"), None)

                self._fire(msg_type, msg)
        except ConnectionClosed:
            raise

    # ── Commands ────────────────────────────────────────────

    async def send_command(self, cmd_type: str, payload: dict, timeout: float = 10.0) -> dict:
        """Send a command and wait for its response."""
        request_id = str(uuid.uuid4())
        message = {"type": cmd_type, "request_id": request_id, "payload": payload}

        future: asyncio.Future = asyncio.get_event_loop().create_future()
        self._pending[request_id] = future

        await self.ws.send(json.dumps(message))
        return await asyncio.wait_for(future, timeout=timeout)

    async def spawn(self, prefab_key: str, position=None, rotation=None,
                    scale=None, name=None, parent_uuid=None) -> dict:
        """Spawn a new object from a prefab."""
        payload = {"prefab_key": prefab_key}
        if position is not None: payload["position"] = position
        if rotation is not None: payload["rotation"] = rotation
        if scale is not None: payload["scale"] = scale
        if name: payload["name"] = name
        if parent_uuid: payload["parent_uuid"] = parent_uuid
        return await self.send_command("spawn", payload)

    async def transform(self, uuid: str, position=None, rotation=None,
                        scale=None, local=False) -> dict:
        """Update an object's transform."""
        payload = {"uuid": uuid}
        if position is not None: payload["position"] = position
        if rotation is not None: payload["rotation"] = rotation
        if scale is not None: payload["scale"] = scale
        if local: payload["local"] = local
        return await self.send_command("transform", payload)

    async def delete(self, uuid: str, include_children=True) -> dict:
        """Delete an object."""
        return await self.send_command("delete", {
            "uuid": uuid,
            "include_children": include_children,
        })

    async def rename(self, uuid: str, name: str) -> dict:
        """Rename an object."""
        return await self.send_command("rename", {"uuid": uuid, "name": name})

    async def set_parent(self, uuid: str, parent_uuid: str, world_position_stays=True) -> dict:
        """Reparent an object."""
        return await self.send_command("set_parent", {
            "uuid": uuid,
            "parent_uuid": parent_uuid,
            "world_position_stays": world_position_stays,
        })

    async def set_active(self, uuid: str, active: bool) -> dict:
        """Enable or disable a GameObject."""
        return await self.send_command("set_active", {"uuid": uuid, "active": active})

    async def request_scene_dump(self, include_inactive=False) -> dict:
        """Request a fresh full scene dump."""
        return await self.send_command("scene_dump", {"include_inactive": include_inactive})

    async def ping(self) -> dict:
        """Application-level health check."""
        return await self.send_command("ping", {})

    # ── Event System ────────────────────────────────────────

    def on(self, event: str, handler: Callable):
        """Register an event handler."""
        self._handlers.setdefault(event, []).append(handler)

    def _fire(self, event: str, data: Any = None):
        for handler in self._handlers.get(event, []):
            try:
                handler(data)
            except Exception as e:
                logger.error(f"Handler error for '{event}': {e}")

    # ── Scene Access ────────────────────────────────────────

    @property
    def scene(self) -> dict:
        """Read-only access to the local scene graph."""
        return dict(self._scene)


# ── Usage Example ───────────────────────────────────────────

async def main():
    client = LiveLinkClient(LiveLinkConfig(host="localhost", port=8080))

    # Register handlers
    client.on("connected", lambda _: logger.info("Scene sync starting..."))

    def on_scene_dump(msg):
        count = msg["payload"]["object_count"]
        logger.info(f"Scene loaded: {count} objects")

    def on_sync(msg):
        n = len(msg.get("objects", []))
        if n > 0:
            logger.debug(f"Delta sync: {n} objects changed")

    client.on("scene_dump", on_scene_dump)
    client.on("sync", on_sync)

    # Run client in background
    client_task = asyncio.create_task(client.connect())

    # Wait for connection
    await asyncio.sleep(2)

    # Spawn a cube
    resp = await client.spawn("Cube", position=[0, 2, 0], name="Python Cube")
    if resp["success"]:
        cube_uuid = resp["data"]["uuid"]
        logger.info(f"Spawned cube: {cube_uuid}")

        # Move it
        await client.transform(cube_uuid, position=[5, 1, 0])
        logger.info("Moved cube")

        # Clean up
        await client.delete(cube_uuid)
        logger.info("Deleted cube")

    # Ping
    pong = await client.ping()
    logger.info(f"Ping response: {pong['message']}")

    # Disconnect
    await client.disconnect()
    client_task.cancel()


if __name__ == "__main__":
    asyncio.run(main())
```

---

## 7. Node.js Client Example

A production-ready Node.js client using the `ws` library.

### Requirements

```bash
npm install ws
```

### Client Implementation

```javascript
/**
 * LiveLink WebSocket Client for Node.js
 *
 * Production-ready client with:
 * - Automatic reconnection with exponential backoff
 * - Request/response correlation via request_id
 * - Event-driven architecture
 * - Local scene graph tracking
 */

const WebSocket = require('ws');
const { EventEmitter } = require('events');
const { randomUUID } = require('crypto');

class LiveLinkClient extends EventEmitter {
  constructor(options = {}) {
    super();
    this.host = options.host || 'localhost';
    this.port = options.port || 8080;
    this.reconnect = options.reconnect !== false;
    this.maxReconnectDelay = options.maxReconnectDelay || 30000;
    this.initialReconnectDelay = options.initialReconnectDelay || 1000;

    this.ws = null;
    this._pending = new Map(); // request_id → { resolve, reject, timer }
    this._scene = new Map();   // uuid → object state
    this._reconnectDelay = this.initialReconnectDelay;
    this._running = false;
  }

  get uri() {
    return `ws://${this.host}:${this.port}`;
  }

  // ── Connection ──────────────────────────────────────────

  connect() {
    this._running = true;
    this._connectOnce();
  }

  _connectOnce() {
    console.log(`Connecting to ${this.uri}...`);
    this.ws = new WebSocket(this.uri);

    this.ws.on('open', () => {
      console.log('Connected!');
      this._reconnectDelay = this.initialReconnectDelay;
      this.emit('connected');
    });

    this.ws.on('message', (data) => {
      try {
        const msg = JSON.parse(data.toString());
        this._handleMessage(msg);
      } catch (err) {
        console.error('Failed to parse message:', err.message);
      }
    });

    this.ws.on('close', (code, reason) => {
      console.log(`Disconnected: ${code} ${reason}`);
      this._rejectAllPending('Connection closed');
      this.emit('disconnected', { code, reason });
      this._scheduleReconnect();
    });

    this.ws.on('error', (err) => {
      console.error('WebSocket error:', err.message);
      this.emit('error', err);
    });
  }

  _scheduleReconnect() {
    if (!this._running || !this.reconnect) return;
    console.log(`Reconnecting in ${this._reconnectDelay}ms...`);
    setTimeout(() => {
      if (this._running) this._connectOnce();
    }, this._reconnectDelay);
    this._reconnectDelay = Math.min(this._reconnectDelay * 2, this.maxReconnectDelay);
  }

  disconnect() {
    this._running = false;
    if (this.ws) {
      this.ws.close();
      this.ws = null;
    }
  }

  // ── Message Handling ────────────────────────────────────

  _handleMessage(msg) {
    // Correlate responses
    if (msg.type === 'response' && this._pending.has(msg.request_id)) {
      const { resolve, timer } = this._pending.get(msg.request_id);
      this._pending.delete(msg.request_id);
      clearTimeout(timer);
      resolve(msg);
      return;
    }

    // Update local scene graph
    if (msg.type === 'scene_dump') {
      for (const obj of msg.payload?.objects || []) {
        this._scene.set(obj.uuid, obj);
      }
    } else if (msg.type === 'sync') {
      for (const obj of msg.objects || []) {
        const existing = this._scene.get(obj.uuid);
        this._scene.set(obj.uuid, existing ? { ...existing, ...obj } : obj);
      }
    } else if (msg.type === 'object_destroyed') {
      this._scene.delete(msg.uuid);
    }

    this.emit(msg.type, msg);
  }

  _rejectAllPending(reason) {
    for (const [id, { reject, timer }] of this._pending) {
      clearTimeout(timer);
      reject(new Error(reason));
    }
    this._pending.clear();
  }

  // ── Commands ────────────────────────────────────────────

  sendCommand(type, payload = {}, timeoutMs = 10000) {
    return new Promise((resolve, reject) => {
      if (!this.ws || this.ws.readyState !== WebSocket.OPEN) {
        return reject(new Error('Not connected'));
      }

      const requestId = randomUUID();
      const message = { type, request_id: requestId, payload };

      const timer = setTimeout(() => {
        this._pending.delete(requestId);
        reject(new Error(`Command '${type}' timed out after ${timeoutMs}ms`));
      }, timeoutMs);

      this._pending.set(requestId, { resolve, reject, timer });
      this.ws.send(JSON.stringify(message));
    });
  }

  async spawn(prefabKey, options = {}) {
    const payload = { prefab_key: prefabKey };
    if (options.position) payload.position = options.position;
    if (options.rotation) payload.rotation = options.rotation;
    if (options.scale) payload.scale = options.scale;
    if (options.name) payload.name = options.name;
    if (options.parentUuid) payload.parent_uuid = options.parentUuid;
    return this.sendCommand('spawn', payload);
  }

  async transform(uuid, options = {}) {
    const payload = { uuid };
    if (options.position) payload.position = options.position;
    if (options.rotation) payload.rotation = options.rotation;
    if (options.scale) payload.scale = options.scale;
    if (options.local !== undefined) payload.local = options.local;
    return this.sendCommand('transform', payload);
  }

  async delete(uuid, includeChildren = true) {
    return this.sendCommand('delete', { uuid, include_children: includeChildren });
  }

  async rename(uuid, name) {
    return this.sendCommand('rename', { uuid, name });
  }

  async setParent(uuid, parentUuid, worldPositionStays = true) {
    return this.sendCommand('set_parent', {
      uuid,
      parent_uuid: parentUuid,
      world_position_stays: worldPositionStays,
    });
  }

  async setActive(uuid, active) {
    return this.sendCommand('set_active', { uuid, active });
  }

  async requestSceneDump(includeInactive = false) {
    return this.sendCommand('scene_dump', { include_inactive: includeInactive });
  }

  async ping() {
    return this.sendCommand('ping', {});
  }

  // ── Scene Access ────────────────────────────────────────

  get scene() {
    return new Map(this._scene);
  }

  getObject(uuid) {
    return this._scene.get(uuid) || null;
  }

  get objects() {
    return [...this._scene.values()];
  }
}

// ── Usage Example ───────────────────────────────────────────

async function main() {
  const client = new LiveLinkClient({ host: 'localhost', port: 8080 });

  client.on('connected', () => console.log('Ready'));
  client.on('scene_dump', (msg) => {
    console.log(`Scene: ${msg.payload.object_count} objects`);
  });
  client.on('sync', (msg) => {
    if (msg.objects?.length) console.log(`Sync: ${msg.objects.length} changed`);
  });

  client.connect();

  // Wait for connection
  await new Promise((r) => client.on('connected', r));

  // Spawn a cube
  const resp = await client.spawn('Cube', {
    position: [0, 2, 0],
    name: 'Node Cube',
  });

  if (resp.success) {
    const cubeUuid = resp.data.uuid;
    console.log(`Spawned: ${cubeUuid}`);

    await client.transform(cubeUuid, { position: [5, 1, 0] });
    console.log('Moved cube');

    await client.delete(cubeUuid);
    console.log('Deleted cube');
  }

  const pong = await client.ping();
  console.log(`Ping: ${pong.message}`);

  client.disconnect();
}

main().catch(console.error);
```

---

## 8. Error Handling & Reconnection

### 8.1 Error Scenarios

| Scenario | Detection | Recovery |
|----------|-----------|----------|
| **Connection refused** | `ECONNREFUSED` on connect | Retry with backoff |
| **Connection dropped** | `close` event / `read` returns 0 | Reconnect |
| **Invalid JSON** | `JSON.parse` throws | Log and skip message |
| **Unknown command type** | Server sends error response | Check `response.success` |
| **Object not found** | Server sends error response | Check UUID validity |
| **Prefab not registered** | Server sends error response | Check `list_spawnable_objects` |
| **Server shutdown** | Close frame received | Reconnect if `reconnect: true` |

### 8.2 Reconnection Strategy

**Recommended: Exponential Backoff with Jitter**

```
delay = min(initial_delay * 2^attempt + random(0, 1000), max_delay)
```

| Attempt | Delay (base) | With jitter (approx) |
|---------|-------------|---------------------|
| 0 | 1s | 1–2s |
| 1 | 2s | 2–3s |
| 2 | 4s | 4–5s |
| 3 | 8s | 8–9s |
| 4 | 16s | 16–17s |
| 5+ | 30s (cap) | 30–31s |

### 8.3 Response Error Handling

Always check the `success` field on responses:

```python
resp = await client.spawn("NonExistentPrefab", position=[0, 0, 0])
if not resp["success"]:
    print(f"Spawn failed: {resp['message']}")
    # "message" will contain the error reason, e.g., "Prefab not found: NonExistentPrefab"
```

### 8.4 Connection Health Monitoring

Use application-level `ping` to detect stale connections:

```python
import asyncio

async def health_monitor(client, interval=30):
    """Periodically ping the server to verify connection health."""
    while True:
        await asyncio.sleep(interval)
        try:
            resp = await asyncio.wait_for(client.ping(), timeout=5.0)
            if resp["message"] != "pong":
                logger.warning(f"Unexpected ping response: {resp}")
        except asyncio.TimeoutError:
            logger.warning("Ping timed out — connection may be stale")
        except Exception as e:
            logger.warning(f"Ping failed: {e}")
```

---

## 9. Design Issues

The following issues were identified during code review of `LiveLinkServer.cs` and `PacketSchemas.cs`.

### 9.1 Security

| # | Issue | Severity | Location |
|---|-------|----------|----------|
| S1 | **No authentication.** Any client that can reach the port can connect and issue commands (spawn, delete, transform). No token, API key, or origin check. | **Critical** | `LiveLinkServer.cs` |
| S2 | **No TLS/WSS support.** All traffic is plaintext, vulnerable to eavesdropping and MITM on non-localhost deployments. | **High** | `LiveLinkServer.cs` |
| S3 | **No origin validation.** The WebSocket handshake does not check `Origin` header, enabling CSWSH (Cross-Site WebSocket Hijacking) if the server is exposed to the web. | **High** | `LiveLinkServer.cs:PerformHandshakeAsync` |
| S4 | **No rate limiting.** A single client can flood the server with commands, potentially causing frame drops or Unity main thread starvation. | **Medium** | `LiveLinkServer.cs` |

### 9.2 Protocol Robustness

| # | Issue | Severity | Location |
|---|-------|----------|----------|
| P1 | **No WebSocket ping/pong at the protocol level.** The server does not send or respond to WebSocket-level ping frames (opcode `0x9`/`0xA`). This means network-level connection staleness is only detectable by TCP timeout, which can take minutes. The application-level `ping` type is not a substitute. | **High** | `LiveLinkServer.cs:ReadLoopAsync` |
| P2 | **No message size limit.** A malicious or buggy client can send an arbitrarily large frame, causing out-of-memory. The `payloadLen` can be up to 2^63 bytes. | **High** | `LiveLinkServer.cs:ReadLoopAsync` |
| P3 | **No frame fragmentation handling.** The code reads the FIN bit but does not accumulate continuation frames. A fragmented message will be processed as a partial message on the first frame, then the remaining frames will be misinterpreted. | **Medium** | `LiveLinkServer.cs:ReadLoopAsync` |
| P4 | **No per-message compression.** `permessage-deflate` (RFC 7692) is not negotiated. Large scene dumps can be several MB of JSON. | **Low** | `LiveLinkServer.cs:PerformHandshakeAsync` |
| P5 | **Silent exception swallowing in BroadcastAsync.** Send failures are caught with empty `catch { }`, making it impossible to detect disconnected clients or diagnose issues. | **Medium** | `LiveLinkServer.cs:BroadcastAsync` |

### 9.3 Architecture

| # | Issue | Severity | Location |
|---|-------|----------|----------|
| A1 | **Mixed concerns.** `LiveLinkServer.cs` contains WebSocket framing (RFC 6455), connection management, handshake logic, and message routing in a single 300+ line class. This makes testing and reuse difficult. | **Medium** | `LiveLinkServer.cs` |
| A2 | **No protocol versioning.** There is no version field in messages or during the handshake. Breaking changes will silently break existing clients. | **Medium** | `PacketSchemas.cs`, `LiveLinkServer.cs` |
| A3 | **OnMessageReceived fires on background thread.** Event handlers run on the TCP reader thread, not the Unity main thread. Consumers must be thread-safe or dispatch to main thread manually. While `MainThreadDispatcher` exists, the event itself doesn't enforce this. | **Medium** | `LiveLinkServer.cs:ReadLoopAsync` |
| A4 | **No backpressure.** If a client reads slowly, the server's send buffer grows unboundedly. `BroadcastAsync` does not check or limit queue depth. | **Low** | `LiveLinkServer.cs:BroadcastAsync` |
| A5 | **Guid-based UUIDs are not sortable.** `Guid.NewGuid().ToString("N")` produces random IDs with no temporal ordering, making debugging harder. Consider ULID or KSUID. | **Low** | `WebSocketConnection` constructor |

### 9.4 Data Model

| # | Issue | Severity | Location |
|---|-------|----------|----------|
| D1 | **`response.data` is `JObject` (untyped).** Each command returns different data shapes, but the schema is not documented or typed. Consumers must know the expected shape per command. | **Medium** | `PacketSchemas.cs:ResponsePacket` |
| D2 | **`transform` payload is fully optional.** All of `position`, `rotation`, `scale` can be null/omitted, but the server behavior for "no change" is not documented. Does it leave the value unchanged, or set it to zero? | **Low** | `PacketSchemas.cs:TransformPayload` |
| D3 | **Rotation convention not documented.** The quaternion order `[x, y, z, w]` must be assumed from the code. Unity uses `[x, y, z, w]` internally, but many libraries default to `[w, x, y, z]`. | **Low** | `PacketSchemas.cs:TransformDTO` |
| D4 | **`heartbeat` packet is defined but heartbeat sending is not visible in `LiveLinkServer.cs`.** It's unclear whether heartbeats are actually sent or if the type is dead code. | **Low** | `PacketSchemas.cs:HeartbeatPacket` |
| D5 | **`ObjectSpawnedPacket` and `ObjectDestroyedPacket` are defined but their broadcast behavior is not documented.** It's unclear if all clients receive these or only the ones that didn't send the command. | **Low** | `PacketSchemas.cs` |

### 9.5 Reliability

| # | Issue | Severity | Location |
|---|-------|----------|----------|
| R1 | **No command timeout on server.** If a command handler hangs (e.g., waiting for main thread), the client's response future will never resolve. | **Medium** | `LiveLinkServer.cs` |
| R2 | **`ReadExactlyAsync` returns partial count without error.** If the stream closes mid-read, the function returns a short count. The caller checks `read < 2` for the header, but payload reads use `(int)payloadLen` which may silently produce a truncated message. | **Medium** | `LiveLinkServer.cs:ReadExactlyAsync` |
| R3 | **No graceful shutdown.** `StopServer` disposes all connections immediately without sending close frames to clients. | **Low** | `LiveLinkServer.cs:StopServer` |

---

## 10. Refactoring Suggestions

### 10.1 Extract WebSocket Framing Layer

Separate RFC 6455 framing into its own class (`WebSocketFramer`) that handles:
- Frame encoding/decoding
- Masking/unmasking
- Ping/pong at the protocol level
- Fragmentation/reassembly
- Close handshake

```csharp
public class WebSocketFramer
{
    public async Task<WebSocketFrame> ReadFrameAsync(Stream stream, CancellationToken ct);
    public Task WriteFrameAsync(Stream stream, WebSocketFrame frame, CancellationToken ct);
    public Task SendPingAsync(Stream stream, byte[] payload, CancellationToken ct);
    public Task SendCloseAsync(Stream stream, CloseStatus status, CancellationToken ct);
}
```

### 10.2 Add Authentication Middleware

Implement a simple token-based auth during the handshake:

```csharp
// Client sends token as query parameter or header
ws://localhost:8080?token=abc123

// Or in the upgrade request headers:
// Authorization: Bearer abc123

public class AuthConfig
{
    public string Token { get; set; }
    public List<string> AllowedOrigins { get; set; }
}
```

### 10.3 Add Protocol Versioning

Include a version in the initial handshake or first message:

```json
// Server → Client (first message after scene_dump, or as part of it)
{
  "type": "handshake",
  "protocol_version": "1.0.0",
  "server_version": "0.5.0",
  "capabilities": ["delta_sync", "spawn", "transform", "delete", "rename", "set_parent", "set_active", "gltf"]
}
```

### 10.4 Typed Response Data

Replace `JObject Data` with discriminated types:

```csharp
[JsonProperty("data")]
public ResponseData Data { get; set; }

[JsonConverter(typeof(ResponseDataConverter))]
public class ResponseData
{
    [JsonProperty("uuid")] public string UUID { get; set; }
    [JsonProperty("name")] public string Name { get; set; }
    // Extend as needed
}
```

Or use a generic approach:

```csharp
public class ResponsePacket<T> : BasePacket where T : class
{
    [JsonProperty("data")] public T Data { get; set; }
}
```

### 10.5 Add Message Size Limits

```csharp
private const long MaxPayloadBytes = 10 * 1024 * 1024; // 10 MB

// In ReadLoopAsync:
if (payloadLen > MaxPayloadBytes)
{
    await SendCloseAsync(connection, CloseStatus.MessageTooBig, "Payload exceeds limit");
    break;
}
```

### 10.6 Implement WebSocket-Level Ping/Pong

```csharp
// In ReadLoopAsync, handle opcode 0x9 (Ping):
case 0x9: // Ping
    await SendFrameAsync(stream, payload, 0xA); // Pong with same payload
    break;

case 0xA: // Pong
    // Update last-pong timestamp for connection health tracking
    connection.LastPongReceived = DateTime.UtcNow;
    break;
```

### 10.7 Add Rate Limiting

```csharp
public class RateLimiter
{
    private readonly int _maxMessagesPerSecond;
    private readonly SemaphoreSlim _semaphore;

    public async Task<bool> TryAcquire(string clientId)
    {
        // Token bucket or sliding window per client
    }
}
```

### 10.8 Structured Logging

Replace `Debug.Log` with structured logging that includes connection ID, message type, and timing:

```csharp
public interface ILiveLinkLogger
{
    void ClientConnected(string connectionId, string remoteAddress);
    void ClientDisconnected(string connectionId, int remainingClients);
    void MessageReceived(string connectionId, string messageType, int payloadSize);
    void CommandProcessed(string connectionId, string commandType, string requestId, bool success, TimeSpan duration);
    void Error(string connectionId, Exception ex, string context);
}
```

### 10.9 Graceful Shutdown

```csharp
public async Task StopServerAsync()
{
    _isRunning = false;
    _cancellationTokenSource?.Cancel();

    // Send close frames to all clients
    var closeTasks = new List<Task>();
    lock (_clientsLock)
    {
        foreach (var client in _connectedClients)
        {
            closeTasks.Add(client.CloseAsync());
        }
    }
    await Task.WhenAll(closeTasks);

    lock (_clientsLock) _connectedClients.Clear();
    _listener?.Stop();
}
```

### 10.10 Separate Concerns into Distinct Classes

```
LiveLink/
├── Network/
│   ├── WebSocketFramer.cs       # RFC 6455 frame encoding/decoding
│   ├── WebSocketConnection.cs   # Single connection lifecycle
│   ├── WebSocketServer.cs       # TCP listener + connection pool
│   ├── AuthMiddleware.cs        # Authentication
│   └── RateLimiter.cs           # Per-client rate limiting
├── Protocol/
│   ├── PacketSchemas.cs         # DTOs (existing)
│   ├── PacketSerializer.cs      # Serialization (extract from PacketSchemas)
│   └── ProtocolVersion.cs       # Version constants + negotiation
└── Handlers/
    ├── ICommandHandler.cs        # Interface
    ├── SpawnHandler.cs
    ├── TransformHandler.cs
    ├── DeleteHandler.cs
    └── ...
```

---

## Appendix A: Data Type Reference

### TransformDTO

| Field | JSON Key | Type | Description |
|-------|----------|------|-------------|
| Position | `pos` | `float[3]` | World or local position `[x, y, z]` |
| Rotation | `rot` | `float[4]` | Quaternion `[x, y, z, w]` (Unity convention) |
| Scale | `scale` | `float[3]` | Local scale `[x, y, z]` |

### SceneObjectDTO

| Field | JSON Key | Type | Description |
|-------|----------|------|-------------|
| UUID | `uuid` | `string` | Unique identifier (32 hex chars, no dashes) |
| ParentUUID | `parent_uuid` | `string?` | Parent object UUID, `null` for scene root |
| Name | `name` | `string` | GameObject name |
| Transform | `transform` | `TransformDTO` | Transform data |
| Active | `active` | `boolean` | Whether the GameObject is active |
| Layer | `layer` | `int` | Unity layer index |
| Tag | `tag` | `string` | Unity tag |
| Children | `children` | `string[]` | Child object UUIDs |

### Quaternion Convention

LiveLink uses Unity's internal quaternion order: **`[x, y, z, w]`**.

This differs from some math libraries that use `[w, x, y, z]`. When integrating with physics engines or math libraries, you may need to reorder:

```python
# Unity [x, y, z, w] → scipy [w, x, y, z]
unity_quat = [0.0, 0.707, 0.0, 0.707]
scipy_quat = [unity_quat[3], unity_quat[0], unity_quat[1], unity_quat[2]]
# → [0.707, 0.0, 0.707, 0.0]
```

---

## Appendix B: Quick Reference Card

```
┌─────────────────────────────────────────────────────────────┐
│                    LiveLink WebSocket API                    │
│                     Quick Reference Card                     │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│  CONNECT:     ws://localhost:8080                            │
│  PROTOCOL:    JSON over WebSocket (RFC 6455)                │
│  AUTH:        None                                          │
│                                                             │
│  ── Server → Client (automatic) ──                          │
│  scene_dump   Full scene hierarchy (on connect)             │
│  sync         Delta update (periodic)                       │
│  heartbeat    Keep-alive with client count                  │
│  response     Command result                                │
│  object_spawned   Spawn notification                        │
│  object_destroyed Delete notification                       │
│                                                             │
│  ── Client → Server (commands) ──                           │
│  scene_dump   Request full dump                             │
│  spawn        Create object from prefab                     │
│  transform    Update position/rotation/scale                │
│  delete       Remove object                                 │
│  rename       Change object name                            │
│  set_parent   Reparent object                               │
│  set_active   Enable/disable object                         │
│  ping         Health check → "pong"                         │
│                                                             │
│  ── Response format ──                                      │
│  { type, success, message, request_id, data }               │
│                                                             │
│  ── Quaternion order ──                                     │
│  [x, y, z, w]  (Unity convention)                          │
│                                                             │
└─────────────────────────────────────────────────────────────┘
```
