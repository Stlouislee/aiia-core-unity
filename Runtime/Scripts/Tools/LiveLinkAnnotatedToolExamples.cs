using System;
using UnityEngine;

namespace LiveLink.Tools
{
    /// <summary>
    /// Example tools that demonstrate annotation-based discovery.
    /// Third-party developers can follow the same pattern in their own assemblies.
    /// </summary>
    public static class LiveLinkAnnotatedToolExamples
    {
        [LiveLinkTool(
            "livelink_echo",
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
            return new
            {
                echoed = uppercase ? safeText.ToUpperInvariant() : safeText,
                utc = DateTime.UtcNow.ToString("O"),
                frame = Time.frameCount
            };
        }

        [LiveLinkTool(
            "livelink_create_empty_object",
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
            return new
            {
                name = go.name,
                instance_id = go.GetInstanceID()
            };
        }
    }
}
