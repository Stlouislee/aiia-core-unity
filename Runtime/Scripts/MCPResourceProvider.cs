using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using LiveLink.Network;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LiveLink
{
    /// <summary>
    /// Provides resource data for MCP resource requests.
    /// Handles the new unity:// URI scheme for scene, hierarchy, game objects,
    /// components, selection, and event resources.
    /// </summary>
    public class MCPResourceProvider
    {
        private readonly LiveLinkManager _manager;
        private readonly SceneEventTracker _eventTracker;

        /// <summary>
        /// BindingFlags used to discover public serializable fields on components.
        /// </summary>
        private const BindingFlags SERIALIZED_FLAGS =
            BindingFlags.Instance | BindingFlags.Public;

        public MCPResourceProvider(LiveLinkManager manager, SceneEventTracker eventTracker)
        {
            _manager = manager;
            _eventTracker = eventTracker;
        }

        #region Resource Templates

        /// <summary>
        /// Returns MCP resource template definitions for resources/list.
        /// </summary>
        public List<object> GetResourceTemplates()
        {
            return new List<object>
            {
                new
                {
                    uriTemplate = "unity://scene/active",
                    name = "Active Scene Info",
                    mimeType = "application/json",
                    description = "Basic information about the active Unity scene including name, root count, time, render pipeline, etc."
                },
                new
                {
                    uriTemplate = "unity://scene/hierarchy?root=/&depth=2",
                    name = "Scene Hierarchy",
                    mimeType = "application/json",
                    description = "Scene hierarchy tree with configurable root path and depth. Query params: root (path, default '/'), depth (int, default 2)."
                },
                new
                {
                    uriTemplate = "unity://go/{instanceId}",
                    name = "GameObject Metadata",
                    mimeType = "application/json",
                    description = "Metadata for a specific GameObject: name, tag, layer, active, parent, children, component count."
                },
                new
                {
                    uriTemplate = "unity://go/{instanceId}/components",
                    name = "GameObject Components",
                    mimeType = "application/json",
                    description = "List of all components on a GameObject with their types, instance IDs, and enabled states."
                },
                new
                {
                    uriTemplate = "unity://component/{instanceId}/{componentType}",
                    name = "Component Snapshot",
                    mimeType = "application/json",
                    description = "Snapshot of a specific component's public fields and properties."
                },
                new
                {
                    uriTemplate = "unity://selection",
                    name = "Current Selection",
                    mimeType = "application/json",
                    description = "Currently selected objects in the Unity Editor."
                },
                new
                {
                    uriTemplate = "unity://events/recent",
                    name = "Recent Events",
                    mimeType = "application/json",
                    description = "Recent scene events (create, delete, property changes) for incremental understanding. Query param: count (int, default 50)."
                }
            };
        }

        #endregion

        #region Resource Routing

        /// <summary>
        /// Dispatches a resource read request to the appropriate handler.
        /// Returns null if the URI is not recognized.
        /// </summary>
        public object ReadResource(string uri)
        {
            if (string.IsNullOrEmpty(uri))
                return null;

            // Parse query parameters
            string path = uri;
            Dictionary<string, string> queryParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            int queryIndex = uri.IndexOf('?');
            if (queryIndex >= 0)
            {
                path = uri.Substring(0, queryIndex);
                string queryString = uri.Substring(queryIndex + 1);
                foreach (var pair in queryString.Split('&'))
                {
                    var kv = pair.Split(new[] { '=' }, 2);
                    if (kv.Length == 2)
                    {
                        queryParams[Uri.UnescapeDataString(kv[0])] = Uri.UnescapeDataString(kv[1]);
                    }
                    else if (kv.Length == 1)
                    {
                        queryParams[Uri.UnescapeDataString(kv[0])] = "";
                    }
                }
            }

            // Normalize: remove trailing slash
            path = path.TrimEnd('/');

            // Route
            if (path == "unity://scene/active")
                return ReadSceneActive();

            if (path == "unity://scene/hierarchy")
                return ReadSceneHierarchy(queryParams);

            if (path == "unity://selection")
                return ReadSelection();

            if (path == "unity://events/recent")
                return ReadRecentEvents(queryParams);

            // unity://go/{instanceId}/components
            if (path.StartsWith("unity://go/") && path.EndsWith("/components"))
            {
                string segment = path.Substring("unity://go/".Length);
                segment = segment.Substring(0, segment.Length - "/components".Length);
                if (int.TryParse(segment, out int instanceId))
                    return ReadGameObjectComponents(instanceId);
                return null;
            }

            // unity://go/{instanceId}
            if (path.StartsWith("unity://go/"))
            {
                string segment = path.Substring("unity://go/".Length);
                if (int.TryParse(segment, out int instanceId))
                    return ReadGameObjectMetadata(instanceId);
                return null;
            }

            // unity://component/{instanceId}/{componentType}
            if (path.StartsWith("unity://component/"))
            {
                string remainder = path.Substring("unity://component/".Length);
                int slashIdx = remainder.IndexOf('/');
                if (slashIdx > 0)
                {
                    string idStr = remainder.Substring(0, slashIdx);
                    string componentType = remainder.Substring(slashIdx + 1);
                    if (int.TryParse(idStr, out int instanceId))
                        return ReadComponentSnapshot(instanceId, componentType);
                }
                return null;
            }

            return null;
        }

        #endregion

        #region unity://scene/active

        private SceneInfoDTO ReadSceneActive()
        {
            var scene = SceneManager.GetActiveScene();
            var rootObjects = scene.GetRootGameObjects();

            // Count all objects recursively
            int totalCount = 0;
            foreach (var root in rootObjects)
            {
                totalCount += CountTransformsRecursive(root.transform);
            }

            // Detect render pipeline
            string renderPipeline = "Built-in";
            var rpAsset = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            if (rpAsset != null)
            {
                string rpTypeName = rpAsset.GetType().Name;
                if (rpTypeName.Contains("Universal") || rpTypeName.Contains("URP"))
                    renderPipeline = "URP";
                else if (rpTypeName.Contains("HighDefinition") || rpTypeName.Contains("HDRP"))
                    renderPipeline = "HDRP";
                else
                    renderPipeline = rpTypeName;
            }

            return new SceneInfoDTO
            {
                SceneName = scene.name,
                ScenePath = scene.path,
                IsLoaded = scene.isLoaded,
                IsDirty = scene.isDirty,
                RootCount = rootObjects.Length,
                ObjectCount = totalCount,
                RenderPipeline = renderPipeline,
                TimeScale = Time.timeScale,
                GameTime = Time.time,
                RealTime = Time.realtimeSinceStartup,
                FrameCount = Time.frameCount,
                QualityLevel = QualitySettings.GetQualityLevel(),
                Platform = Application.platform.ToString(),
                UnityVersion = Application.unityVersion
            };
        }

        private int CountTransformsRecursive(Transform t)
        {
            int count = 1;
            for (int i = 0; i < t.childCount; i++)
            {
                count += CountTransformsRecursive(t.GetChild(i));
            }
            return count;
        }

        #endregion

        #region unity://scene/hierarchy

        private List<HierarchyNodeDTO> ReadSceneHierarchy(Dictionary<string, string> queryParams)
        {
            int depth = 2;
            string rootPath = "/";

            if (queryParams.TryGetValue("depth", out string depthStr) && int.TryParse(depthStr, out int parsedDepth))
            {
                depth = Mathf.Clamp(parsedDepth, 1, 50);
            }

            if (queryParams.TryGetValue("root", out string rootArg))
            {
                rootPath = rootArg;
            }

            var scene = SceneManager.GetActiveScene();
            var rootGameObjects = scene.GetRootGameObjects();
            var result = new List<HierarchyNodeDTO>();

            if (rootPath == "/" || rootPath == "")
            {
                // Return all root objects
                foreach (var go in rootGameObjects)
                {
                    result.Add(BuildHierarchyNode(go.transform, 0, depth));
                }
            }
            else
            {
                // Find the specified root
                string cleanPath = rootPath.TrimStart('/');
                Transform target = FindTransformByPath(rootGameObjects, cleanPath);
                if (target != null)
                {
                    result.Add(BuildHierarchyNode(target, 0, depth));
                }
            }

            return result;
        }

        private HierarchyNodeDTO BuildHierarchyNode(Transform transform, int currentDepth, int maxDepth)
        {
            var go = transform.gameObject;
            var node = new HierarchyNodeDTO
            {
                InstanceId = go.GetInstanceID(),
                Name = go.name,
                Active = go.activeSelf,
                Layer = go.layer,
                Tag = go.tag,
                IsStatic = go.isStatic,
                Depth = currentDepth,
                ChildCount = transform.childCount
            };

            if (currentDepth < maxDepth)
            {
                for (int i = 0; i < transform.childCount; i++)
                {
                    node.Children.Add(BuildHierarchyNode(transform.GetChild(i), currentDepth + 1, maxDepth));
                }
            }

            return node;
        }

        /// <summary>
        /// Finds a Transform by a slash-separated path (e.g. "Player/Body/Head").
        /// </summary>
        private Transform FindTransformByPath(GameObject[] roots, string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            string[] parts = path.Split('/');
            if (parts.Length == 0) return null;

            // Find the matching root
            Transform current = null;
            foreach (var go in roots)
            {
                if (go.name == parts[0])
                {
                    current = go.transform;
                    break;
                }
            }

            if (current == null) return null;

            // Walk the path
            for (int i = 1; i < parts.Length; i++)
            {
                Transform child = current.Find(parts[i]);
                if (child == null) return null;
                current = child;
            }

            return current;
        }

        #endregion

        #region unity://go/{instanceId}

        private GameObjectMetadataDTO ReadGameObjectMetadata(int instanceId)
        {
            var go = FindGameObjectByInstanceId(instanceId);
            if (go == null) return null;

            var transform = go.transform;
            var dto = new GameObjectMetadataDTO
            {
                InstanceId = instanceId,
                Name = go.name,
                Active = go.activeSelf,
                ActiveInHierarchy = go.activeInHierarchy,
                IsStatic = go.isStatic,
                Layer = go.layer,
                Tag = go.tag,
                SceneName = go.scene.name,
                ComponentCount = go.GetComponents<Component>().Length,
                Transform = new TransformMetadataDTO
                {
                    Position = new[] { transform.position.x, transform.position.y, transform.position.z },
                    Rotation = new[] { transform.rotation.x, transform.rotation.y, transform.rotation.z, transform.rotation.w },
                    Scale = new[] { transform.lossyScale.x, transform.lossyScale.y, transform.lossyScale.z },
                    LocalPosition = new[] { transform.localPosition.x, transform.localPosition.y, transform.localPosition.z },
                    LocalRotation = new[] { transform.localRotation.x, transform.localRotation.y, transform.localRotation.z, transform.localRotation.w },
                    LocalScale = new[] { transform.localScale.x, transform.localScale.y, transform.localScale.z }
                }
            };

            // Parent info
            if (transform.parent != null)
            {
                dto.Parent = new ParentInfoDTO
                {
                    InstanceId = transform.parent.gameObject.GetInstanceID(),
                    Name = transform.parent.gameObject.name
                };
            }

            // Children info
            for (int i = 0; i < transform.childCount; i++)
            {
                var child = transform.GetChild(i);
                dto.Children.Add(new ChildInfoDTO
                {
                    InstanceId = child.gameObject.GetInstanceID(),
                    Name = child.gameObject.name,
                    Active = child.gameObject.activeSelf
                });
            }

            return dto;
        }

        #endregion

        #region unity://go/{instanceId}/components

        private ComponentListDTO ReadGameObjectComponents(int instanceId)
        {
            var go = FindGameObjectByInstanceId(instanceId);
            if (go == null) return null;

            var components = go.GetComponents<Component>();

            var dto = new ComponentListDTO
            {
                GameObjectInstanceId = instanceId,
                GameObjectName = go.name
            };

            foreach (var component in components)
            {
                if (component == null) continue; // Missing script

                var compType = component.GetType();
                bool enabled = true;
                if (component is Behaviour behaviour)
                {
                    enabled = behaviour.enabled;
                }
                else if (component is Renderer renderer)
                {
                    enabled = renderer.enabled;
                }
                else if (component is Collider collider)
                {
                    enabled = collider.enabled;
                }

                dto.Components.Add(new ComponentInfoDTO
                {
                    InstanceId = component.GetInstanceID(),
                    Type = compType.FullName,
                    ShortType = compType.Name,
                    Enabled = enabled
                });
            }

            return dto;
        }

        #endregion

        #region unity://component/{instanceId}/{componentType}

        private ComponentSnapshotDTO ReadComponentSnapshot(int instanceId, string componentType)
        {
            var go = FindGameObjectByInstanceId(instanceId);
            if (go == null) return null;

            var components = go.GetComponents<Component>();
            Component target = null;

            foreach (var comp in components)
            {
                if (comp == null) continue;
                if (comp.GetType().Name == componentType || comp.GetType().FullName == componentType)
                {
                    target = comp;
                    break;
                }
            }

            if (target == null) return null;

            var compType = target.GetType();
            bool enabled = true;
            if (target is Behaviour behaviour)
                enabled = behaviour.enabled;
            else if (target is Renderer renderer)
                enabled = renderer.enabled;
            else if (target is Collider collider)
                enabled = collider.enabled;

            var dto = new ComponentSnapshotDTO
            {
                InstanceId = target.GetInstanceID(),
                Type = compType.FullName,
                ShortType = compType.Name,
                Enabled = enabled
            };

            // Extract public fields
            var fields = compType.GetFields(SERIALIZED_FLAGS);
            foreach (var field in fields)
            {
                try
                {
                    object value = field.GetValue(target);
                    dto.Fields[field.Name] = SerializeFieldValue(value);
                }
                catch
                {
                    dto.Fields[field.Name] = "<error reading field>";
                }
            }

            // Extract public properties with getters (excludes indexers)
            var properties = compType.GetProperties(BindingFlags.Instance | BindingFlags.Public);
            foreach (var prop in properties)
            {
                // Skip indexers and write-only properties
                if (!prop.CanRead || prop.GetIndexParameters().Length > 0)
                    continue;

                // Skip certain noisy/dangerous properties
                if (IsSkippedProperty(prop.Name))
                    continue;

                try
                {
                    object value = prop.GetValue(target);
                    string key = prop.Name;
                    // Avoid overwriting fields with same name
                    if (!dto.Fields.ContainsKey(key))
                    {
                        dto.Fields[key] = SerializeFieldValue(value);
                    }
                }
                catch
                {
                    // Some properties throw on access (e.g. mesh on MeshFilter in some states)
                }
            }

            return dto;
        }

        /// <summary>
        /// Serializes a field value to a JSON-safe representation.
        /// </summary>
        private object SerializeFieldValue(object value)
        {
            if (value == null) return null;

            var type = value.GetType();

            // Primitives and strings
            if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal))
                return value;

            // Enums
            if (type.IsEnum)
                return value.ToString();

            // Unity types
            if (value is Vector2 v2) return new[] { v2.x, v2.y };
            if (value is Vector3 v3) return new[] { v3.x, v3.y, v3.z };
            if (value is Vector4 v4) return new[] { v4.x, v4.y, v4.z, v4.w };
            if (value is Quaternion q) return new[] { q.x, q.y, q.z, q.w };
            if (value is Color c) return new { r = c.r, g = c.g, b = c.b, a = c.a };
            if (value is Color32 c32) return new { r = (int)c32.r, g = (int)c32.g, b = (int)c32.b, a = (int)c32.a };
            if (value is Bounds b) return new { center = new[] { b.center.x, b.center.y, b.center.z }, size = new[] { b.size.x, b.size.y, b.size.z } };
            if (value is Rect rect) return new { x = rect.x, y = rect.y, width = rect.width, height = rect.height };
            if (value is Matrix4x4 m) return m.ToString();
            if (value is AnimationCurve) return "<AnimationCurve>";
            if (value is Gradient) return "<Gradient>";
            if (value is LayerMask lm) return lm.value;

            // Unity Object references
            if (value is UnityEngine.Object uObj)
            {
                if (uObj == null) return null;
                return new { instance_id = uObj.GetInstanceID(), name = uObj.name, type = uObj.GetType().Name };
            }

            // Arrays and lists
            if (type.IsArray)
            {
                var arr = (Array)value;
                if (arr.Length > 100) return $"<Array [{arr.Length}]>";
                var result = new object[arr.Length];
                for (int i = 0; i < arr.Length; i++)
                {
                    result[i] = SerializeFieldValue(arr.GetValue(i));
                }
                return result;
            }

            if (value is System.Collections.IList list)
            {
                if (list.Count > 100) return $"<List [{list.Count}]>";
                var result = new object[list.Count];
                for (int i = 0; i < list.Count; i++)
                {
                    result[i] = SerializeFieldValue(list[i]);
                }
                return result;
            }

            // Fallback: ToString
            return value.ToString();
        }

        /// <summary>
        /// Properties that are noisy, redundant, or may cause side effects when read.
        /// </summary>
        private bool IsSkippedProperty(string name)
        {
            switch (name)
            {
                // Unity Object base
                case "hideFlags":
                // Transform redundancies (we have a dedicated transform section)
                case "transform":
                case "gameObject":
                // Internal
                case "rigidbody":
                case "rigidbody2D":
                case "camera":
                case "light":
                case "animation":
                case "constantForce":
                case "renderer":
                case "audio":
                case "networkView":
                case "collider":
                case "collider2D":
                case "hingeJoint":
                case "particleSystem":
                    return true;
                default:
                    return false;
            }
        }

        #endregion

        #region unity://selection

        private SelectionDTO ReadSelection()
        {
            var dto = new SelectionDTO();

            #if UNITY_EDITOR
            var selectedObjects = UnityEditor.Selection.gameObjects;
            dto.Count = selectedObjects.Length;

            if (UnityEditor.Selection.activeGameObject != null)
            {
                var active = UnityEditor.Selection.activeGameObject;
                dto.ActiveObject = new SelectedObjectDTO
                {
                    InstanceId = active.GetInstanceID(),
                    Name = active.name,
                    SceneName = active.scene.name
                };
            }

            foreach (var go in selectedObjects)
            {
                if (go == null) continue;
                dto.Objects.Add(new SelectedObjectDTO
                {
                    InstanceId = go.GetInstanceID(),
                    Name = go.name,
                    SceneName = go.scene.name
                });
            }
            #else
            dto.Count = 0;
            #endif

            return dto;
        }

        #endregion

        #region unity://events/recent

        private RecentEventsDTO ReadRecentEvents(Dictionary<string, string> queryParams)
        {
            int count = 50;
            if (queryParams.TryGetValue("count", out string countStr) && int.TryParse(countStr, out int parsedCount))
            {
                count = parsedCount;
            }

            if (_eventTracker != null)
            {
                return _eventTracker.GetRecentEvents(count);
            }

            return new RecentEventsDTO
            {
                Count = 0,
                MaxEvents = 0,
                Events = new List<SceneEventDTO>()
            };
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Finds a GameObject by Unity instance ID in the active scene.
        /// </summary>
        private GameObject FindGameObjectByInstanceId(int instanceId)
        {
            // First try the scanner's tracked objects
            if (_manager.Scanner != null)
            {
                foreach (var kvp in _manager.Scanner.UUIDToGameObject)
                {
                    if (kvp.Value != null && kvp.Value.GetInstanceID() == instanceId)
                        return kvp.Value;
                }
            }

            // Fallback: search scene roots and recurse
            var scene = SceneManager.GetActiveScene();
            var roots = scene.GetRootGameObjects();
            foreach (var root in roots)
            {
                var found = FindInHierarchy(root.transform, instanceId);
                if (found != null) return found;
            }

            return null;
        }

        private GameObject FindInHierarchy(Transform parent, int instanceId)
        {
            if (parent.gameObject.GetInstanceID() == instanceId)
                return parent.gameObject;

            for (int i = 0; i < parent.childCount; i++)
            {
                var result = FindInHierarchy(parent.GetChild(i), instanceId);
                if (result != null) return result;
            }

            return null;
        }

        #endregion
    }
}
