# Unity LiveLink

[![Unity 2020.3+](https://img.shields.io/badge/Unity-2020.3%2B-blue.svg)](https://unity.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

A lightweight Unity package that establishes a bidirectional WebSocket bridge between a running Unity scene and external applications (Python, Node.js, Web Dashboards).

## Features

- 🔌 **Drop-and-Play**: Add a single `LiveLinkManager` component to your scene
- 🎯 **Configurable Scope**: Sync the entire hierarchy or just a specific branch
- 📡 **Bidirectional Communication**: Read scene state and send commands from external apps
- ⚡ **Delta Sync**: Efficient updates that only transmit changed objects
- 🔧 **Prefab Spawning**: Spawn registered prefabs from external commands
- 🖥️ **Custom Editor**: Easy-to-use inspector with status display and controls

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
| **Auto Start** | Start server automatically on Play |
| **Scope** | `WholeScene` or `TargetObjectOnly` |
| **Target Root** | Root object when using TargetObjectOnly scope |
| **Sync Frequency** | Updates per second (0 = manual only) |
| **Delta Sync** | Only send changed objects |
| **Spawnable Prefabs** | Prefabs that can be instantiated via commands |

### 3. Enter Play Mode

The server will start automatically (if Auto Start is enabled) and begin accepting WebSocket connections.

## Communication Protocol

All communication uses JSON over WebSocket. Connect to `ws://localhost:8080/` (or your configured port).

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

## License

MIT License - see [LICENSE](LICENSE) for details.

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.