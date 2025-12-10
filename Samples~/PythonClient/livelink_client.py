#!/usr/bin/env python3
"""
Unity LiveLink - Python Test Client

This script demonstrates how to connect to Unity LiveLink and interact with the scene.

Requirements:
    pip install websockets

Usage:
    python livelink_client.py
"""

import asyncio
import json
import sys
from typing import Optional

try:
    import websockets
except ImportError:
    print("Please install websockets: pip install websockets")
    sys.exit(1)


class LiveLinkClient:
    """Client for Unity LiveLink WebSocket server."""
    
    def __init__(self, uri: str = "ws://localhost:8080"):
        self.uri = uri
        self.websocket: Optional[websockets.WebSocketClientProtocol] = None
        self.request_id = 0
        self.scene_objects = {}
    
    def _next_request_id(self) -> str:
        """Generate unique request ID."""
        self.request_id += 1
        return f"py-{self.request_id:04d}"
    
    async def connect(self):
        """Connect to Unity LiveLink server."""
        print(f"Connecting to {self.uri}...")
        self.websocket = await websockets.connect(self.uri)
        print("Connected!")
        
        # Receive initial scene dump
        response = await self.websocket.recv()
        data = json.loads(response)
        
        if data.get("type") == "scene_dump":
            self._process_scene_dump(data)
        
        return self
    
    async def disconnect(self):
        """Disconnect from server."""
        if self.websocket:
            await self.websocket.close()
            self.websocket = None
            print("Disconnected.")
    
    def _process_scene_dump(self, data: dict):
        """Process scene dump message."""
        payload = data.get("payload", {})
        objects = payload.get("objects", [])
        
        self.scene_objects.clear()
        for obj in objects:
            uuid = obj.get("uuid")
            if uuid:
                self.scene_objects[uuid] = obj
        
        print(f"Scene: {payload.get('scene_name', 'Unknown')}")
        print(f"Objects: {payload.get('object_count', 0)}")
    
    async def send_command(self, command_type: str, payload: dict) -> dict:
        """Send command and wait for response."""
        if not self.websocket:
            raise RuntimeError("Not connected")
        
        request_id = self._next_request_id()
        command = {
            "type": command_type,
            "request_id": request_id,
            "payload": payload
        }
        
        await self.websocket.send(json.dumps(command))
        
        # Wait for response
        while True:
            response = await self.websocket.recv()
            data = json.loads(response)
            
            if data.get("type") == "response" and data.get("request_id") == request_id:
                return data
            elif data.get("type") == "scene_dump":
                self._process_scene_dump(data)
            elif data.get("type") == "sync":
                self._process_sync(data)
            # Store other messages for later processing
    
    def _process_sync(self, data: dict):
        """Process sync message."""
        objects = data.get("objects", [])
        for obj in objects:
            uuid = obj.get("uuid")
            if uuid:
                self.scene_objects[uuid] = obj
    
    async def spawn(self, prefab_key: str, position: list = None, 
                   rotation: list = None, name: str = None) -> dict:
        """Spawn a prefab in the scene."""
        payload = {"prefab_key": prefab_key}
        
        if position:
            payload["position"] = position
        if rotation:
            payload["rotation"] = rotation
        if name:
            payload["name"] = name
        
        return await self.send_command("spawn", payload)
    
    async def transform(self, uuid: str, position: list = None,
                       rotation: list = None, scale: list = None) -> dict:
        """Transform an object."""
        payload = {"uuid": uuid}
        
        if position:
            payload["position"] = position
        if rotation:
            payload["rotation"] = rotation
        if scale:
            payload["scale"] = scale
        
        return await self.send_command("transform", payload)
    
    async def delete(self, uuid: str) -> dict:
        """Delete an object."""
        return await self.send_command("delete", {"uuid": uuid})
    
    async def ping(self) -> dict:
        """Send ping to test connection."""
        return await self.send_command("ping", {})
    
    async def request_scene_dump(self) -> dict:
        """Request fresh scene dump."""
        return await self.send_command("scene_dump", {})
    
    def list_objects(self):
        """Print all known objects."""
        print(f"\n--- Scene Objects ({len(self.scene_objects)}) ---")
        for uuid, obj in self.scene_objects.items():
            pos = obj.get("transform", {}).get("pos", [0, 0, 0])
            print(f"  [{uuid}] {obj.get('name', 'Unknown')} @ ({pos[0]:.1f}, {pos[1]:.1f}, {pos[2]:.1f})")
        print()


async def interactive_demo():
    """Interactive demonstration of LiveLink client."""
    client = LiveLinkClient()
    
    try:
        await client.connect()
        
        # List initial scene
        client.list_objects()
        
        # Interactive loop
        print("\nCommands:")
        print("  list    - List scene objects")
        print("  spawn   - Spawn a Cube")
        print("  ping    - Test connection")
        print("  refresh - Request scene dump")
        print("  quit    - Exit")
        print()
        
        while True:
            try:
                cmd = input("> ").strip().lower()
            except EOFError:
                break
            
            if cmd == "quit" or cmd == "q":
                break
            elif cmd == "list" or cmd == "l":
                client.list_objects()
            elif cmd == "spawn" or cmd == "s":
                result = await client.spawn("Cube", position=[0, 2, 0], name="Test Cube")
                print(f"Spawn result: {result.get('success')} - {result.get('message')}")
                if result.get("data"):
                    print(f"  UUID: {result['data'].get('uuid')}")
            elif cmd == "ping" or cmd == "p":
                result = await client.ping()
                print(f"Ping: {result.get('message')}")
            elif cmd == "refresh" or cmd == "r":
                await client.request_scene_dump()
                client.list_objects()
            else:
                print(f"Unknown command: {cmd}")
    
    except websockets.exceptions.ConnectionRefused:
        print("Error: Could not connect to Unity LiveLink server.")
        print("Make sure Unity is running with LiveLink Manager enabled.")
    except KeyboardInterrupt:
        print("\nInterrupted.")
    finally:
        await client.disconnect()


async def simple_demo():
    """Simple non-interactive demo."""
    print("Unity LiveLink - Python Client Demo")
    print("=" * 40)
    
    try:
        async with websockets.connect("ws://localhost:8080") as ws:
            # Receive scene dump
            data = await ws.recv()
            scene = json.loads(data)
            
            print(f"\nConnected to Unity!")
            print(f"Scene: {scene.get('payload', {}).get('scene_name', 'Unknown')}")
            print(f"Objects: {scene.get('payload', {}).get('object_count', 0)}")
            
            # Send ping
            ws.send(json.dumps({
                "type": "ping",
                "request_id": "demo-001",
                "payload": {}
            }))
            
            response = await ws.recv()
            result = json.loads(response)
            print(f"\nPing response: {result.get('message', 'N/A')}")
            
            # List objects
            print("\nScene objects:")
            for obj in scene.get("payload", {}).get("objects", []):
                pos = obj.get("transform", {}).get("pos", [0, 0, 0])
                print(f"  - {obj.get('name')} @ ({pos[0]:.1f}, {pos[1]:.1f}, {pos[2]:.1f})")
            
    except websockets.exceptions.ConnectionRefused:
        print("\nError: Could not connect to Unity LiveLink server.")
        print("Make sure Unity is running with LiveLink Manager enabled.")


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "--simple":
        asyncio.run(simple_demo())
    else:
        asyncio.run(interactive_demo())
