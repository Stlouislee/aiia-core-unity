# Copilot Instructions — Unity LiveLink

## Repository Summary

This repository is a **Unity Package Manager (UPM) package** (`com.livelink.core`) that provides a bidirectional bridge between a running Unity scene and external applications. It exposes two network interfaces:

1. **WebSocket server** (port 8080) — low-level command/response protocol for scene manipulation.
2. **MCP HTTP+SSE server** (port 3001) — JSON-RPC 2.0 transport implementing the [Model Context Protocol](https://modelcontextprotocol.io/) specification (Tools, Resources, Prompts).

Both servers run inside the Unity Editor as coroutines/background threads and funnel all Unity API calls through `MainThreadDispatcher`.

## Build & Validation

This is a **Unity package, not a standalone project**. There is no `dotnet build`, `msbuild`, `Makefile`, or CI pipeline.

- **Compilation** happens automatically when Unity imports the package via its assembly definitions (`.asmdef`).
- **Validation**: open the package in Unity 2020.3+ and check the Console for compile errors. No other build step exists.
- **Automated tests**: none at this time; test manually with the Python sample clients in `Samples~/PythonClient/`.

## Project Layout

```
package.json                 # UPM manifest (com.livelink.core v1.1.0)
Runtime/
  LiveLink.asmdef            # Runtime assembly; references glTFast
  Scripts/
    LiveLinkManager.cs       # MonoBehaviour entry point, server lifecycle, command dispatch
    MCPToolHandler.cs         # MCP JSON-RPC dispatcher (tools/resources/prompts handlers)
    MCPResourceProvider.cs    # unity:// resource URI routing and data extraction
    MCPResourceMapper.cs      # URI helpers for both unity:// and legacy mcp:// schemes
    SceneScanner.cs           # Scene hierarchy scanning, UUID tracking, delta detection
    SceneEventTracker.cs      # Tracks scene events (create, delete, transform, etc.)
    ResourceDTOs.cs           # DTOs for resource data (SceneInfoDTO, HierarchyNodeDTO, etc.)
    MainThreadDispatcher.cs   # Thread-safe Unity API access singleton
    Network/
      MCPHttpServer.cs        # HTTP+SSE transport, session management (MCPSession)
      LiveLinkServer.cs       # Raw WebSocket server
      PacketSchemas.cs        # All DTOs, JSON-RPC types, serialization helpers
Editor/
  LiveLink.Editor.asmdef      # Editor-only assembly; references LiveLink
  LiveLinkManagerEditor.cs    # Custom Inspector for LiveLinkManager
Samples~/
  PythonClient/               # Python test clients (WebSocket + MCP HTTP)
```

## Assembly Definitions

| Assembly | Namespace(s) | Platform | Notes |
|---|---|---|---|
| `LiveLink` | `LiveLink`, `LiveLink.Network` | All | References `glTFast`; auto-defines `LIVELINK_GLTFAST` when glTFast ≥ 0.0.0 is installed |
| `LiveLink.Editor` | `LiveLink.Editor` | Editor only | References `LiveLink` |

## Conditional Compilation

The only conditional symbol is **`LIVELINK_GLTFAST`**, auto-defined via `versionDefines` in `LiveLink.asmdef`. It gates glTF/GLB import and spawn functionality in `MCPToolHandler.cs`. When editing code inside `#if LIVELINK_GLTFAST` blocks, assume that `GLTFast` APIs are available.

## Key Architectural Patterns

### Threading Model

Unity APIs are **main-thread-only**. Network servers run on background threads. Use `MainThreadDispatcher` to bridge:

```csharp
MainThreadDispatcher.Enqueue(() => {
    // Safe to call Unity APIs here
    var obj = GameObject.Find("name");
});
```

The async variant returns a `Task<T>`:

```csharp
var result = await MainThreadDispatcher.EnqueueAsync(() => {
    return someUnityOperation();
});
```

### MCP Method Dispatch

`MCPToolHandler.HandleRequestAsync()` is the central router. It pattern-matches on `method` strings (`initialize`, `tools/list`, `tools/call`, `resources/list`, `resources/read`, `prompts/list`, `prompts/get`, `notifications/*`). All tool calls are dispatched through `HandleCallToolAsync()` which switches on `params.name`.

### Session Lifecycle (HTTP+SSE)

`MCPHttpServer` manages `MCPSession` objects keyed by session ID. Flow:
1. Client opens SSE connection → session created, `sessionId` sent as first event.
2. Client sends JSON-RPC to `/mcp?sessionId=xxx` → session validated, request dispatched.
3. `initialize` response sets `session.IsInitialized = true`.
4. Background cleanup loop expires sessions idle > 5 minutes.

### Serialization

All JSON goes through **Newtonsoft.Json** (`JsonConvert`, `JObject`, `JArray`). DTOs live in `PacketSchemas.cs` under the `LiveLink.Network` namespace.

## Coding Conventions

- **C# style**: PascalCase for public members, `_camelCase` for private fields, `camelCase` for local variables.
- **Namespace rule**: files under `Runtime/Scripts/Network/` use `LiveLink.Network`; everything else uses `LiveLink`; editor code uses `LiveLink.Editor`.
- **Error handling**: MCP errors use JSON-RPC error codes (e.g., `-32601` method not found, `-32001` session missing, `-32002` not initialized). Wrap risky Unity calls in try/catch and return structured error responses.
- **Async**: handler methods that touch Unity APIs or do I/O are `async Task<MCPResponse>`; pure-data methods can be synchronous returning `MCPResponse` directly.
- **Logging**: use `Debug.Log` / `Debug.LogWarning` / `Debug.LogError` prefixed with `[LiveLink]` or `[MCPHttpServer]` tags.

## Dependencies

| Package | Version | Purpose |
|---|---|---|
| `com.unity.nuget.newtonsoft-json` | 3.0.2 | JSON serialization (required) |
| `com.unity.cloud.gltfast` | 6.1.0 | glTF/GLB runtime import (optional, gates `LIVELINK_GLTFAST`) |

## When Adding New MCP Tools

1. Add the tool definition to `HandleListTools()` in `MCPToolHandler.cs` (name, description, inputSchema).
2. Add a case in the switch inside `HandleCallToolAsync()`.
3. If the tool needs Unity APIs, use `MainThreadDispatcher.EnqueueAsync()`.
4. Return results via `MCPResponse.CreateResult()` or errors via `MCPResponse.CreateError()`.
5. Update the Python test client (see [Updating the Python Test Client](#updating-the-python-test-client) below).
6. Update `README.md` with the new tool's documentation.

## When Adding New MCP Resources

1. Add the resource URI template to `GetResourceTemplates()` in `MCPResourceProvider.cs`.
2. Add a routing case in `ReadResource()` in `MCPResourceProvider.cs`.
3. Create a handler method that returns a DTO or data object.
4. DTOs for new resources go in `ResourceDTOs.cs` under the `LiveLink.Network` namespace.
5. If the resource needs a new URI helper, add it to `MCPResourceMapper.cs`.
6. The URI scheme is `unity://` — e.g., `unity://scene/active`, `unity://go/{instanceId}`.
7. Update the Python test client (see [Updating the Python Test Client](#updating-the-python-test-client) below).
8. Update `README.md` with the new resource's documentation.

## When Adding New MCP Prompts

1. Add the prompt metadata to `HandleListPrompts()`.
2. Add a case in `HandleGetPrompt()` that builds a `messages` array.
3. Prompt builder methods follow the pattern `BuildXxxPrompt(JObject arguments)`.
4. Update the Python test client (see [Updating the Python Test Client](#updating-the-python-test-client) below).

## Updating the Python Test Client

The unified test client lives at `Samples~/PythonClient/livelink_mcp_client.py`. It is the single source of truth for manual testing and must stay in sync with the server's capabilities. When adding or changing any MCP Tool, Resource, or Prompt on the server side, update the client as follows:

### File structure

```
Samples~/PythonClient/
  livelink_mcp_client.py   # Unified client class + CLI (primary file)
  example_usage.py          # End-to-end usage examples
  requirements.txt          # Python dependencies (websockets, aiohttp)
  README.md                 # User-facing documentation
```

### For a new Tool

1. Add a **convenience method** in the `# Core Tools` section of `LiveLinkMCPClient`. Match the tool name and every parameter from the server's `HandleListTools()` definition:
   ```python
   async def my_new_tool(self, required_arg: str, optional_arg: int = None) -> dict:
       """Docstring matching the server description."""
       args = {"required_arg": required_arg}
       if optional_arg is not None: args["optional_arg"] = optional_arg
       return await self.call_tool("my_new_tool", args)
   ```
2. Add a test case in `run_tests()` that calls the new method and validates the response shape.
3. Optionally add an interactive command in `run_demo()`.

### For a new Resource

1. Add a **convenience method** in the `# Scene Resources` or `# GameObject Resources` section. Build the full `unity://` URI including any query parameters:
   ```python
   async def get_my_resource(self, param: str = "default") -> dict:
       """Docstring matching the server description."""
       return await self.read_resource(f"unity://my/resource?param={param}")
   ```
2. Add a test case in `run_tests()`.

### For a new Prompt

1. Add a **convenience method** in the `# Prompts` section:
   ```python
   async def run_my_prompt(self, intent: str, flag: bool = False) -> dict:
       """Docstring matching the server description."""
       args = {"intent": intent}
       if flag: args["flag"] = flag
       return await self.get_prompt("my_prompt", args)
   ```
2. Add a test case in `run_tests()`.

### General rules

- Parameter names in the Python methods **must exactly match** the JSON keys the server expects (snake_case).
- Tools use `uuid` (string) for object identity; Resources use `instance_id` (int / Unity `InstanceID`).
- Keep `README.md` and `example_usage.py` up to date with any new methods.
- Run `python3 -m py_compile Samples~/PythonClient/livelink_mcp_client.py` to verify syntax after edits.
