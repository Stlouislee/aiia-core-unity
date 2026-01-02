#!/usr/bin/env python3
"""
Unity LiveLink - MCP Protocol Test Client

This script tests the MCP (Model Context Protocol) implementation in Unity LiveLink.
"""

import asyncio
import json
import sys

try:
    import websockets
except ImportError:
    print("Please install websockets: pip install websockets")
    sys.exit(1)

async def test_mcp(uri: str = "ws://localhost:8080"):
    print(f"Connecting to {uri}...")
    try:
        async with websockets.connect(uri) as websocket:
            print("Connected!")

            # 1. List Tools
            print("\n--- Testing tools/list ---")
            list_tools_request = {
                "jsonrpc": "2.0",
                "id": 1,
                "method": "tools/list",
                "params": {}
            }
            await websocket.send(json.dumps(list_tools_request))
            response = await websocket.recv()
            print("Response:", json.dumps(json.loads(response), indent=2))

            # 2. List Resources
            print("\n--- Testing resources/list ---")
            list_resources_request = {
                "jsonrpc": "2.0",
                "id": 2,
                "method": "resources/list",
                "params": {}
            }
            await websocket.send(json.dumps(list_resources_request))
            response = await websocket.recv()
            print("Response:", json.dumps(json.loads(response), indent=2))

            # 3. Call Tool (Spawn)
            print("\n--- Testing tools/call (spawn_object) ---")
            spawn_request = {
                "jsonrpc": "2.0",
                "id": 3,
                "method": "tools/call",
                "params": {
                    "name": "spawn_object",
                    "arguments": {
                        "prefab_key": "Cube",
                        "position": [0, 2, 0],
                        "name": "MCP_Spawned_Cube"
                    }
                }
            }
            await websocket.send(json.dumps(spawn_request))
            response = await websocket.recv()
            spawn_response = json.loads(response)
            print("Response:", json.dumps(spawn_response, indent=2))

            if "result" in spawn_response:
                uuid = spawn_response["result"].get("data", {}).get("uuid")
                if uuid:
                    # 4. Read Resource
                    print(f"\n--- Testing resources/read (for {uuid}) ---")
                    read_resource_request = {
                        "jsonrpc": "2.0",
                        "id": 4,
                        "method": "resources/read",
                        "params": {
                            "uri": f"mcp://unity/scenes/MainScene/objects/{uuid}"
                        }
                    }
                    await websocket.send(json.dumps(read_resource_request))
                    response = await websocket.recv()
                    print("Response:", json.dumps(json.loads(response), indent=2))

                    # 5. Call Tool (Transform)
                    print(f"\n--- Testing tools/call (transform_object for {uuid}) ---")
                    transform_request = {
                        "jsonrpc": "2.0",
                        "id": 5,
                        "method": "tools/call",
                        "params": {
                            "name": "transform_object",
                            "arguments": {
                                "uuid": uuid,
                                "position": [0, 5, 0]
                            }
                        }
                    }
                    await websocket.send(json.dumps(transform_request))
                    response = await websocket.recv()
                    print("Response:", json.dumps(json.loads(response), indent=2))

    except Exception as e:
        print(f"Error: {e}")

if __name__ == "__main__":
    asyncio.run(test_mcp())
