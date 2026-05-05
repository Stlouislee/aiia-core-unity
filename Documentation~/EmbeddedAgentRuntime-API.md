# EmbeddedAgentRuntime API Reference

## Overview

`EmbeddedAgentRuntime` hosts a Microsoft Agent Framework `AIAgent` inside Unity. It connects to MCP servers (local and remote), A2A agents, and an LLM backend to provide a fully-featured in-app AI agent.

The API aligns with the **Microsoft Agent Framework** conventions (`AgentResponse`, `AgentResponseUpdate`, `ChatMessage`, `AIContent`).

---

## Quick Start

```csharp
// Get reference (Inspector or code)
[SerializeField] private EmbeddedAgentRuntime _agentRuntime;

// Send a message and get the full structured response
AgentResponse response = await _agentRuntime.RunAsync("Move the chair to the left");

// Access the text answer
string answer = response.Text;

// Inspect intermediate steps (tool calls, tool results, etc.)
foreach (ChatMessage msg in response.Messages)
{
    foreach (AIContent content in msg.Contents)
    {
        switch (content)
        {
            case TextContent text:
                Debug.Log($"Text: {text.Text}");
                break;
            case FunctionCallContent call:
                Debug.Log($"Tool call: {call.Name}({call.Arguments})");
                break;
            case FunctionResultContent result:
                Debug.Log($"Tool result: {result.Result}");
                break;
        }
    }
}

// Check token usage
if (response.Usage != null)
{
    Debug.Log($"Input tokens: {response.Usage.InputTokenCount}");
    Debug.Log($"Output tokens: {response.Usage.OutputTokenCount}");
}

// Check finish reason
Debug.Log($"Finish reason: {response.FinishReason}"); // Stop, ToolCalls, Length, etc.
```

---

## Core Methods

### `RunAsync(string message, CancellationToken) → Task<AgentResponse>`

**Primary API.** Sends a message and returns a structured `AgentResponse` containing:

| Property | Type | Description |
|----------|------|-------------|
| `Text` | `string` | Aggregated text from all messages (convenience) |
| `Messages` | `IList<ChatMessage>` | All messages produced during the run, including intermediate tool call/result messages |
| `Usage` | `UsageDetails` | Token counts (input, output, total) |
| `FinishReason` | `ChatFinishReason?` | Why the agent stopped: `Stop`, `ToolCalls`, `Length`, `ContentFilter`, etc. |
| `AdditionalProperties` | `Dictionary` | Provider-specific metadata |

Each `ChatMessage` contains a `Contents` list with typed `AIContent` items:

- `TextContent` — text output
- `FunctionCallContent` — agent wants to call a tool (has `Name`, `Arguments`, `CallId`)
- `FunctionResultContent` — result of a tool call (has `CallId`, `Result`)
- `DataContent` — binary data (images, audio, etc.)

### `RunStreamingAsync(string message, CancellationToken) → IAsyncEnumerable<AgentResponseUpdate>`

Streaming variant. Yields `AgentResponseUpdate` chunks as the agent produces them.

```csharp
await foreach (AgentResponseUpdate update in _agentRuntime.RunStreamingAsync("Tell me a story"))
{
    // Text chunk
    if (update.Text != null)
        uiText.text += update.Text;

    // Tool call in progress
    foreach (var content in update.Contents)
    {
        if (content is FunctionCallContent call)
            ShowToolCallUI(call.Name, call.Arguments);
    }
}
```

Each `AgentResponseUpdate` has:

| Property | Type | Description |
|----------|------|-------------|
| `Text` | `string?` | Text portion of this update (null if non-text) |
| `Contents` | `IList<AIContent>` | Typed content items in this update |
| `Role` | `ChatRole?` | Who produced this update (assistant, tool, etc.) |
| `FinishReason` | `ChatFinishReason?` | Present on the final update |

### `SendMessageAsync(string message, CancellationToken) → Task<string>`

**Convenience wrapper.** Returns only the text result. Equivalent to:

```csharp
string text = (await RunAsync(message, ct)).Text;
```

Retained for backward compatibility. Prefer `RunAsync` for new code.

### `SubmitMessage(string message)` / `SendMessage(string message)`

Fire-and-forget variants. Use with `OnResponseReceived` / `OnError` events.

---

## Properties

| Property | Type | Description |
|----------|------|-------------|
| `Config` | `AgentRuntimeConfig` | The configuration asset |
| `LiveLinkManager` | `LiveLinkManager` | Associated LiveLink manager |
| `IsInitialized` | `bool` | Whether the runtime has completed initialization |
| `IsBusy` | `bool` | Whether a request is currently being processed |
| `Status` | `string` | Current status text (`"Idle"`, `"Running agent..."`, `"Response received."`) |
| `LastResponse` | `string` | Text of the most recent response |
| `LastAgentResponse` | `AgentResponse?` | Full structured response from the most recent `RunAsync` |
| `LastError` | `string` | Most recent error message |
| `ConnectedServerCount` | `int` | Number of connected MCP servers |
| `AvailableToolNames` | `IReadOnlyList<string>` | Names of all available tools |

---

## Events (Unity Inspector)

These are `UnityEvent` fields — assignable from the Inspector for drag-and-drop wiring.

| Event | Signature | When |
|-------|-----------|------|
| `OnResponseReceived` | `UnityEvent<string>` | Agent completed a response (text payload) |
| `OnError` | `UnityEvent<string>` | An error occurred (error message payload) |
| `OnStatusChanged` | `UnityEvent<string>` | Status text changed |
| `OnToolCall` | `UnityEvent<string, string>` | Agent invoked a tool (tool name, args JSON) |
| `OnConnectionLost` | `UnityEvent<string>` | MCP server disconnected (server name) |
| `OnConnectionRestored` | `UnityEvent<string>` | MCP server reconnected (server name) |

> **Note:** Events fire on the main thread. `RunAsync` / `RunStreamingAsync` callers
> get the same information from the return value and don't need to rely on events.

---

## Lifecycle

```csharp
// Auto-initializes on first RunAsync call if _autoInitialize is true.
// Or initialize explicitly:
await _agentRuntime.InitializeAsync();

// Run queries
AgentResponse r1 = await _agentRuntime.RunAsync("What do you see?");
AgentResponse r2 = await _agentRuntime.RunAsync("Move that object left");

// Reset conversation (clears chat history, creates new session)
await _agentRuntime.ResetSessionAsync();

// Shutdown (disconnects all MCP/A2A servers, releases resources)
await _agentRuntime.ShutdownAsync();
```

---

## Play Mode Chat Window

A built-in editor window for interactive testing during Play Mode.

**Open:** `LiveLink > Agent Chat` or click **"Open Chat Window"** in the Inspector.

**Features:**
- Streaming text display with real-time `AgentResponseUpdate` processing
- Collapsible tool call/result foldouts with name, args, call ID
- Per-response token usage, duration, finish reason
- Stop button for cancelling in-flight requests
- Enter to send, Shift+Enter for newline

The chat window uses `RunStreamingAsync` internally, demonstrating the recommended pattern for rich agent interaction.

---

## Alignment with Microsoft Agent Framework

| Microsoft Agent Framework | EmbeddedAgentRuntime |
|--------------------------|---------------------|
| `AIAgent.RunAsync()` → `AgentResponse` | `RunAsync()` → `AgentResponse` (direct pass-through) |
| `AIAgent.RunStreamingAsync()` → `IAsyncEnumerable<AgentResponseUpdate>` | `RunStreamingAsync()` → same (direct pass-through) |
| `AgentResponse.Messages` | Same — all intermediate steps visible |
| `AgentResponse.Usage` | Same — token usage tracked |
| `AgentResponse.FinishReason` | Same — stop reason exposed |
| `FunctionCallContent` in Messages | Same — plus `OnToolCall` event for Inspector wiring |
| `FunctionResultContent` in Messages | Same — tool results visible in Messages |

The runtime wraps `AIAgent` and adds Unity-specific concerns:
- Main-thread dispatching for Unity API safety
- Inspector-assignable events
- MCP server lifecycle management
- A2A agent connection management
- Automatic initialization with retry
