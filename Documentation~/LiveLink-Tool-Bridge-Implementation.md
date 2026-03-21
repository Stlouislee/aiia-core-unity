# LiveLink Tool Bridge Implementation

## Context

Goal: make LiveLink a configurable bridge that can automatically discover Unity-app functions (including third-party code), expose selected functions as MCP tools, and make those tools available to:

- embedded in-app agent
- external MCP clients through the first-party LiveLink MCP server

This document defines a three-phase implementation that preserves backward compatibility.

## Design Principles

1. First-party Unity capabilities should remain on LiveLink MCP contracts.
2. Unity API access must run on the Unity main thread.
3. Tool exposure must be policy-driven and default-safe.
4. Existing built-in tools must continue working during migration.
5. Third-party developers should only need attributes + normal C# methods.

## Architecture Overview

### New Runtime Components

- `LiveLinkToolAttribute`: marks methods as MCP tools.
- `LiveLinkToolParameterAttribute`: optional parameter metadata override.
- `LiveLinkToolManifestAsset`: zero-intrusion method mapping asset for external code.
- `LiveLinkToolDescriptor`: normalized metadata for list/call operations.
- `LiveLinkToolRegistry`: discovers annotated methods and stores descriptors.
- `LiveLinkToolInvoker`: executes discovered tools, handles parameter binding, main-thread dispatch, and error formatting.
- `LiveLinkToolExposurePolicy`: determines whether a tool is visible to Agent, External MCP, or both.

### Existing Integration Points

- `MCPToolHandler.HandleListTools`: merge legacy tools + discovered tools.
- `MCPToolHandler.HandleCallToolAsync`: route to dynamic invoker first, then legacy switch.
- `EmbeddedAgentRuntime`: unchanged connection model; it consumes whatever local MCP tools are listed.

## Phase 1: Annotation + Discovery + Dynamic MCP Routing

### Scope

- Add attributes and descriptors.
- Add manifest-based descriptor mapping for third-party code that cannot reference LiveLink attributes.
- Discover tools from loaded assemblies (configurable assembly allow-list).
- Generate MCP `tools/list` entries dynamically.
- Execute discovered tools through `tools/call`.
- Keep legacy hardcoded tools intact as fallback.

### Deliverables

- Runtime tool metadata model and registry.
- Dynamic path in MCP list/call handlers.
- One example annotated tool provider.

### Compatibility

- Existing clients continue to call old tool names.
- Existing hardcoded handlers remain valid.

### Acceptance Criteria

- Annotated method appears in `tools/list`.
- `tools/call` reaches annotated method and returns MCP-compatible result.
- Unknown tool still follows existing error behavior.

## Phase 2: Policy + Safety + Main-Thread and Async Support

### Scope

- Add visibility and safety metadata:
  - `AgentOnly`, `ExternalOnly`, `Both`
  - `Mutation` flag
  - optional tags/category
- Add policy filtering in list/call path.
- Add main-thread invocation support for annotated methods.
- Add async method support (`Task` and `Task<T>`).
- Add duplicate-name detection with deterministic handling and warnings.

### Deliverables

- `LiveLinkToolExposurePolicy` runtime implementation.
- Main-thread awaitable dispatch helper.
- Policy-aware list and call behavior.

### Acceptance Criteria

- Tool can be hidden from external MCP while visible to embedded agent.
- Main-thread-only annotated method executes safely.
- Duplicate tool names are logged and first registration wins (or policy-defined rejection).

## Phase 3: Editor UX + Configuration + Docs

### Scope

- Extend `AgentRuntimeConfig` with local dynamic tool policies:
  - enable dynamic tools
  - include categories/tags
  - allow mutation tools
  - explicit allow/deny lists
- Add editor inspector UI (`AgentRuntimeConfigEditor`) for these fields.
- Expose discovery diagnostics (tool count, conflicts, filtered tools).
- Update docs (`README`, `CHANGELOG`, MVP doc, copilot instructions).

### Deliverables

- Config fields and inspector controls.
- Runtime policy wiring from config to MCP tool exposure path.
- Documentation updates and migration notes.

### Acceptance Criteria

- User can configure exposure rules entirely from Unity inspector.
- Tool list shown to embedded agent and external MCP reflects config.
- Docs describe annotation usage for third-party developers.

## Public Annotation Contract (Initial)

```csharp
[LiveLinkTool(
    "tool_name",
    Description = "What this tool does",
    Visibility = LiveLinkToolVisibility.Both,
    RequiresMainThread = true,
    IsMutation = false,
    Category = "scene",
    Tags = new[] { "read" })]
public static object MyToolMethod(string uuid, int depth = 2)
{
    // return plain POCO/object; framework serializes to MCP content
}
```

Supported return forms:

- `object` or POCO
- `Task<object>` / `Task<T>`
- optional direct MCP result wrapper for advanced scenarios

## Non-Intrusive Manifest Contract

For external code that should not add a `LiveLink.Tools` dependency, use `LiveLinkToolManifest` assets.

Each manifest entry maps a static method by:

- assembly name
- full type name
- method name
- optional expected parameter count for overload disambiguation
- optional parameter metadata overrides
- visibility/safety metadata (`Visibility`, `RequiresMainThread`, `IsMutation`, `Category`, `Tags`)

## Risk Notes

- Reflection scanning cost: mitigate via allow-list + caching.
- Unity threading: enforce main-thread execution in invoker.
- Schema inference limits: allow explicit schema overrides later.
- Tool collisions: detect and log clearly.

## Migration Strategy

1. Ship dynamic path behind config toggles with safe defaults.
2. Keep legacy tool table for compatibility.
3. Incrementally move built-in tools to annotations.
4. Remove legacy table only after one major version with migration notice.

## Test Plan (Manual, Unity)

1. Add an annotated tool method in runtime assembly.
2. Enter Play mode with `LiveLinkManager` MCP enabled.
3. Call `tools/list`, verify discovered tool appears.
4. Call `tools/call`, verify execution and response.
5. Toggle policy settings in `AgentRuntimeConfig` and verify filtering behavior.
6. Validate embedded runtime receives expected local tool subset.
