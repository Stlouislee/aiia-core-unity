using System;
using System.Collections.Generic;
using UnityEngine;
using LiveLink.Network;

namespace LiveLink
{
    /// <summary>
    /// Handles mapping between Unity GameObjects and MCP (Model Context Protocol) resources.
    /// </summary>
    public static class MCPResourceMapper
    {
        private const string URI_SCHEME = "mcp://unity";
        private const string SCENE_PATH = "scenes";
        private const string OBJECT_PATH = "objects";

        /// <summary>
        /// Generates an MCP resource URI for a given GameObject.
        /// </summary>
        public static string GetResourceURI(string sceneName, string uuid)
        {
            return $"{URI_SCHEME}/{SCENE_PATH}/{sceneName}/{OBJECT_PATH}/{uuid}";
        }

        /// <summary>
        /// Parses an MCP resource URI to extract the UUID.
        /// </summary>
        public static string GetUUIDFromURI(string uri)
        {
            if (string.IsNullOrEmpty(uri) || !uri.StartsWith(URI_SCHEME))
                return null;

            string[] parts = uri.Split('/');
            if (parts.Length >= 6 && parts[parts.Length - 2] == OBJECT_PATH)
            {
                return parts[parts.Length - 1];
            }

            return null;
        }

        /// <summary>
        /// Converts a SceneObjectDTO to an MCP resource representation.
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
