# LiveLink Dynamic Tool System

> **Package:** `aiia-core-unity` (LiveLink)  
> **Namespace:** `LiveLink.Tools`

## Table of Contents

1. [Overview](#overview)
2. [Attribute-Based Discovery](#attribute-based-discovery)
3. [Manifest-Based Discovery (Zero-Intrusion)](#manifest-based-discovery-zero-intrusion)
4. [Build-Time Discovery Cache](#build-time-discovery-cache)
5. [Tool Exposure Policy](#tool-exposure-policy)
6. [Creating Custom Tools: Step-by-Step](#creating-custom-tools-step-by-step)
7. [Tool Metadata Reference](#tool-metadata-reference)
8. [Design Issues](#design-issues)
9. [Refactoring Suggestions](#refactoring-suggestions)

---

## Overview

The Dynamic Tool System allows Unity developers to expose C# methods as MCP (Model Context Protocol) tools that agents and external clients can discover and invoke at runtime — without maintaining hand-written switch tables.

**Three discovery paths:**

| Path | When to Use | Intrusion Level |
|------|-------------|-----------------|
| **Attribute-based** | Your own code, you can add attributes | Low — add `[LiveLinkTool]` to static methods |
| **Manifest-based** | Third-party / plugin code you cannot modify | Zero — configure via ScriptableObject asset |
| **Build-time cache** | Performance optimization | None at runtime — pre-computed at edit-time |

All three paths feed into a single `LiveLinkToolRegistry`. At runtime, `tools/list` and `tools/call` route through the registry first, then fall back to legacy built-in handlers.

**Key classes:**

| Class | Role |
|-------|------|
| `LiveLinkToolRegistry` | Central registry; rebuilds from cache, reflection, or manifest assets |
| `LiveLinkToolInvoker` | Invokes a tool descriptor, handling main-thread dispatch and async returns |
| `LiveLinkToolExposurePolicy` | Per-consumer allow/deny/mutation/visibility filtering |
| `LiveLinkMcpRequestContext` | `AsyncLocal`-based consumer context (embedded agent vs. external) |
| `LiveLinkToolCacheAsset` | ScriptableObject holding pre-computed tool descriptors |
| `LiveLinkToolManifestAsset` | ScriptableObject mapping assembly/type/method → tool descriptor |

---

## Attribute-Based Discovery

Mark any **public static** method with `[LiveLinkTool]` to register it as an MCP tool.

### `LiveLinkToolAttribute`

```csharp
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class LiveLinkToolAttribute : Attribute
```

**Constructor parameter:**
- `name` (string, required) — The MCP tool name exposed to clients.

**Named properties:**

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Description` | `string` | `""` | Human-readable description shown in `tools/list` |
| `Category` | `string` | `""` | Logical grouping (e.g. `"scene"`, `"utility"`) |
| `Tags` | `string[]` | `[]` | Freeform tags for policy filtering |
| `Visibility` | `LiveLinkToolVisibility` | `Both` | Which consumers can see this tool |
| `RequiresMainThread` | `bool` | `false` | If `true`, invocation is marshalled to the Unity main thread |
| `IsMutation` | `bool` | `false` | If `true`, subject to per-consumer mutation toggles |

### `LiveLinkToolParameterAttribute`

```csharp
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class LiveLinkToolParameterAttribute : Attribute
```

**Constructor parameter:**
- `name` (string, required) — The exposed parameter name in the MCP schema.

**Named properties:**

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Description` | `string` | `""` | Parameter description in the tool's input schema |
| `Required` | `bool` | `false` | Whether the parameter is mandatory |

> **Note:** If `[LiveLinkToolParameter]` is omitted, the C# parameter name is used as-is and the description is empty. The `Required` flag defaults to `true` for non-nullable value types without defaults, regardless of the attribute.

### Visibility enum

```csharp
public enum LiveLinkToolVisibility
{
    Both = 0,          // Visible to all consumers
    AgentOnly = 1,     // Only visible to the embedded agent
    ExternalOnly = 2   // Only visible to external MCP clients
}
```

---

## Manifest-Based Discovery (Zero-Intrusion)

For third-party packages where you **cannot or should not** add a `LiveLink.Tools` dependency, use manifest assets.

### How It Works

1. Create a `LiveLinkToolManifest` asset (**LiveLink > Tool Manifest** in the project window).
2. For each tool, fill in `Assembly Name`, `Type Name`, and `Method Name`.
3. Add the manifest asset to `LiveLinkManager > Dynamic MCP Tools > Tool Manifest Assets`.
4. At startup, `LiveLinkToolManifestResolver` resolves each entry via reflection and produces `LiveLinkToolDescriptor` instances.

### `LiveLinkToolManifestAsset`

A `ScriptableObject` containing a list of `LiveLinkToolManifestEntry` objects.

**`LiveLinkToolManifestEntry` fields:**

| Field | Type | Description |
|-------|------|-------------|
| `ToolName` | `string` | MCP tool name (required) |
| `Description` | `string` | Tool description |
| `AssemblyName` | `string` | Short assembly name (e.g. `"MyPlugin.Runtime"`) |
| `TypeName` | `string` | Fully-qualified type name (e.g. `"MyPlugin.Tools.MathHelpers"`) |
| `MethodName` | `string` | Static method name |
| `ExpectedParameterCount` | `int` | Disambiguation hint for overloads (`-1` = any) |
| `Visibility` | `LiveLinkToolVisibility` | Consumer visibility |
| `RequiresMainThread` | `bool` | Main-thread requirement |
| `IsMutation` | `bool` | Mutation flag |
| `Category` | `string` | Category string |
| `Tags` | `List<string>` | Tag list |
| `ParameterOverrides` | `List<LiveLinkToolManifestParameterOverride>` | Per-parameter name/description/required overrides |

### Parameter Overrides

`LiveLinkToolManifestParameterOverride` lets you rename, describe, or force-required parameters without touching the source method:

| Field | Description |
|-------|-------------|
| `MethodParameterName` | The actual C# parameter name to match (case-insensitive) |
| `ExposedParameterName` | Override name exposed in the MCP schema |
| `Description` | Parameter description |
| `OverrideRequired` | If `true`, the `Required` field overrides auto-detection |
| `Required` | The forced required value |

### Combining with Attribute Mode

Manifest and attribute tools coexist in the same registry. If two tools share the same name, the first one registered wins and a duplicate warning is logged.

---

## Build-Time Discovery Cache

Runtime reflection scanning (`Assembly.GetTypes()` → `GetMethods()` → `GetCustomAttribute()`) can cause startup freezes, especially in large projects. The cache system pre-computes tool descriptors at edit-time.

### Generating the Cache

1. **Menu:** `LiveLink > Rebuild Tool Cache`
2. **Automatic:** On editor load if no cache exists or it is stale.
3. **Pre-build:** Automatically runs before player builds (`IPreprocessBuildWithReport`).

### How It Works

1. `LiveLinkToolCacheBuilder` scans all loaded assemblies for `[LiveLinkTool]` methods.
2. For each discovered tool, it creates a `LiveLinkToolCacheEntry` containing the tool metadata, assembly/type/method names, pre-computed `inputSchema` JSON, and parameter cache entries.
3. It computes file-timestamp hashes for each scanned assembly to detect staleness.
4. The result is written to `Assets/LiveLink/Resources/LiveLinkToolCache.asset`.

### Staleness Detection

`LiveLinkToolCacheAsset.IsStale` compares the current assembly file timestamps against the stored hashes. If any assembly has been recompiled, the cache is considered stale and the registry falls back to runtime reflection.

### Runtime Flow

```
LiveLinkToolRegistry.Rebuild()
  ├─ if cacheAsset != null && !cacheAsset.IsStale
  │    └─ LoadFromCache(cacheAsset)  → resolve MethodInfos from cached assembly/type/method names
  └─ else
       └─ ScanAssembliesForAttributes(assemblyAllowList)  → full reflection scan
  └─ always: merge manifest-based tools
```

### Cache Asset Structure

**`LiveLinkToolCacheAsset`:**

| Field | Description |
|-------|-------------|
| `BuildTimestamp` | `DateTime.UtcNow.Ticks` at build time |
| `AssemblyHashes` | List of `(AssemblyName, Hash)` for staleness detection |
| `Tools` | List of `LiveLinkToolCacheEntry` |

**`LiveLinkToolCacheEntry`:**

| Field | Description |
|-------|-------------|
| `ToolName` | MCP tool name |
| `Description`, `Category`, `Tags`, `Visibility`, `RequiresMainThread`, `IsMutation` | Tool metadata |
| `AssemblyName`, `TypeName`, `MethodName` | Method reference for runtime resolution |
| `InputSchemaJson` | Pre-computed JSON Schema string |
| `Parameters` | List of `LiveLinkToolParameterCache` |

**`LiveLinkToolParameterCache`:**

| Field | Description |
|-------|-------------|
| `Name`, `Description` | Exposed name and description |
| `ParameterTypeName` | Assembly-qualified type name for runtime resolution |
| `Required`, `HasDefaultValue`, `DefaultValueJson`, `Position` | Parameter constraints |

---

## Tool Exposure Policy

The `LiveLinkToolExposurePolicy` class filters which tools are visible to which consumers. The policy is applied per-request via `IsToolVisible(descriptor, consumer)`.

### Consumer Detection

```csharp
public enum LiveLinkToolConsumer
{
    External = 0,       // External MCP client
    EmbeddedAgent = 1   // In-app embedded agent
}
```

The embedded agent's local MCP client sends the header `X-LiveLink-Consumer: embedded-agent`, which the server uses to set the `AsyncLocal` consumer context via `LiveLinkMcpRequestContext.PushConsumer()`.

### Policy Fields

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `EnableDynamicTools` | `bool` | `true` | Master kill-switch for all dynamic tools |
| `ExposeToExternal` | `bool` | `true` | Allow any dynamic tools for external clients |
| `ExposeToEmbeddedAgent` | `bool` | `true` | Allow any dynamic tools for the embedded agent |
| `AllowExternalMutationTools` | `bool` | `false` | Allow mutation tools for external clients |
| `AllowEmbeddedAgentMutationTools` | `bool` | `true` | Allow mutation tools for the embedded agent |
| `ExternalAllowList` | `HashSet<string>` | empty | Whitelist for external consumers (case-insensitive) |
| `ExternalDenyList` | `HashSet<string>` | empty | Blacklist for external consumers |
| `AgentAllowList` | `HashSet<string>` | empty | Whitelist for embedded agent |
| `AgentDenyList` | `HashSet<string>` | empty | Blacklist for embedded agent |
| `AllowedCategories` | `HashSet<string>` | empty | If non-empty, only these categories are allowed |
| `AllowedTags` | `HashSet<string>` | empty | If non-empty, tools must have at least one matching tag |

### Evaluation Order

`IsToolVisible` evaluates filters in this order (short-circuit on first `false`):

1. **Master toggle** — `EnableDynamicTools` must be `true`
2. **Visibility match** — tool's `Visibility` enum must match the consumer
3. **Consumer toggle** — `ExposeToExternal` or `ExposeToEmbeddedAgent`
4. **Mutation rule** — if `IsMutation`, the consumer-specific mutation toggle must be `true`
5. **Allow/deny lists** — deny-list takes precedence; if allow-list is non-empty, tool must be in it
6. **Category filter** — if `AllowedCategories` is non-empty, tool's category must match
7. **Tag filter** — if `AllowedTags` is non-empty, tool must have at least one matching tag

---

## Creating Custom Tools: Step-by-Step

### Example 1: Simple Read-Only Tool

```csharp
using LiveLink.Tools;

public static class MyGameplayTools
{
    [LiveLinkTool(
        "get_player_score",
        Description = "Returns the current player score.",
        Visibility = LiveLinkToolVisibility.Both,
        RequiresMainThread = false,
        IsMutation = false,
        Category = "gameplay",
        Tags = new[] { "read", "score" })]
    public static object GetPlayerScore()
    {
        return new { score = GameManager.Instance.Score };
    }
}
```

### Example 2: Tool with Parameters

```csharp
[LiveLinkTool(
    "set_time_scale",
    Description = "Sets the Unity Time.timeScale.",
    Visibility = LiveLinkToolVisibility.AgentOnly,
    RequiresMainThread = true,
    IsMutation = true,
    Category = "scene",
    Tags = new[] { "mutation", "time" })]
public static object SetTimeScale(
    [LiveLinkToolParameter("scale", Description = "New time scale (0.0 to 10.0)", Required = true)] float scale)
{
    UnityEngine.Time.timeScale = Mathf.Clamp(scale, 0f, 10f);
    return new { timeScale = UnityEngine.Time.timeScale };
}
```

### Example 3: Async Tool (Returns `Task<T>`)

```csharp
[LiveLinkTool(
    "fetch_remote_config",
        Description = "Fetches a remote configuration JSON.",
        Visibility = LiveLinkToolVisibility.Both,
        RequiresMainThread = false,
        IsMutation = false,
        Category = "network")]
public static async Task<object> FetchRemoteConfig(
    [LiveLinkToolParameter("url", Required = true)] string url)
{
    using var client = new System.Net.Http.HttpClient();
    string json = await client.GetStringAsync(url);
    return new { raw = json };
}
```

### Example 4: Manifest Mode (Third-Party Code)

Suppose you have a third-party assembly `ThirdParty.Utils.dll` with a static method:

```csharp
// In ThirdParty.Utils (you cannot modify this)
namespace ThirdParty.MathHelpers
{
    public static class Geometry
    {
        public static float Distance(float x1, float y1, float x2, float y2)
        {
            return (float)System.Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
        }
    }
}
```

Create a `LiveLinkToolManifest` asset and configure:

| Field | Value |
|-------|-------|
| Tool Name | `geometry_distance` |
| Description | "Euclidean distance between two 2D points" |
| Assembly Name | `ThirdParty.Utils` |
| Type Name | `ThirdParty.MathHelpers.Geometry` |
| Method Name | `Distance` |
| Category | `math` |
| Parameter Overrides | `x1` → name: `x1`, required: true; `y1` → name: `y1`, required: true; (etc.) |

Then add the manifest asset to `LiveLinkManager > Dynamic MCP Tools > Tool Manifest Assets`.

### Example 5: Built-in Examples

The package ships with `LiveLinkAnnotatedToolExamples` demonstrating both patterns:

```csharp
// Read-only diagnostic tool — available to all consumers
[LiveLinkTool("livelink_echo",
    Description = "Echoes input text and returns basic runtime context.",
    Visibility = LiveLinkToolVisibility.Both,
    RequiresMainThread = false,
    IsMutation = false,
    Category = "utility",
    Tags = new[] { "utility", "diagnostic" })]
public static object Echo(
    [LiveLinkToolParameter("text", Description = "Text to echo back", Required = true)] string text,
    [LiveLinkToolParameter("uppercase", Description = "Return text uppercased")] bool uppercase = false)
{
    string safeText = text ?? string.Empty;
    return new {
        echoed = uppercase ? safeText.ToUpperInvariant() : safeText,
        utc = DateTime.UtcNow.ToString("O"),
        frame = Time.frameCount
    };
}

// Mutation tool — agent-only, requires main thread
[LiveLinkTool("livelink_create_empty_object",
    Description = "Creates an empty GameObject at world origin for quick runtime debugging.",
    Visibility = LiveLinkToolVisibility.AgentOnly,
    RequiresMainThread = true,
    IsMutation = true,
    Category = "scene",
    Tags = new[] { "mutation", "debug" })]
public static object CreateEmpty(
    [LiveLinkToolParameter("name", Description = "Name of the object to create")] string name = "LiveLinkEmpty")
{
    string safeName = string.IsNullOrWhiteSpace(name) ? "LiveLinkEmpty" : name.Trim();
    GameObject go = new GameObject(safeName);
    return new { name = go.name, instance_id = go.GetInstanceID() };
}
```

---

## Tool Metadata Reference

### Categories

Categories are freeform strings used for policy filtering. Suggested conventions:

| Category | Purpose |
|----------|---------|
| `scene` | GameObject manipulation (spawn, transform, delete) |
| `utility` | General-purpose helpers (echo, ping, diagnostics) |
| `gameplay` | Game logic tools (score, inventory, quests) |
| `network` | Remote data fetching |
| `math` | Mathematical computations |
| `debug` | Debugging and development tools |

### Tags

Tags are freeform strings. Unlike categories (one per tool), a tool can have multiple tags. The exposure policy's `AllowedTags` filter matches if the tool has **any** tag in the allowed set (OR logic).

Common tags: `read`, `mutation`, `diagnostic`, `debug`, `time`, `score`.

### Visibility

Controls which consumer type can discover the tool:

- `Both` — all consumers
- `AgentOnly` — embedded agent only
- `ExternalOnly` — external MCP clients only

### RequiresMainThread

When `true`, the invoker marshals execution to the Unity main thread via `MainThreadDispatcher.Enqueue()`. Required for any tool that touches `UnityEngine` APIs (GameObjects, Transform, Time, etc.).

When `false`, the tool runs on the calling thread (typically a background thread from the MCP HTTP server).

### IsMutation

When `true`, the tool is subject to per-consumer mutation toggles:
- `AllowEmbeddedAgentMutationTools` (default: `true`)
- `AllowExternalMutationTools` (default: `false`)

This allows fine-grained control: the embedded agent can mutate the scene by default, while external clients cannot.

---

## Design Issues

### 1. Duplicated Code Across Three Scanners

The `BuildInputSchema()`, `BuildTypeSchema()`, `IsNullable()`, and `IsAssemblyAllowed()` methods are implemented independently in three places:

- `LiveLinkToolRegistry`
- `LiveLinkToolManifestResolver`
- `LiveLinkToolCacheBuilder`

Each implementation has subtle differences (e.g., `LiveLinkToolRegistry.BuildTypeSchema` handles `List<>` but not general `IEnumerable`; `LiveLinkToolManifestResolver.BuildTypeSchema` handles `IEnumerable` but maps enums to `"string"` while the registry does not). These inconsistencies will produce different JSON schemas for the same tool depending on which discovery path was used.

### 2. Cache Still Resolves Methods at Runtime

`LiveLinkToolRegistry.CreateDescriptorFromCache()` calls `Assembly.GetType()` and `Type.GetMethod()` at runtime. The cache only avoids scanning *all* methods on *all* types — it still does per-tool reflection. If the goal is zero-reflection startup, the cache should store enough data to build descriptors without resolving `MethodInfo` at all (and invoke via compiled delegates or stored `MethodInfo` references in the asset, though Unity serialization limits make this harder).

### 3. Staleness Check Uses File Timestamps, Not Content Hashes

`LiveLinkToolCacheAsset.ComputeAssemblyHash()` uses `FileInfo.LastWriteTimeUtc.Ticks` as the hash. This means:
- Incremental recompiles that don't change the tool assembly will still invalidate the cache.
- Different machines / CI environments with different timestamps will always report stale.
- Assembly version changes without file-system changes (e.g., in-memory assemblies) are not detected.

### 4. No Validation of Tool Name Uniqueness Across Discovery Paths

Attribute-discovered and manifest-discovered tools are merged in the same `Rebuild()` call, but there is no cross-path validation beyond the duplicate log warning. If a manifest entry accidentally reuses an attribute tool name, the manifest tool is silently dropped.

### 5. `TargetInstance` Is Always `null`

`LiveLinkToolDescriptor.TargetInstance` is set to `null` in all code paths (attribute, manifest, cache). The field exists but is never populated. The invoker uses `method.Invoke(descriptor.TargetInstance, parameters)` which works for static methods (passing `null` is correct) but the field is misleading — it suggests instance method support that does not exist.

### 6. Non-Public Static Methods Are Accepted

`ScanAssembliesForAttributes` uses `BindingFlags.NonPublic | BindingFlags.Static` and the cache builder does the same. This means `private` and `internal` methods with `[LiveLinkTool]` will be discovered. This is likely unintentional — MCP tools should be explicitly public APIs.

### 7. `LiveLinkToolManifestResolver` Is `internal static` but Builds Schemas Differently

The manifest resolver handles enums as `"string"` in `BuildTypeSchema`, while the registry does not handle enums at all (falls through to `"object"`). A tool registered via manifest that has an enum parameter will produce a different schema than the same tool registered via attribute.

### 8. Error Swallowing in Cache Loading

`LiveLinkToolRegistry.CreateDescriptorFromCache()` catches all exceptions when deserializing default values and parsing input schemas, silently falling back to `null`. This makes debugging cache corruption very difficult.

### 9. Assembly Allow-List Bypass in Cache Builder

`LiveLinkToolCacheBuilder.IsAssemblyAllowed()` filters out Unity assemblies (starting with `"Unity"`, `"UnityEngine"`, `"UnityEditor"`) which the runtime `LiveLinkToolRegistry.IsAssemblyAllowed()` does **not** do. If a developer puts a tool in a `Unity*`-prefixed assembly, the cache builder will skip it but the runtime scanner will find it (or vice versa, depending on the allow-list).

### 10. No Cancellation Support in `LiveLinkToolInvoker`

`InvokeAsync` does not accept a `CancellationToken`. Long-running tools cannot be cancelled by the MCP client, which could lead to resource leaks or stuck requests.

---

## Refactoring Suggestions

### 1. Extract Shared Utilities into a Static Helper Class

Create `LiveLinkToolSchemaHelper` (or similar) containing the canonical implementations of `BuildInputSchema`, `BuildTypeSchema`, `IsNullable`, and `IsAssemblyAllowed`. All three consumers (`Registry`, `ManifestResolver`, `CacheBuilder`) should reference this single source of truth.

### 2. Store Pre-Resolved Delegates in the Cache

Instead of storing assembly/type/method names and resolving `MethodInfo` at runtime, consider:
- Storing `MethodInfo` references directly in the ScriptableObject (Unity can serialize `MonoScript` but not arbitrary `MethodInfo`).
- Using a hybrid: store a `MonoScript` reference + method name, which is faster to resolve than full assembly scanning.
- At minimum, cache the `MethodInfo` in a `Dictionary` after first resolution so subsequent play-mode entries are instant.

### 3. Use Content-Based Staleness Detection

Replace file-timestamp hashing with a content hash (e.g., SHA-256 of the assembly bytes, or at minimum the assembly's `Version` + `ImageRuntimeVersion`). This avoids false staleness on CI or when timestamps drift.

### 4. Restrict Attribute Discovery to Public Methods

Change the binding flags in both the registry scanner and cache builder to `BindingFlags.Public | BindingFlags.Static` only. Add a compile-time analyzer or post-scan validation warning for non-public methods carrying `[LiveLinkTool]`.

### 5. Remove `TargetInstance` from `LiveLinkToolDescriptor`

Since instance methods are explicitly rejected (with a warning), remove the `TargetInstance` field entirely. If instance method support is planned for the future, add it behind a feature flag with proper lifecycle management.

### 6. Add `CancellationToken` to `InvokeAsync`

```csharp
public static async Task<object> InvokeAsync(
    LiveLinkToolDescriptor descriptor,
    JObject arguments,
    CancellationToken cancellationToken = default)
```

Pass the token to async tool implementations and check `cancellationToken.ThrowIfCancellationRequested()` before synchronous invocations.

### 7. Add a Unified `BuildTypeSchema` That Handles Enums Consistently

The canonical schema builder should handle enums the same way regardless of discovery path. The manifest resolver's approach (enums → `"string"` with enum names) is more useful for LLM consumption. Adopt this uniformly.

### 8. Add Validation Mode

Add a `Validate()` method to `LiveLinkToolRegistry` that runs after `Rebuild()` and reports:
- Duplicate tool names across all discovery paths
- Tools with empty descriptions
- Tools with non-public methods
- Tools with parameter types that produce `"object"` schemas (opaque to LLMs)
- Manifest entries that failed to resolve

### 9. Make Cache Builder Respect the Runtime Allow-List

`LiveLinkToolCacheBuilder` should accept or read the same `assemblyAllowList` that the runtime registry uses, so the cache accurately reflects what would be discovered at runtime.

### 10. Consider a Roslyn Source Generator

For maximum performance and zero-reflection startup, a source generator could emit the tool registry initialization code at compile time, producing a static `RegisterAllTools(Registry)` method. This eliminates all runtime scanning and cache staleness concerns, at the cost of increased build complexity.
