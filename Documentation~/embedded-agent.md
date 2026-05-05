# Embedded Agent Runtime — Technical Documentation

> **Package:** aiia-core-unity (LiveLink)  
> **Last updated:** 2026-05-05 (updated: aligned with Microsoft Agent Framework, added chat window)  
> **Audience:** Unity developers integrating AI agents into their applications

---

## Table of Contents

1. [Overview & Architecture](#1-overview--architecture)
2. [AgentRuntimeConfig Asset Reference](#2-agentruntimeconfig-asset-reference)
3. [Step-by-Step Setup Guide](#3-step-by-step-setup-guide)
4. [Downstream MCP Server Configuration](#4-downstream-mcp-server-configuration)
5. [Persistent Chat History System](#5-persistent-chat-history-system)
6. [UI Events Reference](#6-ui-events-reference)
7. [A2A (Agent-to-Agent) Protocol](#7-a2a-agent-to-agent-protocol)
8. [Security Considerations](#8-security-considerations)
9. [Design Issues Found in the Codebase](#9-design-issues-found-in-the-codebase)
10. [Refactoring Suggestions](#10-refactoring-suggestions)

---

## 1. Overview & Architecture

### What It Is

The **Embedded Agent Runtime** is a Unity component (`EmbeddedAgentRuntime`) that hosts a Microsoft Agent Framework AI agent directly inside a Unity application. It connects the agent to:

1. The **first-party LiveLink MCP server** (scene inspection and editing tools)
2. Any number of **downstream MCP servers** (user-configured HTTP or Stdio endpoints)

The agent runs as an asynchronous background task, communicates with the OpenAI chat API, and uses MCP tool-calling to interact with Unity scene data. It exposes UnityEvents so UI code can observe agent responses, errors, status changes, and tool invocations without modifying package internals.

### Architecture Diagram

```
┌──────────────────────────────────────────────────────────────────┐
│                     Unity Application                            │
│                                                                  │
│  ┌─────────────────────────────────────────────────────────────┐ │
│  │              EmbeddedAgentRuntime (MonoBehaviour)            │ │
│  │                                                             │ │
│  │  AgentRuntimeConfig (ScriptableObject)                      │ │
│  │    ├── OpenAI model / API key                               │ │
│  │    ├── System instructions                                  │ │
│  │    ├── Local MCP settings                                   │ │
│  │    ├── Persistent chat history settings                     │ │
│  │    └── Downstream MCP server list                           │ │
│  │                                                             │ │
│  │  ┌──────────────┐   ┌──────────────────────────────────┐   │ │
│  │  │  OpenAI SDK  │   │        MCP Client Layer          │   │ │
│  │  │  (Chat API)  │   │                                  │   │ │
│  │  └──────┬───────┘   │  ┌────────────┐ ┌─────────────┐ │   │ │
│  │         │           │  │ Local MCP  │ │ External MCP│ │   │ │
│  │         │           │  │ (LiveLink) │ │ (HTTP/Stdio)│ │   │ │
│  │         │           │  └─────┬──────┘ └──────┬──────┘ │   │ │
│  │         │           └────────┼───────────────┼────────┘   │ │
│  │         │                    │               │            │ │
│  │  ┌──────┴────────────────────┴───────────────┴──────────┐ │ │
│  │  │              Microsoft Agent Framework               │ │ │
│  │  │         AIAgent / AgentSession / AITool              │ │ │
│  │  └──────────────────────────────────────────────────────┘ │ │
│  │                                                             │ │
│  │  Events: OnResponseReceived · OnError · OnStatusChanged    │ │
│  │          OnToolCall                                        │ │
│  └─────────────────────────────────────────────────────────────┘ │
│                                                                  │
│  ┌───────────────────────────┐  ┌──────────────────────────────┐ │
│  │   LiveLinkManager         │  │  External MCP Servers        │ │
│  │   (Scene MCP Server)      │  │  (User-configured)           │ │
│  └───────────────────────────┘  └──────────────────────────────┘ │
└──────────────────────────────────────────────────────────────────┘
```

### Key Classes

| Class | Role |
|---|---|
| `EmbeddedAgentRuntime` | MonoBehaviour that owns the lifecycle: initialization, message dispatch, shutdown, event emission |
| `AgentRuntimeConfig` | ScriptableObject holding all configuration: model, API key, MCP servers, chat history |
| `AgentExternalMcpServerConfig` | Serializable DTO for a single downstream MCP server (HTTP or Stdio) |
| `AgentMcpClientFactory` | Static factory that creates `McpClient` instances for local and external servers |
| `AgentMcpConnectionTester` | Editor-safe wrapper for validating downstream MCP connections |
| `FileChatHistoryProvider` | `ChatHistoryProvider` implementation that persists/restores conversations to disk |
| `AgentToolNames` | Registry of first-party scene mutation tool names, used for policy gating |
| `AgentNamedValue` | Serializable key/value pair for headers and environment variables |

### Lifecycle Flow

```
Start()
  └─► InitializeAsync()
        ├── Resolve API key from environment or asset
        ├── Resolve LiveLinkManager reference (scene search or serialized)
        ├── Connect local LiveLink MCP (if enabled)
        ├── Connect each enabled downstream MCP server
        ├── Discover & filter tools from all connected servers
        ├── Build system instructions
        ├── Create OpenAI ChatClient → AIAgent
        ├── Create FileChatHistoryProvider (if persistence enabled)
        ├── Create AgentSession
        └── Set status: "Ready"

RunAsync(message)                                              ← Primary API
  ├── Auto-initialize if not already done
  ├── Acquire run lock (serialized execution)
  ├── Run agent → collect AgentResponse
  │     ├── Messages[] — all intermediate steps (tool calls, results, text)
  │     ├── Usage — token counts (input, output)
  │     └── FinishReason — Stop, ToolCalls, Length, etc.
  ├── Fire OnResponseReceived (text convenience)
  └── Release run lock

RunStreamingAsync(message)                                     ← Streaming API
  ├── Auto-initialize if not already done
  ├── Acquire run lock
  ├── Stream AgentResponseUpdate chunks
  │     ├── TextContent — text deltas
  │     ├── FunctionCallContent — tool calls (name, args, callId)
  │     └── FunctionResultContent — tool results
  ├── Fire OnToolCall events per FunctionCallContent
  ├── Fire OnResponseReceived when complete
  └── Release run lock

SendMessageAsync(message)                                      ← Convenience (backward compat)
  └── Calls RunAsync → returns response.Text

ShutdownAsync()
  ├── Dispose all MCP client connections
  ├── Clear agent and session references
  └── Set status: "Stopped"
```

---

## 2. AgentRuntimeConfig Asset Reference

Create a config asset from the Unity menu: **LiveLink > Create Agent Runtime Config**.

### OpenAI Chat Backend

| Field | Type | Default | Description |
|---|---|---|---|
| `Agent Name` | `string` | `"LiveLink Agent"` | Logical name for the agent in the Agent Framework. Visible in logs and the agent's self-identification. |
| `OpenAI Model` | `string` | `"gpt-4o-mini"` | OpenAI model identifier used for reasoning and tool selection. Recommended: `gpt-4o-mini` for cost efficiency, `gpt-4o` for complex scene reasoning. |
| `Prefer Environment API Key` | `bool` | `true` | When enabled, the runtime reads the API key from an environment variable first, falling back to the asset-stored key only if the env var is empty. **Strongly recommended to keep enabled.** |
| `API Key Environment Variable` | `string` | `"OPENAI_API_KEY"` | Name of the environment variable to read. |
| `Fallback API Key` | `string` | `""` | API key stored directly in the asset. Only used when `Prefer Environment API Key` is on and the env var is missing. **Avoid committing this value to version control.** |

### Agent Behavior

| Field | Type | Default | Description |
|---|---|---|---|
| `System Instructions` | `string` (TextArea) | Pre-written Unity assistant prompt | Injected as the system message. Steers the agent's behavior. Best practice: instruct the agent to inspect the scene via MCP before answering. |
| `Enable Local LiveLink MCP` | `bool` | `true` | Whether the agent connects to the built-in LiveLink MCP server for scene tools. |
| `Auto Start Local LiveLink MCP` | `bool` | `true` | If the local MCP server isn't running when the agent initializes, start it automatically. |
| `Local HTTP Transport Mode` | `AgentMcpHttpTransportMode` | `StreamableHttp` | Transport for the local MCP connection. `StreamableHttp` is recommended; `Sse` is legacy-only. `AutoDetect` lets the SDK negotiate. |
| `Local Connection Timeout Seconds` | `float` | `15` | Timeout for the local MCP connection handshake. In the Editor, this is clamped to 3–5 seconds for faster iteration. |
| `Allow Scene Mutation Tools` | `bool` | `true` | Gates write operations (`spawn_object`, `transform_object`, `delete_object`, `rename_object`, `set_parent`, `set_active`). Disable for read-only QA agents. |

### Chat History Persistence

| Field | Type | Default | Description |
|---|---|---|---|
| `Enable Persistent Chat History` | `bool` | `false` | When enabled, chat history is saved to local files and restored across play sessions and restarts. |
| `Conversation ID` | `string` | `"default"` | Selects which history stream to resume. Use different IDs for separate user profiles or conversation contexts. |
| `Storage Subdirectory` | `string` | `"LiveLink/AgentHistory"` | Relative path under `Application.persistentDataPath`. Forward slashes are normalized. |
| `Max Persisted Messages` | `int` | `200` (min: 10) | Caps the number of messages restored from disk. Oldest entries are trimmed first. |
| `Max File Size (Bytes)` | `int` | `1048576` (1 MB, min: 16384) | Warning threshold. If the history file exceeds this, a warning is logged. Not a hard enforcement—trimming is by message count. |

### Downstream MCP Servers

| Field | Type | Description |
|---|---|---|
| `External MCP Servers` | `List<AgentExternalMcpServerConfig>` | Array of downstream MCP server configurations. Each entry is independently enabled/disabled. |

Each `AgentExternalMcpServerConfig` entry has:

| Field | Type | Default | Description |
|---|---|---|---|
| `Enabled` | `bool` | `true` | Toggle for this server without removing the config. |
| `Display Name` | `string` | `"External MCP Server"` | Human-readable label shown in logs and the inspector. |
| `Transport Type` | `AgentMcpTransportType` | `Http` | `Http` or `Stdio`. |
| `Endpoint` | `string` | `""` | URL for HTTP MCP servers (e.g., `http://localhost:9000/mcp`). |
| `HTTP Transport Mode` | `AgentMcpHttpTransportMode` | `AutoDetect` | `StreamableHttp`, `Sse`, or `AutoDetect`. |
| `Connection Timeout (Seconds)` | `float` | `30` | Handshake timeout. |
| `Headers` | `List<AgentNamedValue>` | `[]` | HTTP headers (e.g., `Authorization: Bearer ...`). |
| `Command` | `string` | `""` | Executable path for Stdio servers. |
| `Arguments` | `List<string>` | `[]` | Command-line arguments for the Stdio process. |
| `Working Directory` | `string` | `""` | Process working directory for Stdio servers. |
| `Environment Variables` | `List<AgentNamedValue>` | `[]` | Environment entries for the Stdio process. |
| `Use Tool Allow List` | `bool` | `false` | When enabled, only tools listed in `Allowed Tools` are exposed from this server. |
| `Allowed Tools` | `List<string>` | `[]` | Tool names to expose when `Use Tool Allow List` is true. Case-insensitive matching. |

---

## 3. Step-by-Step Setup Guide

### Prerequisites

- Unity 2020.3 LTS or newer
- An OpenAI API key
- A scene with a `LiveLinkManager` component (create via **LiveLink > Create Manager**)

### Step 1: Create the Config Asset

1. In the Unity menu, go to **LiveLink > Create Agent Runtime Config**.
2. Name the asset (default: `LiveLinkAgentRuntimeConfig.asset`) and save it in your project.
3. Select the asset in the Project window to configure it in the Inspector.

### Step 2: Configure API Access

1. Set your `OPENAI_API_KEY` environment variable (recommended) or paste the key into the `Fallback API Key` field.
2. Choose your model. `gpt-4o-mini` is the default and cost-effective; `gpt-4o` provides stronger reasoning for complex scene analysis.

### Step 3: Configure System Instructions

Edit the `System Instructions` field. The default prompt tells the agent to inspect the scene before answering. You can customize it to:

- Restrict the agent to specific scene areas
- Define personality or tone
- Add domain-specific knowledge

### Step 4: Create the Runtime GameObject

1. In the Unity menu, go to **LiveLink > Create Embedded Agent Runtime**.
2. This creates a GameObject named "LiveLink Embedded Agent" with the `EmbeddedAgentRuntime` component.
3. In the Inspector, assign your `AgentRuntimeConfig` asset to the `Config` field.
4. If you have a `LiveLinkManager` in the scene, assign it to the `LiveLinkManager` field. If left empty, the runtime will search for one automatically.

### Step 5: Configure Behavior Toggles

| Toggle | When to enable |
|---|---|
| `Auto Initialize` | Agent starts automatically when Play Mode begins. Disable if you want manual control. |
| `Create Session On Initialize` | Agent session is created during initialization. Disable if you want to create it later. |
| `Persist Across Scenes` | Runtime survives scene loads via `DontDestroyOnLoad`. Enable for persistent agents. |

### Step 6: Wire UI Events (Optional)

In the Inspector's Events section, bind listeners to:

- `OnResponseReceived` — display agent text output
- `OnError` — show error messages
- `OnStatusChanged` — update a status indicator
- `OnToolCall` — log or visualize tool invocations

### Step 7: Enter Play Mode

If `Auto Initialize` is enabled, the agent will:

1. Connect to the local LiveLink MCP server
2. Connect to any enabled downstream MCP servers
3. Discover available tools
4. Create the agent and session
5. Set status to "Ready"

You can also use the Inspector controls at runtime:

- **Initialize** — start the runtime
- **Reinitialize** — shut down and restart
- **Reset Session** — create a new agent session (clears in-memory context)
- **Run Suggested Test** — send a predefined prompt to verify the agent is working

---

## 4. Downstream MCP Server Configuration

Downstream MCP servers extend the agent's capabilities beyond scene tools. They are configured per-server in the `AgentRuntimeConfig` asset and are available **only to the embedded agent** — they are not re-exposed through the LiveLink MCP server.

### HTTP MCP Servers

For servers accessible over the network:

```
Display Name:    Docs MCP Server
Transport Type:  Http
Endpoint:        http://localhost:9000/mcp
HTTP Transport Mode: StreamableHttp
Connection Timeout:  30
Headers:
  - Name: Authorization
    Value: Bearer sk-xxxx
```

**Transport mode guidelines:**

| Mode | When to use |
|---|---|
| `StreamableHttp` | Modern MCP servers. Default and recommended. |
| `Sse` | Legacy servers that only support Server-Sent Events. |
| `AutoDetect` | Let the MCP C# SDK negotiate automatically. |

### Stdio MCP Servers

For local processes that communicate via stdin/stdout:

```
Display Name:    Filesystem MCP
Transport Type:  Stdio
Command:         npx
Arguments:       -y, @modelcontextprotocol/server-filesystem, /path/to/allowed/dir
Working Directory: (optional)
Environment Variables:
  - Name: NODE_ENV
    Value: production
```

**Platform support:**

| Platform | Supported |
|---|---|
| Unity Editor | ✅ |
| Windows Standalone | ✅ |
| macOS Standalone | ✅ |
| Linux Standalone | ✅ |
| Android / iOS / WebGL / Consoles | ❌ (throws `PlatformNotSupportedException`) |

### Tool Allow Lists

Each downstream server supports optional tool filtering:

1. Enable `Use Tool Allow List` on the server config.
2. Add tool names to the `Allowed Tools` list.
3. Only matching tools are exposed to the agent. All other tools from that server are silently dropped.

Matching is case-insensitive.

### Testing Connections in the Editor

1. Select your `AgentRuntimeConfig` asset.
2. Expand a downstream server entry.
3. Click **Test Connection**.
4. The editor will attempt to connect, list discovered tools, and display results in a dialog.

This does not require entering Play Mode.

---

## 5. Persistent Chat History System

### How It Works

When `Enable Persistent Chat History` is on, the agent uses a `FileChatHistoryProvider` to:

1. **Restore** history from disk when creating a session
2. **Store** new request/response message pairs after each agent invocation
3. **Trim** history to the configured maximum message count

### Storage Format

History files are stored as JSON under `Application.persistentDataPath`:

```
{persistentDataPath}/
  {StorageSubdirectory}/
    {sha256(conversationId)}.json
```

The file name is a SHA-256 hash of the conversation ID, preventing filesystem issues with special characters.

### Document Schema

```json
{
  "schemaVersion": 1,
  "conversationId": "default",
  "createdUtc": "2026-05-03T10:00:00Z",
  "updatedUtc": "2026-05-03T10:15:00Z",
  "messages": [
    {
      "role": "user",
      "text": "What objects are in the scene?",
      "authorName": null,
      "createdAt": "2026-05-03T10:00:01Z",
      "messageId": "msg_abc123",
      "rawMessageJson": "{\"role\":\"user\",\"text\":\"What objects are in the scene?\"}"
    }
  ]
}
```

Each message stores both a simplified representation (`role`, `text`) and a full `rawMessageJson` for lossless round-tripping. If deserialization of the raw JSON fails, the provider falls back to the simplified fields.

### Atomic Writes

The provider uses a write-to-temp-then-replace strategy:

1. Write to `{file}.tmp`
2. Replace original with `{file}.bak` as backup
3. Move `.tmp` to the final path
4. Clean up `.tmp` if it still exists

This prevents corruption from interrupted writes.

### Corrupt File Recovery

If a history file fails to parse, the provider:

1. Renames it to `{file}.corrupt-{timestamp}`
2. Creates a fresh empty history
3. Logs a warning

### Conversation ID Strategy

| Use case | Conversation ID |
|---|---|
| Single-user app | `"default"` |
| Multi-user app | `"user_{userId}"` |
| Per-feature isolation | `"scene_editor"`, `"qa_bot"` |
| Reset history | Change the ID (old file remains on disk) |

---

## 6. UI Events & Chat Window

### 6.1 Play Mode Chat Window

A built-in editor window lets you chat with the agent interactively during Play Mode.

**Open via:** `LiveLink > Agent Chat` menu, or click **"Open Chat Window"** in the EmbeddedAgentRuntime Inspector.

**Features:**
- Scrollable message area with user/agent/tool-call/tool-result/error entries
- Streaming text display with real-time updates and spinner animation
- Collapsible tool call foldouts showing name, arguments, and call ID
- Collapsible tool result foldouts showing return values
- Per-response usage display: input/output tokens, duration, finish reason
- Enter to send, Shift+Enter for newline
- Stop button to cancel in-flight streaming
- Auto-scroll toggle, usage toggle, clear button

**How it works internally:**
The chat window uses `RunStreamingAsync` to get typed `AgentResponseUpdate` chunks. It inspects each update's `Contents` list for `FunctionCallContent` and `FunctionResultContent` to build the structured message timeline. `UsageContent` items provide token statistics.

### 6.2 Event Reference

All events are `public` fields on `EmbeddedAgentRuntime` and can be wired from the Inspector or subscribed to in code.

### OnResponseReceived

```csharp
public AgentTextEvent OnResponseReceived; // UnityEvent<string>
```

- **Payload:** Final text response from the agent.
- **Thread:** Always dispatched to the Unity main thread.
- **When:** After a successful `SendMessageAsync` call.

**Example:**
```csharp
agentRuntime.OnResponseReceived.AddListener(response => {
    Debug.Log($"Agent says: {response}");
    uiText.text = response;
});
```

### OnError

```csharp
public AgentTextEvent OnError; // UnityEvent<string>
```

- **Payload:** Full exception string (includes stack trace).
- **Thread:** Always dispatched to the Unity main thread.
- **When:** On initialization failure or request failure. Also logged via `Debug.LogError`.

**Example:**
```csharp
agentRuntime.OnError.AddListener(error => {
    Debug.LogError($"Agent error: {error}");
    ShowErrorPanel(error);
});
```

### OnStatusChanged

```csharp
public AgentTextEvent OnStatusChanged; // UnityEvent<string>
```

- **Payload:** Status string describing the current phase.
- **Thread:** Always dispatched to the Unity main thread.
- **When:** On every internal state transition.

**Known status values:**

| Status | Meaning |
|---|---|
| `"Idle"` | Initial state before initialization |
| `"Initializing agent runtime..."` | Initialization started |
| `"Connecting to local LiveLink MCP..."` | Connecting to local MCP server |
| `"Local LiveLink MCP connected."` | Local connection established |
| `"Connecting external MCP: {name}..."` | Connecting to a downstream server |
| `"Connected external MCP: {name}"` | Downstream connection established |
| `"Preparing agent tools..."` | Building the tool list |
| `"Creating OpenAI chat client..."` | Instantiating the OpenAI client |
| `"Creating embedded agent..."` | Creating the Agent Framework agent |
| `"Creating agent session..."` | Creating the agent session |
| `"Ready. Connected N MCP server(s)."` | Initialization complete |
| `"Running agent..."` | Processing a user message |
| `"Response received."` | Message processing complete |
| `"Agent request failed."` | An error occurred during processing |
| `"Agent session reset."` | Session was reset |
| `"Stopped."` | Shutdown complete |

### OnToolCall

```csharp
public AgentToolCallEvent OnToolCall; // UnityEvent<string, string>
```

- **Parameter 1:** Tool name (e.g., `"read_scene_hierarchy"`).
- **Parameter 2:** Serialized JSON parameters (e.g., `{"depth":3}`).
- **Thread:** Always dispatched to the Unity main thread.
- **When:** Before each MCP tool invocation. Fires regardless of tool source (local or external).

**Example:**
```csharp
agentRuntime.OnToolCall.AddListener((toolName, jsonParams) => {
    Debug.Log($"Tool called: {toolName} with {jsonParams}");
    toolLog.Add($"{toolName}({jsonParams})");
});
```

### Subscribing in Code

```csharp
public class AgentUI : MonoBehaviour
{
    [SerializeField] private EmbeddedAgentRuntime _agentRuntime;

    private void OnEnable()
    {
        _agentRuntime.OnResponseReceived.AddListener(HandleResponse);
        _agentRuntime.OnError.AddListener(HandleError);
        _agentRuntime.OnStatusChanged.AddListener(HandleStatus);
        _agentRuntime.OnToolCall.AddListener(HandleToolCall);
    }

    private void OnDisable()
    {
        _agentRuntime.OnResponseReceived.RemoveListener(HandleResponse);
        _agentRuntime.OnError.RemoveListener(HandleError);
        _agentRuntime.OnStatusChanged.RemoveListener(HandleStatus);
        _agentRuntime.OnToolCall.RemoveListener(HandleToolCall);
    }

    private void HandleResponse(string text) { /* update UI */ }
    private void HandleError(string error) { /* show error */ }
    private void HandleStatus(string status) { /* update indicator */ }
    private void HandleToolCall(string name, string args) { /* log */ }
}
```

---

## 7. A2A (Agent-to-Agent) Protocol

### Overview

LiveLink implements the [A2A v1.0 protocol](https://a2a-protocol.org) — an open standard for agent-to-agent communication backed by Google, Microsoft, AWS, IBM, and SAP under the Linux Foundation. A2A complements MCP (tool integration) by handling agent-to-agent delegation and discovery.

The implementation has two sides:

| Component | Purpose |
|-----------|---------|
| **A2A Client** | Discover and delegate tasks to remote A2A agents |
| **A2A Host Server** | Expose the Unity agent as a discoverable A2A endpoint |

### 7.1 A2A Client — Remote Agent Delegation

#### Configuration

Add remote agents in `AgentRuntimeConfig > Remote A2A Agents`:

| Field | Description |
|-------|-------------|
| `Enabled` | Toggle this agent on/off |
| `Display Name` | Human-readable name (becomes tool name: `ask_{name}`) |
| `Endpoint` | Remote agent URL (e.g., `https://openclaw-host`) |
| `Use Agent Card Discovery` | Fetch `/.well-known/agent-card.json` on connect |
| `Connection Timeout` | HTTP timeout in seconds |
| `Headers` | Custom headers (e.g., `Authorization: Bearer ...`) |
| `Enable Streaming` | Use SSE streaming when the remote agent supports it |
| `Delegate Tool Prefix` | Custom tool name prefix (default: `ask_`) |
| `Accept Self-Signed Certs` | Trust untrusted SSL certificates |

#### How It Works

1. On `InitializeAsync`, the runtime fetches each remote agent's card from `/.well-known/agent-card.json`
2. The agent card describes capabilities, skills, and endpoints
3. Each remote agent is wrapped as an `AIFunction` tool (e.g., `ask_openclaw`)
4. The embedded agent can invoke these tools to delegate questions/tasks
5. Remote agent skills are included in the system prompt

#### SSE Streaming & Reconnection

When streaming is enabled and the remote agent supports it, responses arrive as SSE events. The client automatically reconnects on connection drops with exponential backoff (1s → 2s → 4s, max 3 attempts). This handles:

- Android Doze mode / WiFi sleep
- Meta Quest headset standby
- Network switching

#### Platform Notes

| Platform | Status | Notes |
|----------|--------|-------|
| Unity Editor | ✅ Full support | Debug logging enabled |
| Android (IL2CPP) | ✅ Supported | Thread-safe logging, `link.xml` protection |
| Meta Quest | ✅ Supported | SSE reconnection handles WiFi/Doze |
| iOS | ✅ Supported | No special configuration needed |
| WebGL | ⚠️ Limited | SSE streaming may not work due to browser restrictions |

### 7.2 A2A Host Server — Expose Unity Agent

#### Configuration

Configure in `AgentRuntimeConfig > A2A Hosting`:

| Field | Description |
|-------|-------------|
| `Enabled` | Start the A2A host server |
| `Port` | Listen port (default: 8082, MCP uses 8081) |
| `Agent Name` | Name shown in agent card |
| `Agent Description` | Description shown in agent card |
| `Agent Version` | Version string |
| `Enable Streaming` | Support SSE streaming responses |
| `Auth Token` | Optional Bearer token for incoming requests |
| `Rate Limit Per Minute` | Per-IP rate limit (0 = unlimited) |
| `Skills` | List of skills exposed in agent card |

#### Endpoints

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/.well-known/agent-card.json` | Agent discovery (A2A spec) |
| `POST` | `/a2a` | Send message, receive agent response |
| `GET` | `/health` | Health check |
| `OPTIONS` | `*` | CORS preflight |

#### Security

- **Bearer Auth**: Set `Auth Token` in config. Incoming requests must include `Authorization: Bearer <token>`.
- **Rate Limiting**: Per-IP sliding window (1 minute). Exceeding returns 429.
- **CORS**: `Access-Control-Allow-Origin: *` on all responses.

### 7.3 Testing

Run A2A tests with:

```bash
dotnet test Tests~/A2A.Tests/A2A.Tests.csproj
```

Tests cover: protocol types (serialization round-trip), client HTTP behavior (mock), SSE streaming (single + multi-line data), tool wrapper (name sanitization, invocation), host server (routing, auth, streaming), agent card builder, and resilience patterns (retry, reconnection).

---

## 8. Security Considerations

### API Key Management

| Practice | Recommendation |
|---|---|
| Environment variable | **Preferred.** Keep `Prefer Environment API Key` enabled. Set `OPENAI_API_KEY` in the deployment environment. |
| Asset-stored key | Use only as a fallback for local development. Never commit to version control. Add the asset to `.gitignore`. |
| Runtime exposure | The `OpenAIApiKey` property is a public getter on `AgentRuntimeConfig`. Any script with a reference can read it. Consider restricting access if the config asset is shared. |

### Mutation Tool Gating

The `Allow Scene Mutation Tools` flag on `AgentRuntimeConfig` controls whether write-capable MCP tools are exposed to the agent:

**Blocked tools when disabled:**
- `spawn_object`
- `spawn_gltf`
- `transform_object`
- `delete_object`
- `rename_object`
- `set_parent`
- `set_active`

**Use cases:**
- **Read-only QA agents:** Disable mutation to prevent the agent from modifying the scene.
- **Level design assistants:** Enable mutation to allow the agent to spawn and arrange objects.
- **External MCP servers:** Mutation gating applies only to the local LiveLink server. External servers are controlled by their own tool allow lists.

### Downstream MCP Server Trust

- Downstream MCP servers run with the same privileges as the Unity application.
- Stdio servers execute local processes — only configure commands you trust.
- HTTP headers (including auth tokens) are stored in the config asset. Protect the asset accordingly.
- The `X-LiveLink-Consumer: embedded-agent` header is automatically added for the local LiveLink MCP client, allowing server-side policy differentiation.

### Chat History on Disk

- History files are stored in `Application.persistentDataPath` as plain JSON.
- They may contain sensitive conversation data.
- On platforms where users can access the filesystem (desktop), consider encrypting the history directory or disabling persistence for sensitive applications.
- Corrupt files are renamed, not deleted, preserving data for debugging.

### Network Exposure

- The embedded agent makes outbound HTTPS connections to OpenAI and configured MCP endpoints.
- No inbound ports are opened by the agent runtime itself (the LiveLink MCP server opens its own ports separately).
- Ensure firewall rules permit outbound HTTPS to your configured endpoints.

---

## 9. Design Issues Found in the Codebase

### 8.1 Fire-and-Forget Initialization from `Start()`

```csharp
private void Start()
{
    if (_autoInitialize)
    {
        RunBackgroundTask(() => InitializeAsync());
    }
}
```

`RunBackgroundTask` swallows exceptions (they're logged but not rethrown). If initialization fails, the component silently enters a broken state — `_isInitialized` stays `false`, but there's no retry mechanism or clear signal to the caller. The `OnError` event fires, but only if the caller subscribed before `Start()`.

**Impact:** Silent failures in production. No automatic recovery.

### 8.2 `SemaphoreSlim` Not Disposed

`_initializationLock` and `_runLock` are created as `readonly` fields but never disposed. While `SemaphoreSlim` doesn't hold unmanaged resources (unlike `Mutex`), this is technically a resource leak and inconsistent with the `IDisposable` pattern the code follows for MCP clients.

### 8.3 `ShutdownAsync()` in `OnDestroy()` Is Fire-and-Forget

```csharp
private void OnDestroy()
{
    _ = ShutdownAsync();
}
```

`OnDestroy` runs on the main thread during scene teardown. `ShutdownAsync` disposes MCP clients asynchronously. If the Unity application is quitting, the background tasks may not complete, potentially leaving connections open. There's no `Application.quitting` hook or synchronous fallback.

### 8.4 SSE Transport Silently Overridden

```csharp
private AgentMcpHttpTransportMode GetEffectiveLocalTransportMode()
{
    if (_config.LocalHttpTransportMode == AgentMcpHttpTransportMode.Sse)
    {
        Debug.LogWarning("...");
        return AgentMcpHttpTransportMode.StreamableHttp;
    }
    return _config.LocalHttpTransportMode;
}
```

If a user explicitly selects `Sse` in the Inspector, the runtime silently overrides it to `StreamableHttp` with only a log warning. This violates the principle of least surprise — the Inspector shows `Sse` but the runtime uses `StreamableHttp`. Either remove the `Sse` option from the local transport dropdown or honor the user's choice.

### 8.5 `_isInitialized` Not Volatile or Locked

`_isInitialized` is a plain `bool` read from multiple threads (e.g., `SendMessageAsync` checks it, `InitializeAsync` sets it). While the initialization semaphore protects the write path, the read in `SendMessageAsync`:

```csharp
if (!_isInitialized)
{
    await InitializeAsync(cancellationToken).ConfigureAwait(false);
}
```

...is not inside the semaphore. A race between two concurrent `SendMessageAsync` calls could cause double initialization. The `_runLock` protects the message path but not the initialization check itself.

### 8.6 `FindObjectOfType` on Background Thread

```csharp
private async Task ResolveLiveLinkManagerReferenceAsync(CancellationToken cancellationToken)
{
    // ...
    DispatchToMainThread(() =>
    {
        try { tcs.TrySetResult(FindLiveLinkManagerInScene()); }
        catch (Exception ex) { tcs.TrySetException(ex); }
    });
    // ...
}
```

This correctly dispatches to the main thread, but if no `LiveLinkManager` exists, `FindLiveLinkManagerInScene()` returns `null`. The code continues with `_liveLinkManager == null` and later throws a clear exception, but the `TaskCompletionSource` approach is over-engineered for what could be a simple null check with a descriptive error.

### 8.7 Editor Timeout Clamping Is Hidden

```csharp
private float GetEffectiveLocalConnectionTimeoutSeconds()
{
    float configuredTimeout = Mathf.Max(1f, _config.LocalConnectionTimeoutSeconds);
#if UNITY_EDITOR
    float effectiveTimeout = Mathf.Clamp(configuredTimeout, 3f, 5f);
    // ...
```

The Editor silently clamps the timeout to 3–5 seconds regardless of what the user configured. This is only logged at `Debug.Log` level and not reflected in the Inspector. A user setting 30 seconds will see 30 in the Inspector but get 5 in practice.

### 8.8 `ProbeLocalServerHealthAsync` Uses Obsolete `HttpWebRequest`

```csharp
private static async Task<bool> ProbeLocalServerHealthAsync(Uri healthUri)
{
    HttpWebRequest request = WebRequest.CreateHttp(healthUri);
    // ...
}
```

`HttpWebRequest` is obsolete in .NET 6+. While Unity uses .NET Standard 2.1 where it's still available, using `HttpClient` would be more consistent with the rest of the codebase and avoid future deprecation warnings.

### 8.9 ~~No Streaming Support~~ ✅ Fixed

`RunStreamingAsync` now returns `IAsyncEnumerable<AgentResponseUpdate>` with typed content items (`TextContent`, `FunctionCallContent`, `FunctionResultContent`). The chat window uses this for real-time streaming display. `SendMessageAsync` remains as a convenience wrapper for backward compatibility.

### 8.10 `ToolCallNotifyingFunction` Serializes Arguments Twice

The `WrapToolForEvent` method wraps every tool in a `ToolCallNotifyingFunction` that serializes arguments to JSON for the `OnToolCall` event, then the underlying function executes normally. For tools with complex argument structures, this adds unnecessary serialization overhead on the hot path.

---

## 10. Refactoring Suggestions

### 9.1 Add Retry Logic for Initialization

Replace the fire-and-forget pattern with exponential backoff:

```csharp
private async Task InitializeWithRetryAsync(int maxRetries = 3)
{
    for (int attempt = 0; attempt < maxRetries; attempt++)
    {
        try
        {
            await InitializeAsync().ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (attempt < maxRetries - 1)
        {
            Debug.LogWarning($"[LiveLink-Agent] Init attempt {attempt + 1} failed: {ex.Message}. Retrying...");
            await Task.Delay(1000 * (int)Math.Pow(2, attempt)).ConfigureAwait(false);
        }
    }
}
```

### 9.2 Make `_isInitialized` Thread-Safe

Use `Volatile.Read` / `Volatile.Write` or wrap the flag in a lock:

```csharp
private volatile bool _isInitialized;
```

Or better, remove the separate flag and use the semaphore state as the source of truth.

### 9.3 Add `CancellationToken` Support to the Component

Expose a `CancellationTokenSource` that's cancelled on `OnDestroy()`:

```csharp
private CancellationTokenSource _lifetimeCts;

private void Awake()
{
    _lifetimeCts = new CancellationTokenSource();
    // ...
}

private void OnDestroy()
{
    _lifetimeCts.Cancel();
    ShutdownAsync().GetAwaiter().GetResult(); // or synchronous cleanup
}
```

### 9.4 Consolidate Transport Mode Handling

Remove the silent SSE override. Instead, either:

- Remove `Sse` from the `LocalHttpTransportMode` enum values shown in the Inspector (use an `[InspectorOnly]` attribute or custom property drawer), or
- Honor the user's choice and document the known issues with SSE in Unity.

### 9.5 Replace `HttpWebRequest` with `HttpClient`

```csharp
private static readonly HttpClient _healthClient = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };

private static async Task<bool> ProbeLocalServerHealthAsync(Uri healthUri)
{
    HttpResponseMessage response = await _healthClient.GetAsync(healthUri).ConfigureAwait(false);
    return response.IsSuccessStatusCode;
}
```

### 9.6 Add Streaming Response Support

Expose a streaming callback for incremental responses:

```csharp
public AgentTextEvent OnResponseChunk; // fired per chunk during streaming

// In SendMessageAsync:
await foreach (var chunk in _agent.RunStreamingAsync(message, _session))
{
    DispatchToMainThread(() => OnResponseChunk.Invoke(chunk.Text));
}
```

This requires the Microsoft Agent Framework to support streaming, but the architecture should be prepared for it.

### 9.7 Extract Health Check and Readiness Logic

The health probe and readiness wait logic in `EmbeddedAgentRuntime` (20+ lines) should be extracted into a reusable helper:

```csharp
internal static class ServerReadiness
{
    public static async Task WaitForHealthAsync(Uri healthUri, TimeSpan timeout, CancellationToken ct) { ... }
}
```

This improves testability and allows reuse in the Editor connection tester.

### 9.8 Use `IAsyncDisposable` for the Runtime

Implement `IAsyncDisposable` so callers can properly await cleanup:

```csharp
public sealed class EmbeddedAgentRuntime : MonoBehaviour, IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
        _initializationLock.Dispose();
        _runLock.Dispose();
    }
}
```

### 9.9 Add Inspector Validation

Add `OnValidate()` to `AgentRuntimeConfig` to warn about common misconfigurations:

```csharp
private void OnValidate()
{
    if (_preferEnvironmentApiKey && string.IsNullOrEmpty(_openAIApiKeyEnvironmentVariable))
    {
        Debug.LogWarning("AgentRuntimeConfig: Prefer Environment API Key is enabled but no environment variable name is set.");
    }
    if (_maxPersistedMessages < 10) _maxPersistedMessages = 10;
    if (_maxHistoryFileSizeBytes < 16384) _maxHistoryFileSizeBytes = 16384;
}
```

### 9.10 Unify `SubmitMessage` and `SendMessage`

The component exposes both `SubmitMessage(string)` and `SendMessage(string)` (which shadows `MonoBehaviour.SendMessage`). This is confusing:

```csharp
public void SubmitMessage(string message) { _ = SendMessageAsync(message); }
public new void SendMessage(string message) { SubmitMessage(message); }
```

Remove the `SendMessage` overload entirely to avoid shadowing the base class method. Use `SubmitMessage` as the sole fire-and-forget API.

---

## Appendix: File Inventory

| File | Lines | Purpose |
|---|---|---|
| `Runtime/Agent/EmbeddedAgentRuntime.cs` | ~650 | Core runtime component |
| `Runtime/Agent/AgentRuntimeConfig.cs` | ~110 | Configuration ScriptableObject |
| `Runtime/Agent/AgentExternalMcpServerConfig.cs` | ~80 | Downstream MCP server DTO |
| `Runtime/Agent/AgentMcpClientFactory.cs` | ~180 | MCP client creation factory |
| `Runtime/Agent/AgentMcpConnectionTester.cs` | ~15 | Editor connection test wrapper |
| `Runtime/Agent/AgentMcpConnectionTestResult.cs` | ~15 | Connection test result DTO |
| `Runtime/Agent/FileChatHistoryProvider.cs` | ~280 | Persistent chat history |
| `Runtime/Agent/AgentToolNames.cs` | ~25 | Mutation tool name registry |
| `Runtime/Agent/AgentNamedValue.cs` | ~20 | Serializable key/value pair |
| `Runtime/Agent/AgentMcpTransportType.cs` | ~10 | Transport type enum |
| `Runtime/Agent/AgentMcpHttpTransportMode.cs` | ~12 | HTTP transport mode enum |
| `Editor/Agent/EmbeddedAgentRuntimeEditor.cs` | ~140 | Runtime component Inspector |
| `Editor/Agent/AgentRuntimeConfigEditor.cs` | ~250 | Config asset Inspector |
