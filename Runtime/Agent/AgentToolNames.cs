using System;
using System.Collections.Generic;

namespace LiveLink.Agent
{
    /// <summary>
    /// Tool names used by the first-party LiveLink MCP server.
    /// </summary>
    public static class AgentToolNames
    {
        private static readonly HashSet<string> SceneMutationTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "spawn_object",
            "spawn_gltf",
            "transform_object",
            "delete_object",
            "rename_object",
            "set_parent",
            "set_active"
        };

        public static bool IsSceneMutationTool(string toolName)
        {
            return !string.IsNullOrEmpty(toolName) && SceneMutationTools.Contains(toolName);
        }
    }
}
