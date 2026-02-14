using System;
using System.Collections.Generic;
using UnityEngine;
using LiveLink.Network;

namespace LiveLink
{
    /// <summary>
    /// Handles mapping between Unity GameObjects and MCP (Model Context Protocol) resources.
    /// Supports both legacy mcp://unity URIs and new unity:// URIs.
    /// </summary>
    public static class MCPResourceMapper
    {
        private const string LEGACY_URI_SCHEME = "mcp://unity";
        private const string URI_SCHEME = "unity://";
        private const string SCENE_PATH = "scenes";
        private const string OBJECT_PATH = "objects";

        /// <summary>
        /// Generates a unity:// resource URI for a GameObject by instance ID.
        /// </summary>
        public static string GetGameObjectURI(int instanceId)
        {
            return $"unity://go/{instanceId}";
        }

        /// <summary>
        /// Generates a unity:// resource URI for a GameObject's component list.
        /// </summary>
        public static string GetComponentsURI(int instanceId)
        {
            return $"unity://go/{instanceId}/components";
        }

        /// <summary>
        /// Generates a unity:// resource URI for a specific component on a GameObject.
        /// </summary>
        public static string GetComponentURI(int instanceId, string componentType)
        {
            return $"unity://component/{instanceId}/{componentType}";
        }

        /// <summary>
        /// Checks if a URI uses the new unity:// scheme.
        /// </summary>
        public static bool IsUnityScheme(string uri)
        {
            return !string.IsNullOrEmpty(uri) && uri.StartsWith(URI_SCHEME);
        }

        /// <summary>
        /// Checks if a URI uses the legacy mcp://unity scheme.
        /// </summary>
        public static bool IsLegacyScheme(string uri)
        {
            return !string.IsNullOrEmpty(uri) && uri.StartsWith(LEGACY_URI_SCHEME);
        }

        /// <summary>
        /// Generates a legacy MCP resource URI for a given GameObject (kept for backward compatibility).
        /// </summary>
        public static string GetResourceURI(string sceneName, string uuid)
        {
            return $"{LEGACY_URI_SCHEME}/{SCENE_PATH}/{sceneName}/{OBJECT_PATH}/{uuid}";
        }

        /// <summary>
        /// Parses a legacy MCP resource URI to extract the UUID.
        /// </summary>
        public static string GetUUIDFromURI(string uri)
        {
            if (string.IsNullOrEmpty(uri) || !uri.StartsWith(LEGACY_URI_SCHEME))
                return null;

            string[] parts = uri.Split('/');
            if (parts.Length >= 6 && parts[parts.Length - 2] == OBJECT_PATH)
            {
                return parts[parts.Length - 1];
            }

            return null;
        }

        /// <summary>
        /// Converts a SceneObjectDTO to an MCP resource representation (legacy format).
        /// </summary>
        public static Dictionary<string, object> ToMCPResource(SceneObjectDTO dto, string sceneName)
        {
            var resource = new Dictionary<string, object>
            {
                { "uri", GetResourceURI(sceneName, dto.UUID) },
                { "name", dto.Name },
                { "type", "GameObject" },
                { "description", $"Unity GameObject: {dto.Name}" },
                { "metadata", new Dictionary<string, object>
                    {
                        { "uuid", dto.UUID },
                        { "parent_uuid", dto.ParentUUID },
                        { "active", dto.Active },
                        { "layer", dto.Layer },
                        { "tag", dto.Tag },
                        { "transform", new Dictionary<string, object>
                            {
                                { "position", dto.Transform.Position },
                                { "rotation", dto.Transform.Rotation },
                                { "scale", dto.Transform.Scale }
                            }
                        },
                        { "children", dto.Children }
                    }
                }
            };

            return resource;
        }
    }
}
