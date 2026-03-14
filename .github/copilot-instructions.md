# Copilot Instructions - Unity LiveLink

## Repository Summary

This repository is a Unity Package Manager package (`com.livelink.core`) for connecting a running Unity scene to external tools and in-app agents.

It currently exposes three major surfaces:

1. **WebSocket server** (default port `8080`) for the package's custom command/response protocol.
2. **LiveLink MCP server** (default port `8081`) for MCP Tools, Resources, and Prompts.
   - Primary endpoint: `POST /mcp` (`StreamableHttp` / modern MCP HTTP)
   - Compatibility endpoint: `GET /sse` plus `POST /mcp?sessionId=...` (legacy HTTP+SSE)
3. **Embedded Agent Runtime** that hosts Microsoft Agent Framework inside the Unity app and consumes the local LiveLink MCP server by default.

All Unity API access must stay on the main thread.

## Build And Validation

This is a Unity package, not a standalone .NET solution.

- Compilation happens when Unity imports the package assemblies.
- Validate changes by importing the package into Unity 2020.3+ and checking the Console.
- There is no repo-local `dotnet build` or CI pipeline for authoritative validation.
- Manual MCP validation can be done through the embedded runtime inspector or the Python samples in `Samples~/PythonClient/`.

## Project Layout

```text
package.json
CHANGELOG.md
README.md
Runtime/
  LiveLink.asmdef
  link.xml
  Agent/
    LiveLink.AgentRuntime.asmdef
    EmbeddedAgentRuntime.cs
    AgentRuntimeConfig.cs
    AgentExternalMcpServerConfig.cs
    AgentMcpClientFactory.cs
    AgentMcpConnectionTester.cs
    AgentMcpConnectionTestResult.cs
    AgentMcpHttpTransportMode.cs
    AgentMcpTransportType.cs
    AgentNamedValue.cs
    AgentToolNames.cs
  Plugins/
    AgentFramework/          # Vendored Agent Framework / MCP client assemblies
  Scripts/
    LiveLinkManager.cs
    MCPToolHandler.cs
    MCPResourceProvider.cs
    MCPResourceMapper.cs
    MainThreadDispatcher.cs
    SceneScanner.cs
    SceneEventTracker.cs
    ResourceDTOs.cs
    Network/
      MCPHttpServer.cs
      LiveLinkServer.cs
      PacketSchemas.cs
Editor/
  LiveLink.Editor.asmdef
  LiveLinkManagerEditor.cs
  Agent/
    LiveLink.AgentRuntime.Editor.asmdef
    AgentRuntimeConfigEditor.cs
    EmbeddedAgentRuntimeEditor.cs
Documentation~/
  Embedded-Agent-Framework-MVP.md
Samples~/
  PythonClient/
```

## Assembly Definitions

| Assembly | Platform | Purpose |
| --- | --- | --- |
| `LiveLink` | All | Core runtime package, LiveLink servers, MCP handlers, scene tooling |
| `LiveLink.AgentRuntime` | All | Embedded agent runtime and MCP client integration |
| `LiveLink.Editor` | Editor only | Inspector and menu support for `LiveLinkManager` |
| `LiveLink.AgentRuntime.Editor` | Editor only | Inspectors and creation helpers for embedded-agent assets/components |

## Core Runtime Rules

### Main-thread rule

Anything that touches Unity scene objects, components, cameras, or editor state must run through `MainThreadDispatcher`.

### First-party capability rule

If a Unity capability should be available to both:

- the embedded agent, and
- external MCP consumers

then it should be exposed through LiveLink MCP rather than through a private in-process agent-only API.

### MCP transport rule

For the built-in LiveLink MCP server:

- prefer `/mcp` with `StreamableHttp`
- keep `/sse` only for backward compatibility
- do not add new features only to the legacy SSE path

### Downstream MCP rule

External MCP servers configured in `AgentRuntimeConfig` are downstream dependencies of the embedded agent only.

Do **not** proxy or re-expose them through LiveLink MCP.

## Important Files

- `Runtime/Scripts/LiveLinkManager.cs` - entry point for WebSocket + MCP server lifecycle
- `Runtime/Scripts/Network/MCPHttpServer.cs` - MCP HTTP transport handling (`/mcp`, legacy `/sse`, request dispatch)
- `Runtime/Scripts/MCPToolHandler.cs` - MCP method routing and tool definitions
- `Runtime/Scripts/MCPResourceProvider.cs` - `unity://` resource routing
- `Runtime/Agent/EmbeddedAgentRuntime.cs` - embedded Microsoft Agent Framework host
- `Runtime/Agent/AgentRuntimeConfig.cs` - embedded agent configuration asset
- `Editor/Agent/AgentRuntimeConfigEditor.cs` - custom inspector for local/downstream MCP configuration
- `Editor/Agent/EmbeddedAgentRuntimeEditor.cs` - runtime status, controls, and inspector-side test prompt

## Embedded Agent Notes

- The embedded agent connects to the local LiveLink MCP server by default.
- The built-in local connection should normally use `StreamableHttp`.
- If an old asset still points local LiveLink to `Sse`, runtime code may normalize that to the modern `/mcp` path.
- External MCP servers may use HTTP (`AutoDetect` / `StreamableHttp` / `Sse`) or stdio, depending on platform.
- Stdio support is intended for the Unity Editor and desktop players.
- `EmbeddedAgentRuntime` exposes public UnityEvents for UI integration: `OnResponseReceived`, `OnError`, `OnStatusChanged`, and `OnToolCall(toolName, jsonParameters)`.

## When Editing MCP Tools

1. Update the tool schema in `MCPToolHandler.HandleListTools()`.
2. Update the corresponding case in `MCPToolHandler.HandleCallToolAsync()`.
3. If Unity APIs are involved, dispatch work to the main thread.
4. Keep the tool result/error shape MCP-compatible.
5. Update `README.md` and, when relevant, `Documentation~/Embedded-Agent-Framework-MVP.md`.

## When Editing Embedded Agent Behavior

1. Keep the embedded agent on MCP contracts wherever possible.
2. Prefer improving LiveLink MCP over introducing embedded-agent-only Unity capabilities.
3. Keep Editor UX in sync:
   - `AgentRuntimeConfigEditor`
   - `EmbeddedAgentRuntimeEditor`
4. If you add new runtime dependencies, make sure the package still imports cleanly in Unity:
   - vendored DLL placement
   - `.asmdef` references
   - `link.xml`

## Documentation Expectations

When behavior changes, keep these files aligned:

- `README.md`
- `CHANGELOG.md`
- `Documentation~/Embedded-Agent-Framework-MVP.md`
- `.github/copilot-instructions.md`

If the change affects embedded-agent setup or MCP transport behavior, update all four.
