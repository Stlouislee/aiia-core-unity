# Embedded Agent Framework MVP

## Goal

Embed Microsoft Agent Framework inside the Unity application while treating the agent as an MCP client.

The product model is:

- LiveLink remains a first-party MCP server that exposes Unity capabilities.
- The embedded agent connects to the local LiveLink MCP server by default.
- Users may configure additional external MCP servers for the embedded agent to use.
- External MCP servers are not re-hosted or bridged through LiveLink MCP.
- External programs may still call the LiveLink MCP server directly.

The shipped product remains a single Unity application. There is no separately deployed agent host.

## Decision Summary

### Canonical first-party surface

LiveLink MCP is the first-party capability surface of the Unity application.

That means:

- Unity-native tools and resources are exposed through LiveLink MCP.
- The embedded agent always has LiveLink MCP available by default.
- External programs can call LiveLink MCP directly.

If a Unity capability should be available both to the embedded agent and to outside consumers, it should be added to LiveLink MCP.

### External MCP servers are downstream dependencies

Other MCP servers are not part of the LiveLink MCP surface.

Instead:

- users configure them for the embedded agent
- the embedded agent connects to them as additional MCP clients
- their capabilities are available to the agent only
- they are not proxied back through LiveLink MCP

This keeps LiveLink focused on Unity-native capabilities while still allowing the agent to use external ecosystems.

### Why this is better than bridging external MCP

Bridging external MCP back through LiveLink would add:

- another translation layer
- name collision and namespacing work
- proxy reliability concerns
- extra policy and logging complexity
- ambiguity about what is truly a LiveLink capability versus a downstream dependency

For this product, a simpler rule is better:

- first-party Unity capabilities live in LiveLink MCP
- third-party capabilities stay in their own MCP servers
- the embedded agent can use both

## Target Architecture

```text
+--------------------------------------------------------------+
|                        Unity Application                     |
|                                                              |
|  +-------------------------+                                 |
|  | Embedded Agent Runtime  |                                 |
|  | - Agent Framework       |                                 |
|  | - session state         |                                 |
|  | - tool approval         |                                 |
|  +-----------+-------------+                                 |
|              |                                               |
|              | MCP clients                                   |
|              |                                               |
|   +----------+-----------+-------------------------------+    |
|   |                      |                               |    |
|   v                      v                               v    |
|  local LiveLink MCP   external MCP A                 external |
|  server (default)     (user configured)              MCP B    |
|                                                              |
|  +--------------------------------------------------------+  |
|  | LiveLink MCP Server                                    |  |
|  | - scene resources                                      |  |
|  | - Unity tools                                          |  |
|  | - prompts                                              |  |
|  +-----------------------+--------------------------------+  |
|                          |                                   |
|                    Unity main thread                         |
|                          |                                   |
|        +-----------------v------------------+                |
|        | LiveLinkManager and related code   |                |
|        | - scene scan                       |                |
|        | - object mutate                    |                |
|        | - event tracking                   |                |
|        +------------------------------------+                |
|                                                              |
|  external programs --------------------------------------->  |
|                    call LiveLink MCP directly                |
+--------------------------------------------------------------+
```

## Core Principles

### 1. The agent is an MCP client

The embedded agent should consume tools and resources through MCP rather than private in-process adapters for production behavior.

For the MVP, the default MCP dependency is the local LiveLink MCP server already provided by this package.

### 2. LiveLink MCP is always on by default for the agent

The embedded agent should always have access to the local LiveLink MCP server unless the user explicitly disables it.

This ensures scene QA and Unity tool use work out of the box.

### 3. External MCP servers are optional and user-configured

External MCP servers should be additive.

Users choose whether to attach them to the embedded agent, and those connections should be configured through Unity Editor UI.

### 4. No external MCP bridging

LiveLink should not proxy, namespace, or re-expose third-party MCP servers.

If a capability belongs to LiveLink, expose it in LiveLink MCP.
If a capability belongs to another server, let the agent connect to that server directly.

### 5. Annotation-based first-party extension

Unity app capabilities can be added to LiveLink MCP through annotation-based dynamic tool discovery.

- Developers mark static methods with `LiveLinkToolAttribute`.
- LiveLink discovers and lists those methods as MCP tools.
- Calls are executed through a shared invoker with argument binding and optional main-thread dispatch.
- Exposure is policy-controlled for embedded agent and external MCP clients independently.

This keeps first-party extensibility inside LiveLink MCP contracts while removing hardcoded tool bottlenecks.

## What This Means For The Current Package

The package already has the foundation needed for the local default server:

- `LiveLinkManager` can host the MCP server.
- `MCPToolHandler` exposes Unity tools.
- `MCPResourceProvider` exposes scene resources.
- `MCPHttpServer` already provides the built-in MCP HTTP transport on `/mcp`, with legacy `/sse` compatibility for older clients.

The new work is not to replace that surface, but to consume it from inside the same Unity app through an embedded MCP client.

This also means the internal command surface should continue to move toward MCP parity. If the embedded agent needs a first-party Unity capability, that capability should be added to LiveLink MCP instead of hidden behind a separate agent-only path.

Dynamic annotation-based tools now provide an additional path for first-party and third-party Unity code to participate in LiveLink MCP without editing legacy switch statements.

## In-App Runtime Components

### Dynamic MCP Tool Bridge

Implemented under `Runtime/Scripts/Tools/`.

Key pieces:

- `LiveLinkToolContracts.cs`: attributes, descriptors, visibility model, exposure policy model
- `LiveLinkToolRegistry.cs`: assembly scanning and descriptor creation
- `LiveLinkToolInvoker.cs`: argument binding, optional main-thread invocation, async support
- `LiveLinkMcpRequestContext.cs`: per-request consumer context (`EmbeddedAgent` vs `External`)

MCP flow integration:

- `MCPToolHandler.tools/list`: appends discovered tools that pass current exposure policy
- `MCPToolHandler.tools/call`: invokes discovered tools before legacy fallback routing
- `MCPHttpServer`: maps request header `X-LiveLink-Consumer` into context for policy decisions
- `AgentMcpClientFactory`: local embedded-agent MCP connection sets `X-LiveLink-Consumer: embedded-agent`

Policy surface:

- configured in `LiveLinkManager` and `LiveLinkManagerEditor`
- supports per-consumer exposure toggles, mutation gates, allow/deny lists, and category/tag filters

### EmbeddedAgentRuntime

Implemented as `Runtime/Agent/EmbeddedAgentRuntime.cs`.

Responsibilities:

- boot Microsoft Agent Framework
- create and manage agent sessions
- receive user messages from UI or gameplay systems
- connect to the local LiveLink MCP server
- connect to any user-configured external MCP servers
- stream model output and tool usage back to UI

Runtime UI event surface:

- `OnResponseReceived` (`UnityEvent<string>`) - final response text
- `OnError` (`UnityEvent<string>`) - initialization/request failures
- `OnStatusChanged` (`UnityEvent<string>`) - lifecycle status updates
- `OnToolCall` (`UnityEvent<string, string>`) - tool name and serialized JSON arguments for per-tool UI feedback

### AgentRuntimeConfig

Implemented as `Runtime/Agent/AgentRuntimeConfig.cs`.

Defines:

- model and API key settings
- embedded agent instructions
- local LiveLink MCP connection settings
- whether first-party scene mutation tools are enabled
- persistent file-based chat history settings (conversation id, storage path, limits)
- the list of downstream MCP servers

The config asset also exposes public getters so UI code can read active settings (for example selected model) without reflection.

### AgentExternalMcpServerConfig

Implemented as `Runtime/Agent/AgentExternalMcpServerConfig.cs`.

Defines one downstream MCP server entry, including:

- HTTP or stdio transport
- endpoint or command
- headers or environment variables
- enable flag
- optional tool allow list

### Local LiveLink MCP client

Typical endpoint:

- `http://127.0.0.1:{mcpPort}/mcp`

This local connection gives the agent access to:

- scene QA resources
- Unity scene manipulation tools
- LiveLink prompts

In the current implementation this connection is created inside `EmbeddedAgentRuntime` with `AgentMcpClientFactory`.

### Downstream MCP connection management

Implemented inside `EmbeddedAgentRuntime` and `Runtime/Agent/AgentMcpClientFactory.cs`.

Responsibilities:

- create and maintain MCP client connections
- enable or disable configured servers
- merge discovered tools into the agent's available toolset
- handle connection errors and degraded states cleanly

This manager does not proxy those servers through LiveLink MCP.

## Editor Configuration Requirements

The product needs Editor UI for agent configuration.

At minimum, the Editor should allow users to:

- enable or disable the embedded agent
- enable or disable the local LiveLink MCP connection for the agent
- add external MCP servers
- edit server name, transport type, endpoint, and auth settings
- choose which external servers are enabled by default
- test connectivity
- review discovered tools

Suggested configuration model:

- a top-level `AgentRuntimeConfig` asset
- a reorderable list of external MCP server entries
- per-entry enable toggle and transport settings

Current implementation:

- `Editor/AgentRuntimeConfigEditor.cs`
- `Editor/EmbeddedAgentRuntimeEditor.cs`
- `Runtime/Agent/AgentMcpConnectionTester.cs`
- `LiveLink/Create Agent Runtime Config`
- `LiveLink/Create Embedded Agent Runtime`

Suggested external server fields:

- display name
- enabled
- transport type
- endpoint or command
- environment variables or headers
- optional notes

## AgentRuntimeConfig Reference

The `AgentRuntimeConfig` asset is the main configuration entry point for the embedded agent.

Create it from:

- `LiveLink/Create Agent Runtime Config`

### Core fields

- `Agent Name` - logical name used for the embedded agent instance
- `OpenAI Model` - model identifier used for chat and tool planning
- `Prefer Environment API Key` - if enabled, resolve the API key from an environment variable first
- `API Key Environment Variable` - default is `OPENAI_API_KEY`
- `Fallback API Key` - local fallback when no environment variable is available
- `System Instructions` - baseline behavior prompt for the embedded agent

### Local LiveLink MCP fields

- `Enable Local LiveLink MCP` - whether the embedded agent connects to the package's own MCP server
- `Auto Start Local MCP` - start the local MCP server automatically if the `LiveLinkManager` has not started it yet
- `HTTP Transport Mode` - `StreamableHttp` is the recommended mode for the built-in LiveLink MCP server; `Sse` remains only for legacy compatibility and `AutoDetect` is acceptable when needed
- `Connection Timeout (Seconds)` - timeout for the local MCP client connection
- `Allow Scene Mutation Tools` - enable or disable first-party write tools

### Chat history persistence fields

- `Enable Persistent Chat History` - enable durable file-backed chat history
- `Conversation ID` - logical history stream identifier used across play sessions
- `Storage Subdirectory` - relative path under `Application.persistentDataPath`
- `Max Persisted Messages` - cap for retained and restored messages
- `Max File Size (Bytes)` - file-size warning threshold for operational monitoring

### Downstream MCP server fields

Each downstream MCP server entry supports:

- `Display Name`
- `Enabled`
- `Transport Type`
- `Connection Timeout (Seconds)`
- `Headers` for HTTP servers
- `Command`, `Arguments`, `Working Directory`, and `Environment Variables` for stdio servers
- `Use Tool Allow List`
- `Allowed Tools`

### Example config

An MVP-friendly config looks like this:

```text
Agent Name: LiveLink Agent
OpenAI Model: gpt-4o-mini
Prefer Environment API Key: true
API Key Environment Variable: OPENAI_API_KEY
Fallback API Key: <empty>
System Instructions: You are a Unity scene assistant. Inspect the scene through MCP before answering.

Enable Local LiveLink MCP: true
Auto Start Local MCP: true
HTTP Transport Mode: StreamableHttp
Connection Timeout (Seconds): 15
Allow Scene Mutation Tools: true

External MCP Servers:
  - Display Name: Docs MCP
    Enabled: true
    Transport Type: Http
    Endpoint: https://example.com/mcp
HTTP Transport Mode: StreamableHttp
    Connection Timeout (Seconds): 30
    Headers:
      - Authorization: Bearer <token>

  - Display Name: Filesystem MCP
    Enabled: false
    Transport Type: Stdio
    Command: npx
    Arguments:
      - -y
      - @modelcontextprotocol/server-filesystem
      - C:/project
    Working Directory: C:/project
```

### Recommended usage notes

- For team or CI setups, prefer `OPENAI_API_KEY` over storing secrets in the asset.
- For read-only QA, disable `Allow Scene Mutation Tools`.
- For downstream MCP servers with broad toolsets, turn on `Use Tool Allow List` to keep the agent focused.
- Treat stdio MCP servers as Editor/desktop functionality unless you explicitly control the target platform.

## Package Import Notes

To keep Unity package import predictable:

- Agent Framework and MCP client dependencies are vendored in `Runtime/Plugins/AgentFramework`
- those DLLs are explicitly referenced by `LiveLink.AgentRuntime`
- the package includes `Runtime/link.xml` to reduce stripping issues on IL2CPP builds
- stdio MCP servers should be treated as Editor/standalone-desktop functionality

## Capability Model

### First-party capabilities

These are capabilities exposed by LiveLink MCP and owned by this package.

Examples:

- scene information
- hierarchy inspection
- GameObject and component inspection
- recent scene events
- object spawn, transform, delete
- future scene-editing operations such as rename, set parent, and set active

These capabilities should be available to:

- the embedded agent through the local LiveLink MCP client
- external programs through direct LiveLink MCP calls

### Downstream capabilities

These are capabilities exposed by external MCP servers configured by the user.

Examples:

- documentation lookup
- internal knowledge tools
- asset management integrations
- issue tracker integrations

These capabilities should be available to:

- the embedded agent, if that server is configured and enabled

They should not be automatically available to:

- external consumers of LiveLink MCP

## Request Flows

### Flow A: Scene QA

1. user asks a question in the Unity UI
2. EmbeddedAgentRuntime sends the message into Agent Framework
3. agent calls the local LiveLink MCP server
4. LiveLink MCP returns scene resources
5. agent answers using first-party Unity context

### Flow B: Unity tool execution

1. user asks the agent to modify the scene
2. agent selects a tool from the local LiveLink MCP server
3. agent calls `tools/call`
4. LiveLink MCP routes the request to the Unity implementation
5. result returns through MCP
6. agent explains the outcome

### Flow C: External MCP usage

1. user asks a question or action that benefits from an external MCP server
2. agent selects a tool from one of the configured external MCP clients
3. that downstream MCP server handles the request
4. result returns to the embedded agent
5. agent responds

LiveLink MCP is not used as a proxy in this flow.

## MVP Scope

The MVP should support:

- multi-turn QA about the active Unity scene
- tool-based scene operations through the local LiveLink MCP server
- connection to selected external MCP servers configured in the Editor
- one embedded agent runtime inside the Unity app
- a clear distinction between first-party LiveLink MCP capabilities and optional downstream MCP capabilities

The MVP should not require:

- a separately deployed agent host
- direct function-tool adapters for Unity features
- external MCP proxying through LiveLink MCP

## Recommended MVP Milestones

### Milestone 1: Embedded agent over local LiveLink MCP

Deliver:

- EmbeddedAgentRuntime in Unity
- local LiveLink MCP client connection
- Agent Framework session and chat flow
- QA over existing scene resources
- tool execution over existing LiveLink MCP tools

Outcome:

The Unity app can answer scene questions and call current LiveLink MCP tools from inside the same app.

### Milestone 2: MCP surface parity for required first-party actions

Deliver:

- expose missing scene-editing capabilities through LiveLink MCP
- add missing tool schemas and routing
- improve resource responses where agent workflows need stable identifiers

Outcome:

Everything the embedded agent needs from first-party Unity behavior is available through LiveLink MCP.

### Milestone 3: External MCP configuration and connection UI

Deliver:

- `AgentRuntimeConfig` asset and inspector
- external MCP server list
- connection lifecycle management
- enable and disable controls
- connectivity test and tool discovery preview

Outcome:

Users can compose their own downstream MCP setup for the embedded agent without changing code.

### Milestone 4: Guardrails and productization

Deliver:

- approval policy for destructive tools
- audit logging of tool calls
- timeout and retry policy
- fallback UX when local or external MCP is unavailable
- platform validation for target desktop builds

Outcome:

The Unity app is shippable as a single product with predictable behavior.

## Required Package Changes

### 1. Add embedded agent runtime classes

Likely new runtime pieces:

- `EmbeddedAgentRuntime`
- `AgentSessionController`
- `LiveLinkMcpClientFactory`
- `ExternalMcpConnectionManager`
- `AgentRuntimeConfig`

### 2. Add Editor UI for downstream MCP configuration

Likely new editor pieces:

- `AgentRuntimeConfigEditor`
- validation helpers for transport-specific fields
- connectivity test actions
- tool discovery preview UI

### 3. Promote required first-party Unity actions into LiveLink MCP

If the agent needs a first-party Unity capability, add it to LiveLink MCP rather than wiring around MCP.

This includes likely additions such as:

- rename object
- set parent
- set active
- other scene-editing operations needed by agent workflows

### 4. Keep first-party and downstream responsibilities separate

The package should clearly distinguish:

- LiveLink-owned capabilities that belong in the local MCP server
- user-attached downstream capabilities that belong in external MCP servers

## Runtime and Platform Notes

### Single application delivery

This design still ships as one Unity application. The embedded agent runtime and the local LiveLink MCP server are inside the same delivered app.

### Local loopback transport

For the MVP, use local MCP HTTP loopback on `/mcp` to consume LiveLink MCP from inside the same app. Legacy `/sse` remains available only for compatibility with older clients. This keeps the embedded agent on the same MCP contract used by external programs.

### Desktop-first recommendation

The MVP should be validated on desktop targets first. Dependency packaging, outbound connectivity, and optional downstream MCP transports are easiest to control there.

## Risks and Mitigations

### Risk: first-party features bypass LiveLink MCP

If the embedded agent starts using private adapters for Unity behavior, the contract will diverge.

Mitigation:

- require first-party agent-exposed capabilities to land in LiveLink MCP

### Risk: downstream MCP configuration becomes too opaque

Users need to understand what extra servers are attached to the agent.

Mitigation:

- add explicit Editor UI
- show enabled state and discovered tools
- keep downstream configuration separate from LiveLink MCP settings

### Risk: local MCP session lifecycle affects in-app reliability

The local LiveLink MCP connection should prefer the built-in `/mcp` endpoint and Streamable HTTP semantics. Legacy SSE session handling remains a compatibility path, not the primary embedded-agent path.

Mitigation:

- implement reconnect and keepalive in the local MCP client layer
- treat it as package-managed infrastructure

### Risk: downstream MCP failures reduce agent reliability

Mitigation:

- isolate failures per configured server
- mark unavailable servers as degraded
- keep first-party LiveLink MCP capabilities usable even when downstream servers fail

## Non-Goals For This MVP

- redesigning the core LiveLink MCP protocol
- building a separate standalone agent host
- proxying third-party MCP servers through LiveLink MCP
- making every downstream MCP capability visible to external LiveLink consumers

## Summary

The package should embed Microsoft Agent Framework inside the Unity app, with the agent acting as an MCP client.

The embedded agent should connect to:

- the local LiveLink MCP server by default
- zero or more user-configured downstream MCP servers

LiveLink MCP remains the first-party Unity capability surface and stays directly callable by external programs.

External MCP servers remain optional downstream dependencies for the embedded agent and should be configured in the Editor, not bridged back through LiveLink MCP.
