# External Tool Bridge Guide

This guide explains how to expose Unity application functions as LiveLink MCP tools with minimal coupling.

## Integration Modes

LiveLink supports three integration modes:

1. Manifest mode (recommended for third-party code)
- Zero source-code changes in third-party libraries.
- No `LiveLink.Tools` dependency required in third-party assemblies.
- Map existing static methods through a Unity asset.

2. Runtime registration mode (future)
- Register method delegates at startup without attributes.

3. Attribute mode
- Mark methods with `LiveLinkToolAttribute` and optional parameter attributes.

## Manifest Mode (Zero-Intrusion)

## Prerequisites

- Third-party method is available in a loaded assembly.
- Method is static (public or non-public).
- Method arguments are serializable from JSON values.

## Step 1: Create a Manifest Asset

1. In Unity, create asset:
- `Create > LiveLink > Tool Manifest`

2. Add one or more tool entries.

Each entry defines:

- `Tool Name`: MCP tool name exposed to clients.
- `Description`: tool description shown in `tools/list`.
- `Assembly Name`: loaded assembly name containing target method.
- `Type Name`: full type name, e.g. `MyCompany.Gameplay.SpawnBridge`.
- `Method Name`: static method name to invoke.
- `Expected Parameter Count`: optional overload disambiguation (`-1` disables check).
- `Visibility`: `Both`, `AgentOnly`, `ExternalOnly`.
- `Requires Main Thread`: set true for Unity API access.
- `Is Mutation`: set true for scene/data mutation operations.
- `Category`, `Tags`: optional policy metadata.
- `Parameter Overrides`: optional rename/description/required overrides per method parameter.

## Step 2: Attach Manifest To LiveLinkManager

In `LiveLinkManager > Dynamic MCP Tools`:

- Enable `Enable Dynamic MCP Tools`.
- Assign asset(s) to `Tool Manifest Assets`.
- Configure exposure policies for embedded agent and external MCP clients.

## Step 3: Verify

1. Enter Play Mode.
2. Call `tools/list` on LiveLink MCP.
3. Confirm manifest tool appears.
4. Call `tools/call` and verify target method executes.

## Error Handling Behavior

When manifest entry cannot resolve, LiveLink logs warning and skips only that entry.

Common warnings:

- Assembly not found.
- Type not found.
- Method not found.
- Ambiguous overload (first match used unless `Expected Parameter Count` is set).

## Example Entry

Assume existing code (no LiveLink references):

```csharp
namespace Vendor.Gameplay
{
    public static class SpawnApi
    {
        public static object SpawnCrate(string label, int count = 1)
        {
            return new { ok = true, label, count };
        }
    }
}
```

Manifest entry:

- Tool Name: `vendor_spawn_crate`
- Assembly Name: `Vendor.Gameplay`
- Type Name: `Vendor.Gameplay.SpawnApi`
- Method Name: `SpawnCrate`
- Expected Parameter Count: `2`
- Requires Main Thread: `false` (set true if Unity API is touched)
- Visibility: `Both`
- Is Mutation: `true`

Optional parameter overrides:

- `label` -> exposed name `name`, description `Label for spawned crate`
- `count` -> description `Number of crates`

## Best Practices

1. Prefer manifest mode for external/third-party packages.
2. Keep target methods small and deterministic.
3. For Unity object access, set `Requires Main Thread = true`.
4. Mark mutating methods with `Is Mutation = true` to use policy gates.
5. Use unique tool names to avoid collisions.
