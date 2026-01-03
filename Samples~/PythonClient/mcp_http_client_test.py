#!/usr/bin/env python3
"""
Unity LiveLink - MCP HTTP Client Test

This script tests the MCP implementation using HTTP + SSE transport (standard MCP).
"""

import asyncio
import json
import sys
import aiohttp

try:
    import aiohttp
except ImportError:
    print("Please install aiohttp: pip install aiohttp")
    sys.exit(1)

async def test_mcp_http(base_url: str = "http://localhost:8081"):
    """Test MCP server using HTTP + SSE"""
    
    async with aiohttp.ClientSession() as session:
        print(f"Testing MCP server at {base_url}")
        
        # 1. Health Check
        print("\n--- Health Check ---")
        async with session.get(f"{base_url}/health") as resp:
            health = await resp.json()
            print(f"Status: {resp.status}")
            print(f"Response: {json.dumps(health, indent=2)}")
        
        # 2. Initialize (MCP requirement)
        print("\n--- Initialize (MCP Protocol Handshake) ---")
        init_request = {
            "jsonrpc": "2.0",
            "id": 0,
            "method": "initialize",
            "params": {
                "protocolVersion": "2025-11-25",
                "capabilities": {},
                "clientInfo": {
                    "name": "Unity LiveLink Test Client",
                    "version": "1.0.0"
                }
            }
        }
        async with session.post(f"{base_url}/mcp", json=init_request) as resp:
            response = await resp.json()
            print(f"Status: {resp.status}")
            print(f"Response: {json.dumps(response, indent=2)}")
            
            if "error" in response:
                print(f"Initialization failed: {response['error']}")
                return
            
            if "result" not in response:
                print("No result in initialize response")
                return
            
            # Extract server capabilities
            server_info = response["result"].get("serverInfo", {})
            capabilities = response["result"].get("capabilities", {})
            print(f"\nConnected to: {server_info.get('name', 'Unknown')} v{server_info.get('version', 'Unknown')}")
            print(f"Server capabilities: {list(capabilities.keys())}")
        
        # 3. Send initialized notification (MCP requirement)
        print("\n--- Send Initialized Notification ---")
        initialized_notification = {
            "jsonrpc": "2.0",
            "method": "notifications/initialized"
        }
        async with session.post(f"{base_url}/mcp", json=initialized_notification) as resp:
            print(f"Status: {resp.status}")
            if resp.status == 204:
                print("Initialized notification sent successfully")
            else:
                print(f"Unexpected status: {resp.status}")
        
        # 4. List Tools
        print("\n--- Testing tools/list ---")
        list_tools_request = {
            "jsonrpc": "2.0",
            "id": 1,
            "method": "tools/list",
            "params": {}
        }
        async with session.post(f"{base_url}/mcp", json=list_tools_request) as resp:
            response = await resp.json()
            print(f"Status: {resp.status}")
            print(f"Response: {json.dumps(response, indent=2)}")
        
        # 5. List Resources
        print("\n--- Testing resources/list ---")
        list_resources_request = {
            "jsonrpc": "2.0",
            "id": 2,
            "method": "resources/list",
            "params": {}
        }
        async with session.post(f"{base_url}/mcp", json=list_resources_request) as resp:
            response = await resp.json()
            print(f"Status: {resp.status}")
            print(f"Response: {json.dumps(response, indent=2)}")
        
        # 6. Call Tool (Spawn)
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
                    "name": "MCP_HTTP_Spawned_Cube"
                }
            }
        }
        async with session.post(f"{base_url}/mcp", json=spawn_request) as resp:
            response = await resp.json()
            print(f"Status: {resp.status}")
            print(f"Response: {json.dumps(response, indent=2)}")
            
            # Extract UUID if spawn was successful
            uuid = None
            if "result" in response:
                data = response["result"].get("data")
                if isinstance(data, dict):
                    uuid = data.get("uuid")
            
            if uuid:
                # 7. Read Resource
                print(f"\n--- Testing resources/read (for {uuid}) ---")
                read_resource_request = {
                    "jsonrpc": "2.0",
                    "id": 4,
                    "method": "resources/read",
                    "params": {
                        "uri": f"mcp://unity/scenes/MainScene/objects/{uuid}"
                    }
                }
                async with session.post(f"{base_url}/mcp", json=read_resource_request) as resp:
                    response = await resp.json()
                    print(f"Status: {resp.status}")
                    print(f"Response: {json.dumps(response, indent=2)}")
                
                # 8. Call Tool (Transform)
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
                async with session.post(f"{base_url}/mcp", json=transform_request) as resp:
                    response = await resp.json()
                    print(f"Status: {resp.status}")
                    print(f"Response: {json.dumps(response, indent=2)}")
        
        # 9. Test SSE (optional - just connect and receive a few events)
        print("\n--- Testing SSE Connection ---")
        print("Connecting to SSE endpoint...")
        try:
            async with session.get(f"{base_url}/sse") as resp:
                print(f"SSE Status: {resp.status}")
                if resp.status == 200:
                    print("Receiving events (will stop after 10 seconds)...")
                    async for line in resp.content:
                        decoded = line.decode('utf-8').strip()
                        if decoded:
                            print(f"  {decoded}")
                        # Just show a few events then exit
                        await asyncio.sleep(0.1)
                        if asyncio.get_event_loop().time() > asyncio.get_event_loop().time() + 10:
                            break
        except asyncio.TimeoutError:
            print("SSE connection test completed")
        except Exception as e:
            print(f"SSE test skipped: {e}")

if __name__ == "__main__":
    asyncio.run(test_mcp_http())
