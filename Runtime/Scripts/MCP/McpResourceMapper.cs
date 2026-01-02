using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using LiveLink.Network;

namespace LiveLink.MCP
{
    /// <summary>
    /// Maps Unity GameObjects to MCP resources and content payloads.
    /// </summary>
    public class McpResourceMapper
    {
        private readonly SceneScanner _scanner;
        private const string ScenePrefix = "mcp://unity/scenes/";

        public McpResourceMapper(SceneScanner scanner)
        {
            _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        }

        public string BuildSceneUri(string sceneName)
        {
            string safeName = string.IsNullOrEmpty(sceneName) ? "Scene" : sceneName;
            return ScenePrefix + Uri.EscapeDataString(safeName);
        }

        public string BuildObjectUri(string sceneName, string uuid)
        {
            return BuildSceneUri(sceneName) + "/objects/" + uuid;
        }

        public bool TryParseObjectUri(string uri, out string sceneName, out string uuid)
        {
            sceneName = null;
            uuid = null;

            if (string.IsNullOrEmpty(uri)) return false;
            if (!uri.StartsWith(ScenePrefix, StringComparison.OrdinalIgnoreCase)) return false;

            string withoutPrefix = uri.Substring(ScenePrefix.Length);
            var segments = withoutPrefix.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 3 && string.Equals(segments[1], "objects", StringComparison.OrdinalIgnoreCase))
            {
                sceneName = Uri.UnescapeDataString(segments[0]);
                uuid = segments[2];
                return true;
            }

            return false;
        }

        public List<McpResource> BuildResources(SceneDumpPacket sceneDump)
        {
            var resources = new List<McpResource>();
            string sceneName = sceneDump?.Payload?.SceneName ?? SceneManager.GetActiveScene().name ?? "Scene";

            resources.Add(new McpResource
            {
                Uri = BuildSceneUri(sceneName),
                Name = sceneName,
                Description = "Unity scene root",
                MimeType = "application/json",
                Attributes = new JObject { ["scene"] = sceneName }
            });

            if (sceneDump?.Payload?.Objects != null)
            {
                foreach (var obj in sceneDump.Payload.Objects)
                {
                    resources.Add(CreateResource(sceneName, obj));
                }
            }

            return resources;
        }

        public bool TryBuildContent(string uri, out McpContent content)
        {
            content = null;
            if (!TryParseObjectUri(uri, out string sceneName, out string uuid)) return false;

            var go = _scanner.GetGameObjectByUUID(uuid);
            if (go == null) return false;

            var dto = Snapshot(go);
            if (dto == null) return false;

            var payload = BuildAttributes(dto);
            payload["name"] = dto.Name;
            payload["uri"] = BuildObjectUri(sceneName, dto.UUID);
            payload["scene"] = sceneName;

            string text = JsonConvert.SerializeObject(payload, Formatting.None);

            content = new McpContent
            {
                Uri = uri,
                MimeType = "application/json",
                Text = text
            };
            return true;
        }

        private McpResource CreateResource(string sceneName, SceneObjectDTO dto)
        {
            return new McpResource
            {
                Uri = BuildObjectUri(sceneName, dto.UUID),
                Name = dto.Name,
                Description = string.IsNullOrEmpty(dto.Tag) ? "Unity GameObject" : $"Unity GameObject ({dto.Tag})",
                MimeType = "application/json",
                Attributes = BuildAttributes(dto)
            };
        }

        private JObject BuildAttributes(SceneObjectDTO dto)
        {
            var obj = new JObject
            {
                ["uuid"] = dto.UUID,
                ["parent_uuid"] = dto.ParentUUID,
                ["active"] = dto.Active,
                ["layer"] = dto.Layer,
                ["tag"] = dto.Tag
            };

            if (dto.Transform != null)
            {
                obj["position"] = new JArray(dto.Transform.Position ?? Array.Empty<float>());
                obj["rotation"] = new JArray(dto.Transform.Rotation ?? Array.Empty<float>());
                obj["scale"] = new JArray(dto.Transform.Scale ?? Array.Empty<float>());
            }

            if (dto.Children != null && dto.Children.Count > 0)
            {
                obj["children"] = new JArray(dto.Children);
            }

            return obj;
        }

        private SceneObjectDTO Snapshot(GameObject go)
        {
            if (go == null) return null;

            string parentUuid = go.transform.parent != null ? _scanner.GetOrCreateUUID(go.transform.parent.gameObject) : null;
            var dto = new SceneObjectDTO
            {
                UUID = _scanner.GetOrCreateUUID(go),
                ParentUUID = parentUuid,
                Name = go.name,
                Active = go.activeSelf,
                Layer = go.layer,
                Tag = go.tag,
                Transform = new TransformDTO
                {
                    Position = new[] { go.transform.position.x, go.transform.position.y, go.transform.position.z },
                    Rotation = new[] { go.transform.rotation.x, go.transform.rotation.y, go.transform.rotation.z, go.transform.rotation.w },
                    Scale = new[] { go.transform.localScale.x, go.transform.localScale.y, go.transform.localScale.z }
                }
            };

            for (int i = 0; i < go.transform.childCount; i++)
            {
                var child = go.transform.GetChild(i);
                if (_scanner.IncludeInactive || child.gameObject.activeInHierarchy)
                {
                    dto.Children.Add(_scanner.GetOrCreateUUID(child.gameObject));
                }
            }

            return dto;
        }
    }
}
