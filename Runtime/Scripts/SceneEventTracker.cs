using System;
using System.Collections.Generic;
using UnityEngine;
using LiveLink.Network;

namespace LiveLink
{
    /// <summary>
    /// Tracks scene events for incremental understanding.
    /// Records object creation, destruction, and property changes.
    /// </summary>
    public class SceneEventTracker : MonoBehaviour
    {
        private const int MAX_EVENTS = 1000;
        private const float TRANSFORM_CHANGE_THRESHOLD = 0.001f;

        private readonly List<SceneEventDTO> _events = new List<SceneEventDTO>();
        private readonly Dictionary<int, Vector3> _lastPositions = new Dictionary<int, Vector3>();
        private readonly Dictionary<int, Quaternion> _lastRotations = new Dictionary<int, Quaternion>();
        private readonly Dictionary<int, Vector3> _lastScales = new Dictionary<int, Vector3>();
        private readonly Dictionary<int, bool> _lastActiveStates = new Dictionary<int, bool>();
        private readonly Dictionary<int, string> _lastNames = new Dictionary<int, string>();
        private readonly Dictionary<int, int> _lastParentIds = new Dictionary<int, int>();

        private int _eventCounter = 0;
        private bool _isTracking = true;

        /// <summary>
        /// Gets the current number of stored events.
        /// </summary>
        public int EventCount => _events.Count;

        /// <summary>
        /// Gets or sets whether event tracking is enabled.
        /// </summary>
        public bool IsTracking
        {
            get => _isTracking;
            set => _isTracking = value;
        }

        /// <summary>
        /// Gets the maximum number of events to store.
        /// </summary>
        public int MaxEvents => MAX_EVENTS;

        /// <summary>
        /// Registers a GameObject for tracking.
        /// </summary>
        public void RegisterGameObject(GameObject obj)
        {
            if (obj == null) return;

            int instanceId = obj.GetInstanceID();
            var transform = obj.transform;

            _lastPositions[instanceId] = transform.position;
            _lastRotations[instanceId] = transform.rotation;
            _lastScales[instanceId] = transform.localScale;
            _lastActiveStates[instanceId] = obj.activeSelf;
            _lastNames[instanceId] = obj.name;
            _lastParentIds[instanceId] = transform.parent != null ? transform.parent.GetInstanceID() : 0;
        }

        /// <summary>
        /// Unregisters a GameObject from tracking.
        /// </summary>
        public void UnregisterGameObject(GameObject obj)
        {
            if (obj == null) return;

            int instanceId = obj.GetInstanceID();
            _lastPositions.Remove(instanceId);
            _lastRotations.Remove(instanceId);
            _lastScales.Remove(instanceId);
            _lastActiveStates.Remove(instanceId);
            _lastNames.Remove(instanceId);
            _lastParentIds.Remove(instanceId);
        }

        /// <summary>
        /// Records an object creation event.
        /// </summary>
        public void RecordObjectCreated(GameObject obj)
        {
            if (!_isTracking || obj == null) return;

            RegisterGameObject(obj);

            var eventData = new Newtonsoft.Json.Linq.JObject
            {
                ["instance_id"] = obj.GetInstanceID(),
                ["name"] = obj.name,
                ["scene"] = obj.scene.name,
                ["position"] = new Newtonsoft.Json.Linq.JArray(obj.transform.position.x, obj.transform.position.y, obj.transform.position.z),
                ["parent_id"] = obj.transform.parent != null ? obj.transform.parent.GetInstanceID() : 0
            };

            AddEvent(SceneEventType.ObjectCreated, eventData);
        }

        /// <summary>
        /// Records an object destruction event.
        /// </summary>
        public void RecordObjectDestroyed(GameObject obj)
        {
            if (!_isTracking || obj == null) return;

            var eventData = new Newtonsoft.Json.Linq.JObject
            {
                ["instance_id"] = obj.GetInstanceID(),
                ["name"] = obj.name,
                ["scene"] = obj.scene.name,
                ["parent_id"] = obj.transform.parent != null ? obj.transform.parent.GetInstanceID() : 0
            };

            AddEvent(SceneEventType.ObjectDestroyed, eventData);
            UnregisterGameObject(obj);
        }

        /// <summary>
        /// Records a parent change event.
        /// </summary>
        public void RecordParentChanged(GameObject obj, Transform oldParent, Transform newParent)
        {
            if (!_isTracking || obj == null) return;

            int instanceId = obj.GetInstanceID();
            int oldParentId = oldParent != null ? oldParent.GetInstanceID() : 0;
            int newParentId = newParent != null ? newParent.GetInstanceID() : 0;

            if (_lastParentIds.TryGetValue(instanceId, out int lastParentId) && lastParentId == newParentId)
                return;

            _lastParentIds[instanceId] = newParentId;

            var eventData = new Newtonsoft.Json.Linq.JObject
            {
                ["instance_id"] = obj.GetInstanceID(),
                ["name"] = obj.name,
                ["old_parent_id"] = oldParentId,
                ["new_parent_id"] = newParentId,
                ["old_parent_name"] = oldParent != null ? oldParent.name : "None",
                ["new_parent_name"] = newParent != null ? newParent.name : "None"
            };

            AddEvent(SceneEventType.ObjectParentChanged, eventData);
        }

        /// <summary>
        /// Records a transform change event.
        /// </summary>
        public void RecordTransformChanged(GameObject obj)
        {
            if (!_isTracking || obj == null) return;

            int instanceId = obj.GetInstanceID();
            var transform = obj.transform;

            bool positionChanged = false;
            bool rotationChanged = false;
            bool scaleChanged = false;

            if (_lastPositions.TryGetValue(instanceId, out Vector3 lastPos))
            {
                positionChanged = Vector3.Distance(lastPos, transform.position) > TRANSFORM_CHANGE_THRESHOLD;
            }
            else
            {
                positionChanged = true;
            }

            if (_lastRotations.TryGetValue(instanceId, out Quaternion lastRot))
            {
                rotationChanged = Quaternion.Angle(lastRot, transform.rotation) > TRANSFORM_CHANGE_THRESHOLD;
            }
            else
            {
                rotationChanged = true;
            }

            if (_lastScales.TryGetValue(instanceId, out Vector3 lastScale))
            {
                scaleChanged = Vector3.Distance(lastScale, transform.localScale) > TRANSFORM_CHANGE_THRESHOLD;
            }
            else
            {
                scaleChanged = true;
            }

            if (!positionChanged && !rotationChanged && !scaleChanged)
                return;

            _lastPositions[instanceId] = transform.position;
            _lastRotations[instanceId] = transform.rotation;
            _lastScales[instanceId] = transform.localScale;

            var eventData = new Newtonsoft.Json.Linq.JObject
            {
                ["instance_id"] = obj.GetInstanceID(),
                ["name"] = obj.name,
                ["position"] = new Newtonsoft.Json.Linq.JArray(transform.position.x, transform.position.y, transform.position.z),
                ["rotation"] = new Newtonsoft.Json.Linq.JArray(transform.rotation.x, transform.rotation.y, transform.rotation.z, transform.rotation.w),
                ["scale"] = new Newtonsoft.Json.Linq.JArray(transform.localScale.x, transform.localScale.y, transform.localScale.z)
            };

            AddEvent(SceneEventType.ObjectTransformChanged, eventData);
        }

        /// <summary>
        /// Records an active state change event.
        /// </summary>
        public void RecordActiveChanged(GameObject obj)
        {
            if (!_isTracking || obj == null) return;

            int instanceId = obj.GetInstanceID();
            bool newActive = obj.activeSelf;

            if (_lastActiveStates.TryGetValue(instanceId, out bool lastActive) && lastActive == newActive)
                return;

            _lastActiveStates[instanceId] = newActive;

            var eventData = new Newtonsoft.Json.Linq.JObject
            {
                ["instance_id"] = obj.GetInstanceID(),
                ["name"] = obj.name,
                ["old_active"] = lastActive,
                ["new_active"] = newActive,
                ["active_in_hierarchy"] = obj.activeInHierarchy
            };

            AddEvent(SceneEventType.ObjectActiveChanged, eventData);
        }

        /// <summary>
        /// Records a name change event.
        /// </summary>
        public void RecordNameChanged(GameObject obj, string oldName)
        {
            if (!_isTracking || obj == null) return;

            int instanceId = obj.GetInstanceID();
            string newName = obj.name;

            if (_lastNames.TryGetValue(instanceId, out string lastName) && lastName == newName)
                return;

            _lastNames[instanceId] = newName;

            var eventData = new Newtonsoft.Json.Linq.JObject
            {
                ["instance_id"] = obj.GetInstanceID(),
                ["old_name"] = oldName,
                ["new_name"] = newName
            };

            AddEvent(SceneEventType.ObjectNameChanged, eventData);
        }

        /// <summary>
        /// Records a component added event.
        /// </summary>
        public void RecordComponentAdded(Component component)
        {
            if (!_isTracking || component == null) return;

            var eventData = new Newtonsoft.Json.Linq.JObject
            {
                ["component_instance_id"] = component.GetInstanceID(),
                ["component_type"] = component.GetType().FullName,
                ["game_object_instance_id"] = component.gameObject.GetInstanceID(),
                ["game_object_name"] = component.gameObject.name
            };

            AddEvent(SceneEventType.ComponentAdded, eventData);
        }

        /// <summary>
        /// Records a component removed event.
        /// </summary>
        public void RecordComponentRemoved(Component component)
        {
            if (!_isTracking || component == null) return;

            var eventData = new Newtonsoft.Json.Linq.JObject
            {
                ["component_instance_id"] = component.GetInstanceID(),
                ["component_type"] = component.GetType().FullName,
                ["game_object_instance_id"] = component.gameObject.GetInstanceID(),
                ["game_object_name"] = component.gameObject.name
            };

            AddEvent(SceneEventType.ComponentRemoved, eventData);
        }

        /// <summary>
        /// Records a component enabled changed event.
        /// </summary>
        public void RecordComponentEnabledChanged(Component component, bool newEnabled)
        {
            if (!_isTracking || component == null) return;

            var behaviour = component as Behaviour;
            if (behaviour == null) return;

            var eventData = new Newtonsoft.Json.Linq.JObject
            {
                ["component_instance_id"] = component.GetInstanceID(),
                ["component_type"] = component.GetType().FullName,
                ["game_object_instance_id"] = component.gameObject.GetInstanceID(),
                ["game_object_name"] = component.gameObject.name,
                ["old_enabled"] = !newEnabled,
                ["new_enabled"] = newEnabled
            };

            AddEvent(SceneEventType.ComponentEnabledChanged, eventData);
        }

        /// <summary>
        /// Records a scene loaded event.
        /// </summary>
        public void RecordSceneLoaded(UnityEngine.SceneManagement.Scene scene)
        {
            if (!_isTracking) return;

            var eventData = new Newtonsoft.Json.Linq.JObject
            {
                ["scene_name"] = scene.name,
                ["scene_path"] = scene.path,
                ["build_index"] = scene.buildIndex,
                ["is_loaded"] = scene.isLoaded
            };

            AddEvent(SceneEventType.SceneLoaded, eventData);
        }

        /// <summary>
        /// Records a scene unloaded event.
        /// </summary>
        public void RecordSceneUnloaded(UnityEngine.SceneManagement.Scene scene)
        {
            if (!_isTracking) return;

            var eventData = new Newtonsoft.Json.Linq.JObject
            {
                ["scene_name"] = scene.name,
                ["scene_path"] = scene.path,
                ["build_index"] = scene.buildIndex
            };

            AddEvent(SceneEventType.SceneUnloaded, eventData);
        }

        /// <summary>
        /// Gets recent events.
        /// </summary>
        public RecentEventsDTO GetRecentEvents(int count = 50)
        {
            count = Mathf.Clamp(count, 1, MAX_EVENTS);
            int startIndex = Mathf.Max(0, _events.Count - count);

            var recentEvents = new List<SceneEventDTO>();
            for (int i = startIndex; i < _events.Count; i++)
            {
                recentEvents.Add(_events[i]);
            }

            return new RecentEventsDTO
            {
                Count = recentEvents.Count,
                MaxEvents = MAX_EVENTS,
                Events = recentEvents
            };
        }

        /// <summary>
        /// Clears all events.
        /// </summary>
        public void ClearEvents()
        {
            _events.Clear();
            _eventCounter = 0;
        }

        /// <summary>
        /// Adds an event to the tracker.
        /// </summary>
        private void AddEvent(SceneEventType eventType, Newtonsoft.Json.Linq.JObject data)
        {
            var evt = new SceneEventDTO
            {
                EventId = (++_eventCounter).ToString(),
                EventType = eventType.ToString(),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                GameTime = Time.time,
                Data = data
            };

            _events.Add(evt);

            // Trim to max events
            while (_events.Count > MAX_EVENTS)
            {
                _events.RemoveAt(0);
            }
        }

        private void Awake()
        {
            // Register for scene events
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
            UnityEngine.SceneManagement.SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        private void OnDestroy()
        {
            // Unregister from scene events
            UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;
            UnityEngine.SceneManagement.SceneManager.sceneUnloaded -= OnSceneUnloaded;
        }

        private void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            RecordSceneLoaded(scene);
        }

        private void OnSceneUnloaded(UnityEngine.SceneManagement.Scene scene)
        {
            RecordSceneUnloaded(scene);
        }
    }
}
