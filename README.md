# Aroaro LiveLink for Unity

A lightweight Unity package that establishes a bidirectional WebSocket bridge between a running Unity scene and external applications (Python, Node.js, Web Dashboards). Now also speaks MCP (Model Context Protocol) via JSON-RPC so LLM clients can list/read scene resources and call Unity tools.

## Features

- 🔌 **Drop-and-Play**: Add a single `LiveLinkManager` component to your scene
- 🎯 **Configurable Scope**: Sync the entire hierarchy or just a specific branch
- 📡 **Bidirectional Communication**: Read scene state and send commands from external apps
- ⚡ **Delta Sync**: Efficient updates that only transmit changed objects
- 🔧 **Prefab Spawning**: Spawn registered prefabs from external commands
- 🖥️ **Custom Editor**: Easy-to-use inspector with status display and controls
- 🤝 **MCP Support**: JSON-RPC endpoint exposing scene resources and Unity tools

## Installation

### Via Unity Package Manager (Git URL)

1. Open Unity and go to **Window > Package Manager**
2. Click the **+** button in the top-left corner
3. Select **Add package from git URL...**
4. Enter the following URL (including `.git` at the end):

```
https://github.com/Stlouislee/aiia-core-unity.git
```

5. Click **Add** and wait for the package to download and import

### Manual Installation

1. Clone or download this repository
2. Copy the contents into your project's `Packages/com.livelink.core/` folder

## Quick Start

### 1. Add LiveLink Manager to Your Scene

- Go to **LiveLink > Create Manager** in the Unity menu
- Or create an empty GameObject and add the `LiveLinkManager` component

### 2. Configure the Manager

| Property | Description |
|----------|-------------|
| **Port** | WebSocket server port (default: 8080) |
| **MCP Port** | MCP HTTP server port (default: 8081) |
| **Enable MCP Server** | Enable HTTP + SSE transport for MCP protocol |
| **Auto Start** | Start server automatically on Play |
| **Scope** | `WholeScene` or `TargetObjectOnly` |
| **Target Root** | Root object when using TargetObjectOnly scope |
| **Sync Frequency** | Updates per second (0 = manual only) |
| **Delta Sync** | Only send changed objects |
| **Spawnable Prefabs** | Prefabs that can be instantiated via commands |

### 3. Enter Play Mode

The server will start automatically (if Auto Start is enabled) and begin accepting WebSocket connections.

## Communication Protocol

Unity LiveLink provides two transport mechanisms for different use cases:

### Transport Comparison

| Feature | **WebSocket** (Port 8080) | **HTTP+SSE** (Port 8081) |
|---------|--------------------------|--------------------------|
| **Protocol** | Custom JSON | MCP (Model Context Protocol) |
| **Session Management** | Implicit (connection-based) | Explicit (sessionId required) |
| **Authentication** | None | Session-based validation |
| **Bidirectional** | Yes (full duplex) | Request/Response + SSE events |
| **Auto-reconnect** | Client handles | Client must re-establish session |
| **Use Case** | Simple scripting, real-time sync | LLM agents, MCP-compatible tools |
| **Initialization** | Automatic scene dump on connect | Explicit `initialize` handshake |

**Choose WebSocket when:**
- Building simple automation scripts
- Need low-latency bidirectional communication
- Don't require MCP standard compliance

**Choose HTTP+SSE when:**
- Integrating with MCP-compatible LLM clients (Claude Desktop, etc.)
- Need explicit session lifecycle control
- Require RESTful request/response pattern

---

### WebSocket Transport (Port 8080)

All communication uses JSON over WebSocket. Connect to `ws://localhost:8080/` (or your configured port).

### MCP (Model Context Protocol) - HTTP+SSE Transport (Port 8081)

Unity LiveLink implements the official MCP HTTP+SSE transport specification with full session management.
The MCP server listens on all interfaces. Use `localhost` when the client runs on the same machine, or the device IP / port forwarding when Unity is running on Android or Quest.

#### Session Workflow

1. **Connect to SSE endpoint** (`GET /sse`)
   - Server creates a session and sends an `endpoint` event with `sessionId`
   - Keep the SSE connection alive to maintain the session
   
2. **Send POST requests** to the endpoint URL (`POST /mcp?sessionId={sessionId}`)
   - Include the `sessionId` query parameter in all requests
   - First request must be `initialize` to authenticate the session
   - Session expires after 5 minutes of inactivity
   
3. **Session Validation**
   - `initialize`: Creates session state, no prior session required
   - Other methods: Require valid, initialized session
   - Error codes: `-32001` (session required), `-32002` (not initialized)

#### Available Methods

- `initialize` → Handshake with server capabilities
- `resources/list` → Returns MCP resource templates for reading Unity scene data
- `resources/read` → Returns content for a resource URI (see Resource URIs below)
- `tools/list` → Returns available tools
- `tools/call` → Invokes a tool (`spawn_object`, `transform_object`, `delete_object`, etc.)
- `prompts/list` → Returns reusable MCP workflow prompts exposed by the server
- `prompts/get` → Returns a rendered prompt template with optional arguments

### MCP Resource URIs

The server exposes Unity scene data through the `unity://` URI scheme:

| URI | Description |
|-----|-------------|
| `unity://scene/active` | Basic scene info (name, path, root count, render pipeline, time, quality, platform, Unity version) |
| `unity://scene/hierarchy?root=/&depth=2` | Hierarchy tree with configurable root path and depth |
| `unity://go/{instanceId}` | GameObject metadata (name, tag, layer, active state, parent, children, component count, full transform) |
| `unity://go/{instanceId}/components` | Component list with types, instance IDs, and enabled states |
| `unity://component/{instanceId}/{componentType}` | Component field snapshot (all public fields and properties) |
| `unity://selection` | Currently selected objects in the Unity Editor |
| `unity://events/recent?count=50` | Recent scene events (create, delete, transform change, parent change, etc.) |

#### Resource Examples

**Read active scene info:**
```json
{"jsonrpc":"2.0","id":2,"method":"resources/read","params":{"uri":"unity://scene/active"}}
```

Response:
```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "result": {
    "contents": [{
      "uri": "unity://scene/active",
      "mimeType": "application/json",
      "text": "{\"scene_name\":\"SampleScene\",\"root_count\":5,\"object_count\":42,\"render_pipeline\":\"URP\",\"time_scale\":1.0,\"unity_version\":\"2022.3.10f1\"}"
    }]
  }
}
```

**Read scene hierarchy (depth 3):**
```json
{"jsonrpc":"2.0","id":3,"method":"resources/read","params":{"uri":"unity://scene/hierarchy?depth=3"}}
```

**Read a specific GameObject:**
```json
{"jsonrpc":"2.0","id":4,"method":"resources/read","params":{"uri":"unity://go/12345"}}
```

**Read components on a GameObject:**
```json
{"jsonrpc":"2.0","id":5,"method":"resources/read","params":{"uri":"unity://go/12345/components"}}
```

**Read a specific component's fields:**
```json
{"jsonrpc":"2.0","id":6,"method":"resources/read","params":{"uri":"unity://component/12345/MeshRenderer"}}
```

**Read current editor selection:**
```json
{"jsonrpc":"2.0","id":7,"method":"resources/read","params":{"uri":"unity://selection"}}
```

**Read recent events:**
```json
{"jsonrpc":"2.0","id":8,"method":"resources/read","params":{"uri":"unity://events/recent?count=20"}}
```

#### Example Session Flow

```json
**Step 1: Connect to SSE**
```
GET http://localhost:8081/sse
```
Server responds with:
```
event: endpoint
data: /mcp?sessionId=a1b2c3d4...
```

**Step 2: Initialize session**
```json
POST http://localhost:8081/mcp?sessionId=a1b2c3d4...

{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "initialize",
  "params": {
    "protocolVersion": "2024-11-05",
    "capabilities": {},
    "clientInfo": {
      "name": "My MCP Client",
      "version": "1.0.0"
    }
  }
}
```

**Step 3: Use MCP methods**
```json
POST http://localhost:8081/mcp?sessionId=a1b2c3d4...

{"jsonrpc":"2.0","id":2,"method":"resources/read","params":{"uri":"unity://scene/active"}}
```

Response:
```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "result": {
    "contents": [
      {
        "uri": "unity://scene/active",
        "mimeType": "application/json",
        "text": "{\"scene_name\":\"SampleScene\",\"root_count\":3,\"object_count\":15,\"render_pipeline\":\"Built-in\",\"time_scale\":1.0}"
      }
    ]
  }
}
```

**Example: Call a tool**

```json
POST http://localhost:8081/mcp?sessionId=a1b2c3d4...

{
  "jsonrpc": "2.0",
  "id": 3,
  "method": "tools/call",
  "params": {
    "name": "spawn_object",
    "arguments": {
      "prefab_key": "Cube",
      "position": [0, 1, 0],
      "name": "MCP Cube"
    }
  }
}
```

**Example: Get a prompt**

```json
POST http://localhost:8081/mcp?sessionId=a1b2c3d4...

{
  "jsonrpc": "2.0",
  "id": 4,
  "method": "prompts/get",
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

**Session Errors**

- **-32001**: Session required - Connect to `/sse` first to obtain a `sessionId`
- **-32002**: Session not initialized - Send `initialize` method before other operations
- Session expires after 5 minutes of inactivity

See `mcp-config.example.json` for a ready-to-copy client config.

### Available MCP Prompts

- `scene_analysis` - Analyze hierarchy hotspots and propose concrete MCP tool actions.
- `spawn_from_intent` - Turn natural-language level design intent into spawn/transform operations.
- `object_repair` - Diagnose and repair transform/parenting issues for a target UUID.
- `scene_cleanup` - Produce (and optionally execute) a safe cleanup plan for redundant objects.

### Available MCP Tools

#### Core Scene Management

**`spawn_object`** - Spawn a new object from a prefab
```json
{
  "name": "spawn_object",
  "arguments": {
    "prefab_key": "Cube",
    "position": [0, 1, 0],
    "rotation": [0, 0, 0, 1],
    "scale": [1, 1, 1],
    "name": "My Cube",
    "parent_uuid": "parent-uuid"
  }
}
```

**`spawn_gltf`** - Spawn a glTF asset at runtime (via Unity glTFast)

Load from URL:
```json
{
  "name": "spawn_gltf",
  "arguments": {
    "url": "https://example.com/model.glb",
    "position": [0, 1, 0],
    "rotation": [0, 0, 0, 1],
    "scale": [1, 1, 1],
    "name": "Imported glTF"
  }
}
```

Load from base64-encoded `.glb`:
```json
{
  "name": "spawn_gltf",
  "arguments": {
    "data_base64": "<base64 glb bytes>",
    "source_uri": "file:///memory.glb",
    "name": "Imported glTF"
  }
}
```

**`transform_object`** - Update position, rotation, or scale
```json
{
  "name": "transform_object",
  "arguments": {
    "uuid": "object-uuid",
    "position": [10, 0, 10],
    "rotation": [0, 0.5, 0, 0.866],
    "scale": [2, 2, 2]
  }
}
```

**`delete_object`** - Delete an object from the scene
```json
{
  "name": "delete_object",
  "arguments": {
    "uuid": "object-uuid"
  }
}
```

**`scene_dump`** - Get full scene hierarchy
```json
{
  "name": "scene_dump",
  "arguments": {
    "include_inactive": false
  }
}
```

#### Context and Discovery

**`list_spawnable_objects`** - Get available prefab names
```json
{
  "name": "list_spawnable_objects",
  "arguments": {}
}
```
**Response:** `{"prefabs": ["Cube", "Sphere"], "count": 2}`

**`get_view_context`** - Get camera/player perspective
```json
{
  "name": "get_view_context",
  "arguments": {
    "camera_tag": "MainCamera",
    "include_visible_objects": true,
    "raycast_distance": 100
  }
}
```
**Returns:** Camera position, orientation, forward/right/up vectors, field of view, raycast hit info, and optionally visible objects.

#### Planned Tools

**`get_snapshot`** - Capture camera view as image (planned)
- **Arguments:** `width`, `height`, `quality`, `format`, `camera_tag`
- **Returns:** Base64-encoded image with metadata
- **Cross-platform:** Windows, macOS, Linux, iOS, Android, VR, WebGL

---

### Receiving Scene Data

When a client connects, it receives a full scene dump:

```json
{
  "type": "scene_dump",
  "timestamp": 1702234567890,
  "payload": {
    "root_id": "scene_root",
    "scene_name": "SampleScene",
    "object_count": 5,
    "objects": [
      {
        "uuid": "abc123def456",
        "parent_uuid": null,
        "name": "Player",
        "active": true,
        "layer": 0,
        "tag": "Player",
        "transform": {
          "pos": [0, 1, 0],
          "rot": [0, 0, 0, 1],
          "scale": [1, 1, 1]
        },
        "children": ["child-uuid-1", "child-uuid-2"]
      }
    ]
  }
}
```

### Sync Updates

Periodic sync messages contain only changed objects:

```json
{
  "type": "sync",
  "timestamp": 1702234567900,
  "is_delta": true,
  "objects": [
    {
      "uuid": "abc123def456",
      "name": "Player",
      "transform": {
        "pos": [5, 1, 3],
        "rot": [0, 0.707, 0, 0.707],
        "scale": [1, 1, 1]
      }
    }
  ]
}
```

### Sending Commands

#### Spawn Object

```json
{
  "type": "spawn",
  "request_id": "req-001",
  "payload": {
    "prefab_key": "Cube",
    "id": "my-custom-id",
    "position": [5, 0, 5],
    "rotation": [0, 0, 0, 1],
    "scale": [2, 2, 2],
    "name": "My Cube"
  }
}
```

#### Transform Object

```json
{
  "type": "transform",
  "request_id": "req-002",
  "payload": {
    "uuid": "abc123def456",
    "position": [10, 0, 10],
    "rotation": [0, 0.5, 0, 0.866],
    "local": false
  }
}
```

#### Delete Object

```json
{
  "type": "delete",
  "request_id": "req-003",
  "payload": {
    "uuid": "abc123def456"
  }
}
```

#### Request Scene Dump

```json
{
  "type": "scene_dump",
  "request_id": "req-004",
  "payload": {
    "include_inactive": false
  }
}
```

#### Other Commands

- `rename` - Rename an object
- `set_parent` - Change object parent
- `set_active` - Enable/disable object
- `ping` - Health check (responds with "pong")

### Response Format

Commands receive a response:

```json
{
  "type": "response",
  "timestamp": 1702234567890,
  "success": true,
  "message": "Object spawned",
  "request_id": "req-001",
  "data": {
    "uuid": "new-object-uuid",
    "name": "My Cube"
  }
}
```

## Python Client Example

```python
import asyncio
import websockets
import json

async def connect_to_unity():
    uri = "ws://localhost:8080"
    
    async with websockets.connect(uri) as websocket:
        # Receive initial scene dump
        scene_data = await websocket.recv()
        scene = json.loads(scene_data)
        print(f"Connected! Scene has {scene['payload']['object_count']} objects")
        
        # Spawn a cube
        spawn_command = {
            "type": "spawn",
            "request_id": "py-001",
            "payload": {
                "prefab_key": "Cube",
                "position": [0, 2, 0],
                "name": "Python Cube"
            }
        }
        await websocket.send(json.dumps(spawn_command))
        
        # Wait for response
        response = await websocket.recv()
        print(f"Response: {response}")
        
        # Listen for sync updates
        while True:
            message = await websocket.recv()
            data = json.loads(message)
            if data["type"] == "sync":
                print(f"Sync: {len(data['objects'])} objects changed")

# Run the client
asyncio.run(connect_to_unity())
```

### Requirements

```bash
pip install websockets
```

## Node.js Client Example

```javascript
const WebSocket = require('ws');

const ws = new WebSocket('ws://localhost:8080');

ws.on('open', function() {
    console.log('Connected to Unity LiveLink');
    
    // Spawn a cube
    ws.send(JSON.stringify({
        type: 'spawn',
        request_id: 'js-001',
        payload: {
            prefab_key: 'Cube',
            position: [0, 3, 0],
            name: 'JavaScript Cube'
        }
    }));
});

ws.on('message', function(data) {
    const message = JSON.parse(data);
    console.log('Received:', message.type);
    
    if (message.type === 'scene_dump') {
        console.log(`Scene has ${message.payload.object_count} objects`);
    }
});

ws.on('close', function() {
    console.log('Disconnected from Unity');
});
```

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                     External Application                      │
│                  (Python / Node.js / Web)                    │
└──────────────────────────┬──────────────────────────────────┘
                           │ WebSocket (JSON)
                           ▼
┌─────────────────────────────────────────────────────────────┐
│                      LiveLinkServer                          │
│                   (Background Thread)                        │
└──────────────────────────┬──────────────────────────────────┘
                           │ ConcurrentQueue<Action>
                           ▼
┌─────────────────────────────────────────────────────────────┐
│                   MainThreadDispatcher                       │
│                    (Unity Main Thread)                       │
└──────────────────────────┬──────────────────────────────────┘
                           │
                           ▼
┌─────────────────────────────────────────────────────────────┐
│                    LiveLinkManager                           │
│          ┌─────────────┴─────────────┐                      │
│          ▼                           ▼                      │
│    SceneScanner              Command Handlers               │
│   (Read Hierarchy)      (Spawn/Transform/Delete)            │
└─────────────────────────────────────────────────────────────┘
```

## Folder Structure

```
com.livelink.core/
├── package.json                    # UPM manifest
├── README.md                       # This file
├── Runtime/
│   ├── LiveLink.asmdef            # Assembly definition
│   └── Scripts/
│       ├── LiveLinkManager.cs     # Main manager component
│       ├── MainThreadDispatcher.cs # Thread-safe dispatcher
│       ├── SceneScanner.cs        # Hierarchy serialization
│       ├── MCP/                   # MCP JSON-RPC support
│       │   ├── McpTypes.cs        # JSON-RPC + MCP DTOs
│       │   └── McpResourceMapper.cs # URI + content helpers
│       └── Network/
│           ├── LiveLinkServer.cs  # WebSocket server
│           └── PacketSchemas.cs   # JSON DTOs
└── Editor/
    ├── LiveLink.Editor.asmdef     # Editor assembly definition
    └── LiveLinkManagerEditor.cs   # Custom inspector
```

## Requirements

- Unity 2020.3 LTS or newer
- Newtonsoft.Json (automatically installed via dependencies)

## Roadmap

- [ ] Component reflection (send generic component data)
- [ ] WebRTC video streaming (GameView render texture)
- [ ] Editor scene control (not just Play Mode)
- [ ] Multiple scene support
- [ ] Custom event system
- [ ] Expanded MCP tools for component data

## License

MIT License - see [LICENSE](LICENSE) for details.

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.
