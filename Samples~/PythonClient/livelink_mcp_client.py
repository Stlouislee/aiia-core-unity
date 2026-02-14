#!/usr/bin/env python3
"""
Unity LiveLink - Unified MCP Client

A unified Python client for testing Unity LiveLink MCP server.
Supports both WebSocket and HTTP+SSE transports.

Requirements:
    pip install websockets aiohttp

Usage:
    # Interactive mode
    python livelink_mcp_client.py

    # Run all tests
    python livelink_mcp_client.py --test

    # Custom server
    python livelink_mcp_client.py --http --port 3001
"""

import asyncio
import argparse
import json
import sys
from typing import Optional, Any
from dataclasses import dataclass

try:
    import websockets
except ImportError:
    print("Please install websockets: pip install websockets")
    sys.exit(1)

try:
    import aiohttp
except ImportError:
    print("Please install aiohttp: pip install aiohttp")
    sys.exit(1)


@dataclass
class MCPTool:
    """Represents an MCP tool."""
    name: str
    description: str
    input_schema: dict


@dataclass
class MCPResource:
    """Represents an MCP resource."""
    uri: str
    name: str
    description: str
    mime_type: Optional[str] = None


class LiveLinkMCPClient:
    """Unified MCP client for Unity LiveLink."""
    
    def __init__(self, transport: str = "http", host: str = "localhost", port: int = None):
        """
        Initialize the MCP client.
        
        Args:
            transport: "http" for HTTP+SSE, "ws" for WebSocket
            host: Server host
            port: Server port (8080 for WS, 3001 for HTTP by default)
        """
        self.transport = transport
        self.host = host
        self.port = port or (8080 if transport == "ws" else 3001)
        self.ws: Optional[websockets.WebSocketClientProtocol] = None
        self.session: Optional[aiohttp.ClientSession] = None
        self.session_id: Optional[str] = None
        self.mcp_endpoint: Optional[str] = None
        self.request_id = 0
        self.server_info: Optional[dict] = None
        self.server_capabilities: Optional[dict] = None
        self.tools: list[MCPTool] = []
        self.resources: list[MCPResource] = []
    
    @property
    def ws_uri(self) -> str:
        return f"ws://{self.host}:{self.port}"
    
    @property
    def http_base_url(self) -> str:
        return f"http://{self.host}:{self.port}"
    
    # ==================== Connection Methods ====================
    
    async def connect(self) -> bool:
        """Connect to the MCP server."""
        if self.transport == "ws":
            return await self._connect_ws()
        else:
            return await self._connect_http()
    
    async def _connect_ws(self) -> bool:
        """Connect via WebSocket."""
        print(f"Connecting to WebSocket server at {self.ws_uri}...")
        try:
            self.ws = await websockets.connect(self.ws_uri)
            print("Connected!")
            return True
        except Exception as e:
            print(f"Connection failed: {e}")
            return False
    
    async def _connect_http(self) -> bool:
        """Connect via HTTP+SSE."""
        print(f"Connecting to HTTP+SSE server at {self.http_base_url}...")
        try:
            self.session = aiohttp.ClientSession()
            
            # Connect to SSE and get session ID
            async with self.session.get(f"{self.http_base_url}/sse") as resp:
                if resp.status != 200:
                    print(f"SSE connection failed: {resp.status}")
                    return False
                
                async for line in resp.content:
                    decoded = line.decode('utf-8').strip()
                    if decoded.startswith("data: "):
                        endpoint_path = decoded[6:]
                        if "sessionId=" in endpoint_path:
                            from urllib.parse import urlparse, parse_qs
                            parsed = urlparse(endpoint_path)
                            params = parse_qs(parsed.query)
                            self.session_id = params.get('sessionId', [None])[0]
                            self.mcp_endpoint = f"{self.http_base_url}{parsed.path}"
                            print(f"Session ID: {self.session_id}")
                            break
            
            if not self.session_id:
                print("Failed to obtain session ID")
                return False
            
            # Initialize MCP connection
            await self._mcp_initialize()
            print("Connected!")
            return True
        except Exception as e:
            print(f"Connection failed: {e}")
            return False
    
    async def _mcp_initialize(self):
        """Send MCP initialize request."""
        init_request = {
            "jsonrpc": "2.0",
            "id": 0,
            "method": "initialize",
            "params": {
                "protocolVersion": "2024-11-05",
                "capabilities": {},
                "clientInfo": {"name": "LiveLink MCP Client", "version": "1.0.0"}
            }
        }
        
        if self.transport == "ws":
            await self.ws.send(json.dumps(init_request))
            response = await self.ws.recv()
        else:
            async with self.session.post(
                f"{self.mcp_endpoint}?sessionId={self.session_id}", 
                json=init_request
            ) as resp:
                response = await resp.json()
        
        if "result" in response:
            self.server_info = response["result"].get("serverInfo", {})
            self.server_capabilities = response["result"].get("capabilities", {})
            print(f"Server: {self.server_info.get('name')} v{self.server_info.get('version')}")
        
        # Send initialized notification
        notification = {"jsonrpc": "2.0", "method": "notifications/initialized"}
        if self.transport == "ws":
            await self.ws.send(json.dumps(notification))
        else:
            async with self.session.post(
                f"{self.mcp_endpoint}?sessionId={self.session_id}", 
                json=notification
            ) as resp:
                pass
    
    async def disconnect(self):
        """Disconnect from the server."""
        if self.transport == "ws" and self.ws:
            await self.ws.close()
            self.ws = None
        elif self.session:
            await self.session.close()
            self.session = None
        print("Disconnected.")
    
    # ==================== Request Methods ====================
    
    def _next_request_id(self) -> int:
        self.request_id += 1
        return self.request_id
    
    async def _send_request(self, method: str, params: dict = None) -> dict:
        """Send a JSON-RPC request."""
        request = {
            "jsonrpc": "2.0",
            "id": self._next_request_id(),
            "method": method,
            "params": params or {}
        }
        
        if self.transport == "ws":
            await self.ws.send(json.dumps(request))
            response = await self.ws.recv()
            return json.loads(response)
        else:
            async with self.session.post(
                f"{self.mcp_endpoint}?sessionId={self.session_id}", 
                json=request
            ) as resp:
                return await resp.json()
    
    # ==================== MCP Methods ====================
    
    async def list_tools(self) -> list[MCPTool]:
        """List available tools."""
        response = await self._send_request("tools/list")
        self.tools = []
        if "result" in response:
            for tool in response["result"].get("tools", []):
                self.tools.append(MCPTool(
                    name=tool.get("name", ""),
                    description=tool.get("description", ""),
                    input_schema=tool.get("inputSchema", {})
                ))
        return self.tools
    
    async def list_resources(self) -> list[MCPResource]:
        """List available resources."""
        response = await self._send_request("resources/list")
        self.resources = []
        if "result" in response:
            for res in response["result"].get("resources", []):
                self.resources.append(MCPResource(
                    uri=res.get("uri", ""),
                    name=res.get("name", ""),
                    description=res.get("description", ""),
                    mime_type=res.get("mimeType")
                ))
        return self.resources
    
    async def call_tool(self, name: str, arguments: dict = None) -> dict:
        """Call a tool."""
        return await self._send_request("tools/call", {
            "name": name,
            "arguments": arguments or {}
        })
    
    async def read_resource(self, uri: str) -> dict:
        """Read a resource."""
        return await self._send_request("resources/read", {"uri": uri})
    
    async def list_prompts(self) -> list[dict]:
        """List available prompts."""
        response = await self._send_request("prompts/list")
        if "result" in response:
            return response["result"].get("prompts", [])
        return []
    
    async def get_prompt(self, name: str, arguments: dict = None) -> dict:
        """Get a prompt."""
        return await self._send_request("prompts/get", {
            "name": name,
            "arguments": arguments or {}
        })
    
    # ==================== Convenience Methods ====================
    
    # ==================== Core Tools ====================
    
    async def spawn_object(self, prefab_key: str, position: list = None, 
                          rotation: list = None, scale: list = None,
                          name: str = None, parent_uuid: str = None) -> dict:
        """Spawn a new object from a prefab.
        
        Args:
            prefab_key: Name of the prefab (e.g., 'Cube', 'Sphere')
            position: World position [x, y, z]
            rotation: Quaternion rotation [x, y, z, w]
            scale: Local scale [x, y, z]
            name: Optional name for the spawned object
            parent_uuid: Optional UUID of the parent object
        """
        args = {"prefab_key": prefab_key}
        if position: args["position"] = position
        if rotation: args["rotation"] = rotation
        if scale: args["scale"] = scale
        if name: args["name"] = name
        if parent_uuid: args["parent_uuid"] = parent_uuid
        return await self.call_tool("spawn_object", args)
    
    async def transform_object(self, uuid: str, position: list = None,
                              rotation: list = None, scale: list = None) -> dict:
        """Transform an object by UUID.
        
        Args:
            uuid: UUID of the object to transform
            position: World position [x, y, z]
            rotation: Quaternion rotation [x, y, z, w]
            scale: Local scale [x, y, z]
        """
        args = {"uuid": uuid}
        if position: args["position"] = position
        if rotation: args["rotation"] = rotation
        if scale: args["scale"] = scale
        return await self.call_tool("transform_object", args)
    
    async def delete_object(self, uuid: str) -> dict:
        """Delete an object by UUID."""
        return await self.call_tool("delete_object", {"uuid": uuid})
    
    async def scene_dump(self, include_inactive: bool = False) -> dict:
        """Get a full dump of the current scene hierarchy.
        
        Args:
            include_inactive: Whether to include inactive objects
        """
        return await self.call_tool("scene_dump", {"include_inactive": include_inactive})
    
    async def list_spawnable_objects(self) -> dict:
        """Get a list of all prefab names that can be spawned."""
        return await self.call_tool("list_spawnable_objects", {})
    
    async def get_view_context(self, camera_tag: str = "MainCamera",
                              include_visible_objects: bool = False,
                              raycast_distance: float = 100) -> dict:
        """Get the current camera/player view context.
        
        Args:
            camera_tag: Camera tag to query (default: 'MainCamera')
            include_visible_objects: Include list of objects visible in camera frustum
            raycast_distance: Distance to raycast from camera center
        """
        args = {}
        if camera_tag: args["camera_tag"] = camera_tag
        if include_visible_objects: args["include_visible_objects"] = include_visible_objects
        if raycast_distance: args["raycast_distance"] = raycast_distance
        return await self.call_tool("get_view_context", args)
    
    async def spawn_gltf(self, url: str = None, data_base64: str = None,
                        source_uri: str = None, id: str = None,
                        name: str = None, position: list = None,
                        rotation: list = None, scale: list = None,
                        parent_uuid: str = None) -> dict:
        """Spawn a glTF asset at runtime via glTFast.
        
        Args:
            url: URL or file:// path to a .gltf/.glb
            data_base64: Base64 encoded binary glTF (.glb)
            source_uri: Original URI for resolving relative references
            id: Optional UUID to assign to the spawned root
            name: Optional name for the spawned root object
            position: World position [x, y, z]
            rotation: Quaternion rotation [x, y, z, w]
            scale: Local scale [x, y, z]
            parent_uuid: Optional UUID of the parent object
        """
        args = {}
        if url: args["url"] = url
        if data_base64: args["data_base64"] = data_base64
        if source_uri: args["source_uri"] = source_uri
        if id: args["id"] = id
        if name: args["name"] = name
        if position: args["position"] = position
        if rotation: args["rotation"] = rotation
        if scale: args["scale"] = scale
        if parent_uuid: args["parent_uuid"] = parent_uuid
        return await self.call_tool("spawn_gltf", args)
    
    # ==================== Scene Resources ====================
    
    async def get_scene_info(self) -> dict:
        """Get current scene information (unity://scene/active)."""
        return await self.read_resource("unity://scene/active")
    
    async def get_scene_hierarchy(self, root: str = "/", depth: int = 2) -> dict:
        """Get scene hierarchy tree (unity://scene/hierarchy).
        
        Args:
            root: Root path (default: '/')
            depth: Maximum depth (default: 2, max: 50)
        """
        uri = f"unity://scene/hierarchy?root={root}&depth={depth}"
        return await self.read_resource(uri)
    
    async def get_selection(self) -> dict:
        """Get currently selected objects (unity://selection)."""
        return await self.read_resource("unity://selection")
    
    async def get_recent_events(self, count: int = 50) -> dict:
        """Get recent scene events (unity://events/recent).
        
        Args:
            count: Number of recent events to return (default: 50)
        """
        uri = f"unity://events/recent?count={count}"
        return await self.read_resource(uri)
    
    # ==================== GameObject Resources ====================
    
    async def get_gameobject(self, instance_id: int) -> dict:
        """Get GameObject metadata (unity://go/{instanceId}).
        
        Args:
            instance_id: Unity InstanceId of the GameObject
        """
        return await self.read_resource(f"unity://go/{instance_id}")
    
    async def get_gameobject_components(self, instance_id: int) -> dict:
        """Get all components on a GameObject (unity://go/{instanceId}/components).
        
        Args:
            instance_id: Unity InstanceId of the GameObject
        """
        return await self.read_resource(f"unity://go/{instance_id}/components")
    
    async def get_component(self, instance_id: int, component_type: str) -> dict:
        """Get component snapshot (unity://component/{instanceId}/{componentType}).
        
        Args:
            instance_id: Unity InstanceId of the component
            component_type: Component type name (e.g., 'Transform', 'MeshRenderer')
        """
        return await self.read_resource(f"unity://component/{instance_id}/{component_type}")
    
    # ==================== Prompts ====================
    
    async def run_scene_analysis(self, analysis_goal: str = None,
                                include_inactive: bool = False,
                                focus_query: str = None) -> dict:
        """Run scene analysis prompt workflow.
        
        Args:
            analysis_goal: What to optimize or inspect
            include_inactive: Whether to include inactive objects
            focus_query: Optional object/type keyword to focus on
        """
        args = {}
        if analysis_goal: args["analysis_goal"] = analysis_goal
        if include_inactive: args["include_inactive"] = include_inactive
        if focus_query: args["focus_query"] = focus_query
        return await self.get_prompt("scene_analysis", args)
    
    async def run_spawn_from_intent(self, intent: str, count: int = None,
                                   placement_strategy: str = None) -> dict:
        """Run intent-to-spawn prompt workflow.
        
        Args:
            intent: What should be created in the scene
            count: Preferred number of spawned objects
            placement_strategy: e.g. front_of_camera, grid, random_scatter
        """
        args = {"intent": intent}
        if count is not None: args["count"] = count
        if placement_strategy: args["placement_strategy"] = placement_strategy
        return await self.get_prompt("spawn_from_intent", args)
    
    async def run_object_repair(self, uuid: str, issue_description: str = None,
                               preserve_world_pose: bool = False) -> dict:
        """Run object repair prompt workflow.
        
        Args:
            uuid: Target object UUID
            issue_description: Describe the observed issue
            preserve_world_pose: Preserve world-space pose when reparenting
        """
        args = {"uuid": uuid}
        if issue_description: args["issue_description"] = issue_description
        if preserve_world_pose: args["preserve_world_pose"] = preserve_world_pose
        return await self.get_prompt("object_repair", args)
    
    async def run_scene_cleanup(self, scope: str = None,
                               name_pattern: str = None,
                               dry_run: bool = False) -> dict:
        """Run scene cleanup prompt workflow.
        
        Args:
            scope: all, inactive_only, or name_pattern
            name_pattern: Regex-like substring filter for candidate names
            dry_run: If true, only return plan and do not execute delete calls
        """
        args = {}
        if scope: args["scope"] = scope
        if name_pattern: args["name_pattern"] = name_pattern
        if dry_run: args["dry_run"] = dry_run
        return await self.get_prompt("scene_cleanup", args)
    
    # ==================== Demo & Test Methods ====================
    
    async def run_demo(self):
        """Run interactive demo."""
        print("\n" + "=" * 50)
        print("Unity LiveLink MCP Client - Interactive Demo")
        print("=" * 50)
        
        # List tools
        print("\n--- Available Tools ---")
        tools = await self.list_tools()
        for tool in tools:
            print(f"  {tool.name}: {tool.description[:60]}...")
        
        # List resources
        print("\n--- Available Resources ---")
        resources = await self.list_resources()
        for res in resources[:5]:
            print(f"  {res.uri}")
        if len(resources) > 5:
            print(f"  ... and {len(resources) - 5} more")
        
        # List prompts
        print("\n--- Available Prompts ---")
        prompts = await self.list_prompts()
        for prompt in prompts:
            print(f"  {prompt.get('name')}: {prompt.get('title')}")
        
        # Demo commands
        print("\n--- Demo Commands ---")
        print("  spawn cube       - Spawn a cube")
        print("  spawn sphere     - Spawn a sphere")
        print("  list spawn       - List spawnable objects")
        print("  scene            - Get scene info")
        print("  hierarchy        - Get scene hierarchy")
        print("  view             - Get camera view context")
        print("  events           - Get recent events")
        print("  quit             - Exit")
        
        while True:
            try:
                cmd = input("\n> ").strip().lower()
            except EOFError:
                break
            
            if cmd in ("quit", "q", "exit"):
                break
            elif cmd == "spawn cube":
                result = await self.spawn_object("Cube", position=[0, 2, 0], name="MCP_Cube")
                self._print_result(result)
            elif cmd == "spawn sphere":
                result = await self.spawn_object("Sphere", position=[0, 3, 0], name="MCP_Sphere")
                self._print_result(result)
            elif cmd == "list spawn":
                result = await self.list_spawnable_objects()
                self._print_result(result)
            elif cmd == "scene":
                result = await self.get_scene_info()
                self._print_result(result)
            elif cmd == "hierarchy":
                result = await self.get_scene_hierarchy(depth=2)
                self._print_result(result)
            elif cmd == "view":
                result = await self.get_view_context(include_visible_objects=True)
                self._print_result(result)
            elif cmd == "events":
                result = await self.get_recent_events(count=10)
                self._print_result(result)
            elif cmd == "dump":
                result = await self.scene_dump(include_inactive=True)
                self._print_result(result)
            else:
                print(f"Unknown command: {cmd}")
    
    async def run_tests(self):
        """Run automated tests."""
        print("\n" + "=" * 50)
        print("Unity LiveLink MCP Client - Automated Tests")
        print("=" * 50)
        
        tests_passed = 0
        tests_failed = 0
        
        # Test 1: List tools
        print("\n[TEST 1] List tools")
        try:
            tools = await self.list_tools()
            print(f"  Found {len(tools)} tools")
            for tool in tools[:3]:
                print(f"    - {tool.name}")
            tests_passed += 1
        except Exception as e:
            print(f"  FAILED: {e}")
            tests_failed += 1
        
        # Test 2: List resources
        print("\n[TEST 2] List resources")
        try:
            resources = await self.list_resources()
            print(f"  Found {len(resources)} resources")
            for res in resources[:3]:
                print(f"    - {res.uri}")
            tests_passed += 1
        except Exception as e:
            print(f"  FAILED: {e}")
            tests_failed += 1
        
        # Test 3: Get scene info
        print("\n[TEST 3] Get scene info")
        try:
            scene = await self.get_scene_info()
            print(f"  Scene name: {scene.get('result', {}).get('data', {}).get('name', 'Unknown')}")
            tests_passed += 1
        except Exception as e:
            print(f"  FAILED: {e}")
            tests_failed += 1
        
        # Test 4: Spawn object
        print("\n[TEST 4] Spawn object")
        try:
            result = await self.spawn_object("Cube", position=[0, 2, 0], name="Test_Cube")
            uuid = result.get("result", {}).get("data", {}).get("uuid")
            if uuid:
                print(f"  Spawned: {uuid}")
                
                # Test 5: Transform object
                print("\n[TEST 5] Transform object")
                transform_result = await self.transform_object(uuid, position=[0, 5, 0])
                print(f"  Transform: {'OK' if 'result' in transform_result else 'FAILED'}")
                tests_passed += 1
            else:
                print(f"  FAILED: No UUID returned")
                tests_failed += 1
        except Exception as e:
            print(f"  FAILED: {e}")
            tests_failed += 1
        
        # Test 6: Get scene hierarchy
        print("\n[TEST 6] Get scene hierarchy")
        try:
            result = await self.get_scene_hierarchy(depth=2)
            hierarchy = result.get("result", {}).get("data", [])
            print(f"  Hierarchy nodes: {len(hierarchy) if isinstance(hierarchy, list) else 'N/A'}")
            tests_passed += 1
        except Exception as e:
            print(f"  FAILED: {e}")
            tests_failed += 1
        
        # Test 7: List spawnable objects
        print("\n[TEST 7] List spawnable objects")
        try:
            result = await self.list_spawnable_objects()
            objs = result.get("result", {}).get("data", [])
            print(f"  Spawnable objects: {len(objs) if isinstance(objs, list) else 'N/A'}")
            tests_passed += 1
        except Exception as e:
            print(f"  FAILED: {e}")
            tests_failed += 1
        
        # Test 8: Get view context
        print("\n[TEST 8] Get view context")
        try:
            result = await self.get_view_context()
            data = result.get("result", {}).get("data", {})
            print(f"  Camera position: {data.get('camera_position', 'N/A')}")
            tests_passed += 1
        except Exception as e:
            print(f"  FAILED: {e}")
            tests_failed += 1
        
        # Summary
        print("\n" + "=" * 50)
        print(f"Tests completed: {tests_passed} passed, {tests_failed} failed")
        print("=" * 50)
    
    def _print_result(self, result: dict):
        """Print a result nicely."""
        if "result" in result:
            data = result["result"]
            if isinstance(data, dict):
                print(json.dumps(data, indent=2))
            else:
                print(data)
        elif "error" in result:
            print(f"Error: {result['error']}")
    
    async def health_check(self) -> bool:
        """Check server health (HTTP only)."""
        if self.transport != "http" or not self.session:
            return False
        try:
            async with self.session.get(f"{self.http_base_url}/health") as resp:
                return resp.status == 200
        except:
            return False


async def main():
    parser = argparse.ArgumentParser(description="Unity LiveLink MCP Client")
    parser.add_argument("--transport", "-t", choices=["http", "ws"], default="http",
                        help="Transport type (default: http)")
    parser.add_argument("--host", "-H", default="localhost",
                        help="Server host (default: localhost)")
    parser.add_argument("--port", "-p", type=int, default=None,
                        help="Server port (default: 3001 for http, 8080 for ws)")
    parser.add_argument("--test", action="store_true",
                        help="Run automated tests")
    parser.add_argument("--demo", action="store_true",
                        help="Run interactive demo (default)")
    
    args = parser.parse_args()
    
    client = LiveLinkMCPClient(transport=args.transport, host=args.host, port=args.port)
    
    try:
        if not await client.connect():
            print("Failed to connect to server")
            return 1
        
        if args.test:
            await client.run_tests()
        else:
            await client.run_demo()
    
    except KeyboardInterrupt:
        print("\nInterrupted.")
    finally:
        await client.disconnect()
    
    return 0


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
