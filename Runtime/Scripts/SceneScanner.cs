using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using LiveLink.Network;

namespace LiveLink
{
    /// <summary>
    /// Defines the scope of scene scanning.
    /// </summary>
    public enum ScanScope
    {
        /// <summary>
        /// Scan the entire active scene.
        /// </summary>
        WholeScene,

        /// <summary>
        /// Only scan children of a specific target object.
        /// </summary>
        TargetObjectOnly
    }

    /// <summary>
    /// Scans the Unity scene hierarchy and converts it to serializable DTOs.
    /// Supports both full scene scanning and targeted branch scanning.
    /// </summary>
    public class SceneScanner
    {
        private readonly Dictionary<int, string> _instanceIdToUuid = new Dictionary<int, string>();
        private readonly Dictionary<string, GameObject> _uuidToGameObject = new Dictionary<string, GameObject>();
        private readonly Dictionary<string, Vector3> _lastPositions = new Dictionary<string, Vector3>();
        private readonly Dictionary<string, Quaternion> _lastRotations = new Dictionary<string, Quaternion>();
        
        private ScanScope _scope = ScanScope.WholeScene;
        private Transform _targetRoot;
        private bool _includeInactive = false;
        private float _deltaThreshold = 0.001f;

        /// <summary>
        /// Gets the current scan scope.
        /// </summary>
        public ScanScope Scope
        {
            get => _scope;
            set => _scope = value;
        }

        /// <summary>
        /// Gets or sets the target root for TargetObjectOnly scope.
        /// </summary>
        public Transform TargetRoot
        {
            get => _targetRoot;
            set => _targetRoot = value;
        }

        /// <summary>
        /// Gets or sets whether to include inactive objects.
        /// </summary>
        public bool IncludeInactive
        {
            get => _includeInactive;
            set => _includeInactive = value;
        }

        /// <summary>
        /// Gets or sets the distance threshold for delta sync.
        /// </summary>
        public float DeltaThreshold
        {
            get => _deltaThreshold;
            set => _deltaThreshold = value;
        }

        /// <summary>
        /// Gets the UUID-to-GameObject lookup dictionary.
        /// </summary>
        public IReadOnlyDictionary<string, GameObject> UUIDToGameObject => _uuidToGameObject;

        /// <summary>
        /// Gets the UUID for a GameObject, creating one if necessary.
        /// </summary>
        public string GetOrCreateUUID(GameObject obj)
        {
            if (obj == null) return null;

            int instanceId = obj.GetInstanceID();
            if (_instanceIdToUuid.TryGetValue(instanceId, out string uuid))
            {
                return uuid;
            }

            // Generate new UUID
            uuid = System.Guid.NewGuid().ToString("N").Substring(0, 12);
            _instanceIdToUuid[instanceId] = uuid;
            _uuidToGameObject[uuid] = obj;
            return uuid;
        }

        /// <summary>
        /// Gets a GameObject by its UUID.
        /// </summary>
        public GameObject GetGameObjectByUUID(string uuid)
        {
            if (string.IsNullOrEmpty(uuid)) return null;
            _uuidToGameObject.TryGetValue(uuid, out GameObject obj);
            return obj;
        }

        /// <summary>
        /// Registers a GameObject with a specific UUID.
        /// </summary>
        public void RegisterWithUUID(GameObject obj, string uuid)
        {
            if (obj == null || string.IsNullOrEmpty(uuid)) return;

            int instanceId = obj.GetInstanceID();
            _instanceIdToUuid[instanceId] = uuid;
            _uuidToGameObject[uuid] = obj;
        }

        /// <summary>
        /// Removes a GameObject from tracking.
        /// </summary>
        public void Unregister(GameObject obj)
        {
            if (obj == null) return;

            int instanceId = obj.GetInstanceID();
            if (_instanceIdToUuid.TryGetValue(instanceId, out string uuid))
            {
                _instanceIdToUuid.Remove(instanceId);
                _uuidToGameObject.Remove(uuid);
                _lastPositions.Remove(uuid);
                _lastRotations.Remove(uuid);
            }
        }

        /// <summary>
        /// Clears all tracking data.
        /// </summary>
        public void Clear()
        {
            _instanceIdToUuid.Clear();
            _uuidToGameObject.Clear();
            _lastPositions.Clear();
            _lastRotations.Clear();
        }

        /// <summary>
        /// Scans the scene and returns all objects as DTOs.
        /// </summary>
        public SceneDumpPacket ScanFullScene()
        {
            var packet = new SceneDumpPacket();
            packet.Payload.SceneName = SceneManager.GetActiveScene().name;

            var rootObjects = GetRootObjects();
            var allObjects = new List<SceneObjectDTO>();

            foreach (var root in rootObjects)
            {
                ScanTransformRecursive(root, null, allObjects);
            }

            packet.Payload.Objects = allObjects;
            packet.Payload.ObjectCount = allObjects.Count;

            return packet;
        }

        /// <summary>
        /// Scans for changes since the last scan (delta sync).
        /// </summary>
        public SyncPacket ScanDelta()
        {
            var packet = new SyncPacket { IsDelta = true };
            var changedObjects = new List<SceneObjectDTO>();

            var rootObjects = GetRootObjects();

            foreach (var root in rootObjects)
            {
                ScanDeltaRecursive(root, changedObjects);
            }

            packet.Objects = changedObjects;
            return packet;
        }

        /// <summary>
        /// Gets root objects based on current scope.
        /// </summary>
        private List<Transform> GetRootObjects()
        {
            var result = new List<Transform>();

            if (_scope == ScanScope.TargetObjectOnly && _targetRoot != null)
            {
                result.Add(_targetRoot);
            }
            else
            {
                // Whole scene mode
                var scene = SceneManager.GetActiveScene();
                var rootGameObjects = scene.GetRootGameObjects();
                foreach (var go in rootGameObjects)
                {
                    if (_includeInactive || go.activeInHierarchy)
                    {
                        result.Add(go.transform);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Recursively scans transforms and builds DTOs.
        /// </summary>
        private void ScanTransformRecursive(Transform transform, string parentUuid, List<SceneObjectDTO> results)
        {
            if (transform == null) return;
            if (!_includeInactive && !transform.gameObject.activeInHierarchy) return;

            var dto = CreateDTO(transform, parentUuid);
            results.Add(dto);

            // Update last known positions
            _lastPositions[dto.UUID] = transform.position;
            _lastRotations[dto.UUID] = transform.rotation;

            // Scan children
            for (int i = 0; i < transform.childCount; i++)
            {
                var child = transform.GetChild(i);
                if (_includeInactive || child.gameObject.activeInHierarchy)
                {
                    dto.Children.Add(GetOrCreateUUID(child.gameObject));
                    ScanTransformRecursive(child, dto.UUID, results);
                }
            }
        }

        /// <summary>
        /// Recursively scans for changes.
        /// </summary>
        private void ScanDeltaRecursive(Transform transform, List<SceneObjectDTO> changedObjects)
        {
            if (transform == null) return;
            if (!_includeInactive && !transform.gameObject.activeInHierarchy) return;

            string uuid = GetOrCreateUUID(transform.gameObject);
            bool hasChanged = false;

            // Check if position changed
            if (_lastPositions.TryGetValue(uuid, out Vector3 lastPos))
            {
                if (Vector3.Distance(transform.position, lastPos) > _deltaThreshold)
                {
                    hasChanged = true;
                }
            }
            else
            {
                // New object
                hasChanged = true;
            }

            // Check if rotation changed
            if (!hasChanged && _lastRotations.TryGetValue(uuid, out Quaternion lastRot))
            {
                if (Quaternion.Angle(transform.rotation, lastRot) > _deltaThreshold)
                {
                    hasChanged = true;
                }
            }

            if (hasChanged)
            {
                string parentUuid = transform.parent != null ? GetOrCreateUUID(transform.parent.gameObject) : null;
                var dto = CreateDTO(transform, parentUuid);
                changedObjects.Add(dto);

                _lastPositions[uuid] = transform.position;
                _lastRotations[uuid] = transform.rotation;
            }

            // Recurse children
            for (int i = 0; i < transform.childCount; i++)
            {
                ScanDeltaRecursive(transform.GetChild(i), changedObjects);
            }
        }

        /// <summary>
        /// Creates a DTO from a transform.
        /// </summary>
        private SceneObjectDTO CreateDTO(Transform transform, string parentUuid)
        {
            var go = transform.gameObject;
            var uuid = GetOrCreateUUID(go);

            return new SceneObjectDTO
            {
                UUID = uuid,
                ParentUUID = parentUuid,
                Name = go.name,
                Active = go.activeSelf,
                Layer = go.layer,
                Tag = go.tag,
                Transform = new TransformDTO
                {
                    Position = new float[] { transform.position.x, transform.position.y, transform.position.z },
                    Rotation = new float[] { transform.rotation.x, transform.rotation.y, transform.rotation.z, transform.rotation.w },
                    Scale = new float[] { transform.localScale.x, transform.localScale.y, transform.localScale.z }
                }
            };
        }

        /// <summary>
        /// Cleans up references to destroyed objects.
        /// </summary>
        public void CleanupDestroyedObjects()
        {
            var toRemove = new List<string>();

            foreach (var kvp in _uuidToGameObject)
            {
                if (kvp.Value == null)
                {
                    toRemove.Add(kvp.Key);
                }
            }

            foreach (var uuid in toRemove)
            {
                _uuidToGameObject.Remove(uuid);
                _lastPositions.Remove(uuid);
                _lastRotations.Remove(uuid);

                // Also clean instanceId mapping
                int? instanceIdToRemove = null;
                foreach (var kvp in _instanceIdToUuid)
                {
                    if (kvp.Value == uuid)
                    {
                        instanceIdToRemove = kvp.Key;
                        break;
                    }
                }
                if (instanceIdToRemove.HasValue)
                {
                    _instanceIdToUuid.Remove(instanceIdToRemove.Value);
                }
            }
        }
    }
}
